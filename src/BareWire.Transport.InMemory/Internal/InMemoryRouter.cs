using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// Resolves the target queues for a <c>(exchange, routingKey)</c> pair against a sealed
/// <see cref="ExchangeRegistry"/> snapshot, mirroring AMQP routing semantics (Direct/Fanout/Topic,
/// exchange-to-exchange recursion with cycle protection, and the default exchange), and caches the
/// deduplicated, ordered result so repeated sends for the same pair pay for the traversal once.
/// </summary>
/// <remarks>
/// <para>
/// One instance is built from one immutable <see cref="ExchangeRegistry"/> snapshot and never rebinds
/// to another — the cache is therefore implicitly scoped to that snapshot; a topology redeploy that
/// produces a new registry is expected to construct a new router rather than mutate this one.
/// </para>
/// <para>
/// This type only resolves and reports routing outcomes — it does not itself decide what a caller does
/// with an <see cref="InMemoryRouteStatus.Unroutable"/> or <see cref="InMemoryRouteStatus.ExchangeNotFound"/>
/// result. Wiring this into the publish path is the responsibility of a later component.
/// </para>
/// </remarks>
internal sealed partial class InMemoryRouter
{
    /// <summary>The AMQP binding-key length limit, in UTF-8 bytes.</summary>
    internal const int MaxRoutingKeyBytes = 255;

    // Every UTF-16 char encodes to at most 3 UTF-8 bytes on its own (a surrogate PAIR of 2 chars
    // encodes to 4 bytes, i.e. 2 bytes/char average) — so a key of at most 85 chars can never exceed
    // 255 UTF-8 bytes, and the byte count never needs to be computed for it.
    private const int FastPathMaxChars = MaxRoutingKeyBytes / 3;

    // The unroutable-warning throttle window. Not user-configurable at this
    // stage; it may be promoted to an option later.
    private static readonly TimeSpan UnroutableLogWindow = TimeSpan.FromSeconds(60);

    private readonly ExchangeRegistry _registry;
    private readonly InMemoryTransportOptions _options;
    private readonly ILogger<InMemoryRouter> _logger;
    private readonly TimeProvider _timeProvider;

    // Opt-in — created only when an external Meter is supplied by the composition root, through the
    // adapter's single InMemoryTransportMetrics owner. null = no instrument, no cost.
    private readonly InMemoryTransportMetrics? _metrics;

    // Built once in the constructor from the immutable registry snapshot — never mutated afterwards.
    private readonly FrozenDictionary<string, ExchangeNode> _exchanges;

    // ConcurrentDictionary key is a ValueTuple<string,string> — no boxing, no allocation on a TryGetValue
    // hit. Holds only Routed and Unroutable outcomes for declared, non-default exchanges (the
    // default exchange is served from ExchangeNode.DefaultExchangeRoutes and never cached here).
    private readonly ConcurrentDictionary<(string Exchange, string RoutingKey), ImmutableArray<string>> _cache = new();
    private int _cacheCount;
    private long _unroutableCount;
    private long _logFailureCount;

    internal InMemoryRouter(
        ExchangeRegistry registry,
        InMemoryTransportOptions options,
        ILogger<InMemoryRouter> logger,
        InMemoryTransportMetrics? metrics = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _metrics = metrics;

        _exchanges = BuildExchangeIndex(registry);
        _defaultExchangeRoutes = _exchanges[ExchangeRegistry.DefaultExchangeName].DefaultExchangeRoutes!;
    }

    // The default exchange's frozen queueName -> [queueName] map, cached in a field so the per-message
    // default-exchange path costs a single dictionary lookup.
    private readonly FrozenDictionary<string, ImmutableArray<string>> _defaultExchangeRoutes;

    /// <summary>
    /// Routes on the default exchange: the queue named <paramref name="routingKey"/> when it is declared,
    /// otherwise <see cref="InMemoryRouteStatus.Unroutable"/>. Never touches the route cache, so arbitrary
    /// keys on the default exchange cannot evict hot routes on other exchanges.
    /// </summary>
    private InMemoryRouteResult RouteDefaultExchange(string routingKey)
    {
        if (routingKey.Length > FastPathMaxChars && Encoding.UTF8.GetByteCount(routingKey) > MaxRoutingKeyBytes)
        {
            return new InMemoryRouteResult(InMemoryRouteStatus.RoutingKeyTooLong, ImmutableArray<string>.Empty);
        }

        return _defaultExchangeRoutes.TryGetValue(routingKey, out ImmutableArray<string> queues)
            ? new InMemoryRouteResult(InMemoryRouteStatus.Routed, queues)
            : new InMemoryRouteResult(InMemoryRouteStatus.Unroutable, ImmutableArray<string>.Empty);
    }

    /// <summary>Gets the immutable topology snapshot this router was built from.</summary>
    internal ExchangeRegistry Registry => _registry;

    /// <summary>Gets the current number of entries in the route cache.</summary>
    /// <remarks>
    /// Reads the maintained counter via <see cref="Volatile"/> — never
    /// <c>ConcurrentDictionary{TKey,TValue}.Count</c>, which locks every internal segment and would
    /// undermine the zero-allocation, lock-free cache-hit path this property is used to verify.
    /// </remarks>
    internal int CacheCount => Volatile.Read(ref _cacheCount);

    /// <summary>Gets the configured route-cache capacity (<see cref="InMemoryTransportOptions.RouteCacheCapacity"/>).</summary>
    internal int CacheCapacity => _options.RouteCacheCapacity;

    /// <summary>Gets the total number of <see cref="ReportUnroutable"/> calls made against this router.</summary>
    internal long UnroutableCount => Interlocked.Read(ref _unroutableCount);

    /// <summary>
    /// Gets the number of metric or logger calls from this instance that threw and were suppressed — see
    /// the explicit catch-and-count guard in <see cref="ReportUnroutable"/>. Without this guard, a
    /// throwing logger there would unwind into the send path's per-message exception boundary and get
    /// mapped to <c>internal_error</c> for the very message already being reported as <c>unroutable</c>.
    /// </summary>
    internal long LogFailureCount => Interlocked.Read(ref _logFailureCount);

    /// <summary>
    /// Resolves the target queues for <paramref name="exchange"/> and <paramref name="routingKey"/>,
    /// serving a cached result when available and computing (and caching) a fresh one on a miss.
    /// </summary>
    /// <remarks>
    /// Check order: the routing-key length is checked in O(1) first; the cache is then consulted
    /// before anything else is validated, so a cache hit never pays for UTF-8 byte counting or an
    /// exchange existence check — both are deferred to a miss.
    /// </remarks>
    internal InMemoryRouteResult Route(string exchange, string routingKey)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(routingKey);

        if (routingKey.Length > MaxRoutingKeyBytes)
        {
            return new InMemoryRouteResult(InMemoryRouteStatus.RoutingKeyTooLong, ImmutableArray<string>.Empty);
        }

        if (exchange.Length == 0)
        {
            // The default exchange is never cached, so it bypasses the route-cache lookup entirely and
            // goes straight to its frozen queue-name map (one lookup per message instead of three).
            return RouteDefaultExchange(routingKey);
        }

        (string Exchange, string RoutingKey) key = (exchange, routingKey);
        if (_cache.TryGetValue(key, out ImmutableArray<string> cached))
        {
            return new InMemoryRouteResult(
                cached.IsEmpty ? InMemoryRouteStatus.Unroutable : InMemoryRouteStatus.Routed,
                cached);
        }

        if (routingKey.Length > FastPathMaxChars && Encoding.UTF8.GetByteCount(routingKey) > MaxRoutingKeyBytes)
        {
            return new InMemoryRouteResult(InMemoryRouteStatus.RoutingKeyTooLong, ImmutableArray<string>.Empty);
        }

        if (!_exchanges.TryGetValue(exchange, out ExchangeNode? node))
        {
            return new InMemoryRouteResult(InMemoryRouteStatus.ExchangeNotFound, ImmutableArray<string>.Empty);
        }

        ImmutableArray<string> resolved = ResolveQueues(node, routingKey);
        AddToCache(key, resolved);

        return new InMemoryRouteResult(
            resolved.IsEmpty ? InMemoryRouteStatus.Unroutable : InMemoryRouteStatus.Routed,
            resolved);
    }

    /// <summary>
    /// Adds <paramref name="queues"/> to the route cache under <paramref name="key"/>, clearing the
    /// cache first when it has reached <see cref="InMemoryTransportOptions.RouteCacheCapacity"/>.
    /// </summary>
    /// <remarks>
    /// Under concurrent misses the entry count may briefly exceed the configured capacity by up to the
    /// number of racing threads — the cache remains numerically bounded, never unbounded, and the next
    /// miss to observe the over-capacity count clears it. The count is incremented only after
    /// <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/> reports success (never via
    /// <c>GetOrAdd</c> with a factory, which can race and run the factory more than once), so the counter
    /// never overcounts relative to what was actually inserted since the last clear.
    /// </remarks>
    private void AddToCache((string Exchange, string RoutingKey) key, ImmutableArray<string> queues)
    {
        EnsureCacheCapacity();

        if (_cache.TryAdd(key, queues))
        {
            Interlocked.Increment(ref _cacheCount);
        }
    }

    private void EnsureCacheCapacity()
    {
        int current = Volatile.Read(ref _cacheCount);
        if (current < _options.RouteCacheCapacity)
        {
            return;
        }

        // Only the thread whose compare-exchange observes the count unchanged performs the clear —
        // avoids a Clear() race where two threads both wipe the (already-cleared) cache.
        if (Interlocked.CompareExchange(ref _cacheCount, 0, current) == current)
        {
            _cache.Clear();
        }
    }

    /// <summary>
    /// Resolves the queues reachable from <paramref name="sourceNode"/> for <paramref name="routingKey"/>
    /// via breadth-first search over exchange-to-exchange bindings, matching each hop's bindings against
    /// the SOURCE exchange's type (Direct/Fanout/Topic) while keeping the original routing key unchanged
    /// throughout — an exchange-to-exchange binding routes on its own key pattern, but the message it
    /// forwards still carries the caller's original key for matching at the destination.
    /// </summary>
    /// <remarks>
    /// The visited-exchange set is allocated only here, on a cache miss, and guarantees termination for
    /// a binding cycle (e.g. A→B→A): each exchange is enqueued at most once. The returned array is
    /// sorted with <see cref="StringComparer.Ordinal"/> so a later sender can reserve queue slots in a
    /// fixed order without re-sorting per message.
    /// </remarks>
    private ImmutableArray<string> ResolveQueues(ExchangeNode sourceNode, string routingKey)
    {
        var visitedExchanges = new HashSet<string>(StringComparer.Ordinal) { sourceNode.Name };
        var seenQueues = new HashSet<string>(StringComparer.Ordinal);
        List<string> queues = [];

        var frontier = new Queue<ExchangeNode>();
        frontier.Enqueue(sourceNode);

        while (frontier.Count > 0)
        {
            ExchangeNode current = frontier.Dequeue();

            foreach ((string bindingKey, string queueName) in current.QueueBindings)
            {
                if (MatchesBinding(current.Type, bindingKey, routingKey) && seenQueues.Add(queueName))
                {
                    queues.Add(queueName);
                }
            }

            foreach ((string bindingKey, string destinationExchangeName) in current.ExchangeBindings)
            {
                if (!MatchesBinding(current.Type, bindingKey, routingKey))
                {
                    continue;
                }

                if (visitedExchanges.Add(destinationExchangeName)
                    && _exchanges.TryGetValue(destinationExchangeName, out ExchangeNode? destinationNode))
                {
                    frontier.Enqueue(destinationNode);
                }
            }
        }

        if (queues.Count == 0)
        {
            return ImmutableArray<string>.Empty;
        }

        string[] sorted = [.. queues];
        Array.Sort(sorted, StringComparer.Ordinal);
        return ImmutableCollectionsMarshal.AsImmutableArray(sorted);
    }

    /// <summary>
    /// Reports that a message on <paramref name="exchange"/> with <paramref name="routingKey"/> matched
    /// no binding: logs a throttled <see cref="LogLevel.Warning"/> (exchange and routing key only — no
    /// message body, headers, or identifiers), increments <see cref="UnroutableCount"/> and the opt-in
    /// unroutable metric, and returns whether the message should be treated as confirmed.
    /// </summary>
    /// <remarks>
    /// Call only after <see cref="Route"/> returned <see cref="InMemoryRouteStatus.Unroutable"/> for the
    /// same <paramref name="exchange"/> — an undeclared exchange throws instead of accepting an arbitrary
    /// caller-supplied name into the log and the metric's <c>exchange</c> tag.
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> (the message is confirmed / silently dropped) unless
    /// <see cref="InMemoryTransportOptions.GuaranteedRouting"/> is enabled, in which case
    /// <see langword="false"/> (the message is not confirmed).
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="exchange"/> is not the default exchange and not declared in the topology.
    /// </exception>
    internal bool ReportUnroutable(string exchange, string routingKey)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(routingKey);

        if (!_exchanges.TryGetValue(exchange, out ExchangeNode? node))
        {
            throw new ArgumentException(
                $"'{exchange}' is not a declared exchange. ReportUnroutable must only be called after " +
                $"{nameof(Route)} returned {nameof(InMemoryRouteStatus)}.{nameof(InMemoryRouteStatus.Unroutable)} " +
                "for the same exchange.",
                nameof(exchange));
        }

        Interlocked.Increment(ref _unroutableCount);

        // Caught and counted explicitly, never left to propagate: this method is called from the send
        // path's per-message exception boundary (InMemorySender.ProcessMessage), which maps any escaping
        // exception to SendRejectionReason.InternalError for the same message already being reported as
        // unroutable here — a throwing metrics listener or logger would otherwise double-count one
        // rejection under two reasons.
        try
        {
            _metrics?.RecordExchangeRejected("unroutable", exchange);

            if (node.TryEnterLogWindow(_timeProvider, UnroutableLogWindow, out int suppressedCount))
            {
                // The routing key is logged as structured state, not string-interpolated into text —
                // a caller-controlled routing key may contain CR/LF, which a text-formatting sink (not
                // this logger) could render as forged extra log lines. The throttle above already bounds
                // the volume, so no further sanitization is applied here.
                LogUnroutable(_logger, exchange, routingKey, suppressedCount);
            }
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _logFailureCount);
        }

        return !_options.GuaranteedRouting;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "In-memory message unroutable: exchange '{Exchange}', routing key '{RoutingKey}' matched no binding. " +
        "{SuppressedCount} earlier occurrence(s) for this exchange were suppressed since the last warning.")]
    private static partial void LogUnroutable(ILogger logger, string exchange, string routingKey, int suppressedCount);

    private static bool MatchesBinding(ExchangeType sourceType, string bindingKey, string routingKey) => sourceType switch
    {
        ExchangeType.Direct => string.Equals(bindingKey, routingKey, StringComparison.Ordinal),
        ExchangeType.Fanout => true,
        ExchangeType.Topic => TopicMatcher.IsMatch(bindingKey, routingKey),
        // Headers/ConsistentHash are already rejected at topology-build time (InMemoryTopologyInterpreter).
        _ => false,
    };

    private static FrozenDictionary<string, ExchangeNode> BuildExchangeIndex(ExchangeRegistry registry)
    {
        var queueBindingsBySource = new Dictionary<string, List<(string RoutingKey, string QueueName)>>(StringComparer.Ordinal);
        foreach (ExchangeQueueBinding binding in registry.ExchangeQueueBindings)
        {
            if (!queueBindingsBySource.TryGetValue(binding.ExchangeName, out List<(string RoutingKey, string QueueName)>? list))
            {
                list = [];
                queueBindingsBySource[binding.ExchangeName] = list;
            }

            list.Add((binding.RoutingKey, binding.QueueName));
        }

        var exchangeBindingsBySource = new Dictionary<string, List<(string RoutingKey, string ExchangeName)>>(StringComparer.Ordinal);
        foreach (ExchangeExchangeBinding binding in registry.ExchangeExchangeBindings)
        {
            if (!exchangeBindingsBySource.TryGetValue(binding.SourceExchangeName, out List<(string RoutingKey, string ExchangeName)>? list))
            {
                list = [];
                exchangeBindingsBySource[binding.SourceExchangeName] = list;
            }

            list.Add((binding.RoutingKey, binding.DestinationExchangeName));
        }

        var nodes = new Dictionary<string, ExchangeNode>(StringComparer.Ordinal);

        // The default exchange's routes are a frozen queueName -> [queueName] map, built once here.
        var defaultRoutes = new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal);
        foreach (string queueName in registry.Queues.Keys)
        {
            defaultRoutes[queueName] = ImmutableArray.Create(queueName);
        }

        nodes[ExchangeRegistry.DefaultExchangeName] = new ExchangeNode(
            name: ExchangeRegistry.DefaultExchangeName,
            isDefaultExchange: true,
            type: default,
            queueBindings: [],
            exchangeBindings: [],
            defaultExchangeRoutes: defaultRoutes.ToFrozenDictionary(StringComparer.Ordinal));

        foreach (ExchangeDeclaration exchange in registry.Exchanges.Values)
        {
            queueBindingsBySource.TryGetValue(exchange.Name, out List<(string RoutingKey, string QueueName)>? queueBindings);
            exchangeBindingsBySource.TryGetValue(exchange.Name, out List<(string RoutingKey, string ExchangeName)>? exchangeBindings);

            nodes[exchange.Name] = new ExchangeNode(
                name: exchange.Name,
                isDefaultExchange: false,
                type: exchange.Type,
                queueBindings: queueBindings is null ? [] : [.. queueBindings],
                exchangeBindings: exchangeBindings is null ? [] : [.. exchangeBindings],
                defaultExchangeRoutes: null);
        }

        return nodes.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// One exchange's routing index (its queue and exchange bindings) plus the unroutable-warning
    /// throttle state for that exchange (including the default exchange, keyed by an empty name).
    /// </summary>
    private sealed class ExchangeNode(
        string name,
        bool isDefaultExchange,
        ExchangeType type,
        ImmutableArray<(string RoutingKey, string QueueName)> queueBindings,
        ImmutableArray<(string RoutingKey, string ExchangeName)> exchangeBindings,
        FrozenDictionary<string, ImmutableArray<string>>? defaultExchangeRoutes)
    {
        internal string Name { get; } = name;

        internal bool IsDefaultExchange { get; } = isDefaultExchange;

        internal ExchangeType Type { get; } = type;

        internal ImmutableArray<(string RoutingKey, string QueueName)> QueueBindings { get; } = queueBindings;

        internal ImmutableArray<(string RoutingKey, string ExchangeName)> ExchangeBindings { get; } = exchangeBindings;

        internal FrozenDictionary<string, ImmutableArray<string>>? DefaultExchangeRoutes { get; } = defaultExchangeRoutes;

        // Per-exchange throttle state for the unroutable warning. 0 = never logged (a real
        // DateTimeOffset.UtcTicks value is always far greater than zero, so 0 is a safe sentinel).
        private long _lastLogTicks;
        private int _suppressedSinceLastLog;

        /// <summary>
        /// Thread-safe throttle check for the unroutable warning: returns <see langword="true"/> when
        /// this call should log now (the first occurrence for this exchange, or the throttle window has
        /// elapsed since the last log), together with the number of calls suppressed since that last log
        /// entry. Returns <see langword="false"/> (incrementing the suppressed count) otherwise.
        /// </summary>
        internal bool TryEnterLogWindow(TimeProvider timeProvider, TimeSpan window, out int suppressedCount)
        {
            long now = timeProvider.GetUtcNow().UtcTicks;
            long last = Volatile.Read(ref _lastLogTicks);

            if (last != 0 && now - last < window.Ticks)
            {
                Interlocked.Increment(ref _suppressedSinceLastLog);
                suppressedCount = 0;
                return false;
            }

            if (Interlocked.CompareExchange(ref _lastLogTicks, now, last) != last)
            {
                // Lost the race to another thread that is also entering a new log window — treat this
                // call as suppressed rather than double-logging.
                Interlocked.Increment(ref _suppressedSinceLastLog);
                suppressedCount = 0;
                return false;
            }

            suppressedCount = Interlocked.Exchange(ref _suppressedSinceLastLog, 0);
            return true;
        }
    }
}
