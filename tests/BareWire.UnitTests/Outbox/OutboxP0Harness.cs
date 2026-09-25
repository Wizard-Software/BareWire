using System.Buffers;
using System.Diagnostics.Metrics;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Outbox;
using BareWire.Outbox.EntityFramework;
using BareWire.Outbox.EntityFramework.Internal;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace BareWire.UnitTests.Outbox;

/// <summary>
/// Selects which <c>IOutboxStore</c> implementation an <see cref="OutboxP0Harness"/> drives: the EF
/// Core store against a shared SQLite <c>:memory:</c> connection, or the in-process
/// <c>InMemoryOutboxStore</c>.
/// </summary>
public enum OutboxP0StoreKind
{
    /// <summary>The EF Core store, backed by a SQLite <c>:memory:</c> connection.</summary>
    EfCoreSqlite,

    /// <summary>The in-process <c>InMemoryOutboxStore</c>.</summary>
    InMemory,
}

// ── Events ────────────────────────────────────────────────────────────────────

// One ordered log of everything the P0 harness observed while the dispatcher loop ran: cycle
// boundaries, transport sends, deliveries, drain-streak pauses, and rate-limited retry warnings. Only
// ever appended to from the dispatcher's polling-loop thread (see CycleCountingOutboxStore,
// OutboxP0ScriptedTransportAdapter, OutboxP0EventLogger); read by the test only after
// OutboxP0Harness.RunUntilAsync has returned, once the loop has fully stopped.
internal abstract record OutboxP0Event(int Cycle);

internal sealed record CycleStarted(int Cycle, DateTimeOffset Now) : OutboxP0Event(Cycle);

internal sealed record Sent(int Cycle, string Label, int Attempt, bool Confirmed, DateTimeOffset Now) : OutboxP0Event(Cycle);

internal sealed record Delivered(int Cycle, string Label) : OutboxP0Event(Cycle);

internal sealed record Paused(int Cycle, int BatchCount) : OutboxP0Event(Cycle);

internal sealed record RetryWarning(int Cycle, long RowId, int RetryCount) : OutboxP0Event(Cycle);

// ── Harness ───────────────────────────────────────────────────────────────────

/// <summary>
/// Drives a real <c>OutboxDispatcher</c> loop against a real store (EF Core on SQLite, or
/// <c>InMemoryOutboxStore</c>) with the store's clock advanced by a fixed step on every dispatch
/// cycle, so P0 liveness properties can be proven deterministically instead of on wall-clock time.
/// </summary>
/// <remarks>
/// Lifecycle: <see cref="CreateAsync"/> constructs the dispatcher but does not start it. Every
/// <see cref="SaveAsync"/> / <see cref="SeedAsDueRetriesAsync"/> call must happen before the single
/// <see cref="RunUntilAsync"/> call, which starts the dispatcher, waits for the decorator to signal
/// completion (the <paramref name="done"/> predicate became true, or <c>maxCycles</c> was reached),
/// stops the dispatcher, and only then returns — so every accessor below is safe to read once
/// <see cref="RunUntilAsync"/> has returned, and never safe to read (or write via Save/Seed) before.
/// </remarks>
internal sealed class OutboxP0Harness : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // Every routing key this harness writes carries this prefix, followed by the caller's label — the
    // harness identifies rows by label, not by the store's assigned Id, because both stores assign ids
    // in write order (so a poison cohort written before a fresh cohort always has the lower ids).
    internal const string LabelPrefix = "p0.";

    private readonly OutboxOptions _options;
    private readonly TimeSpan _clockStepPerCycle;
    private readonly OutboxP0StoreKind _kind;
    private readonly List<OutboxP0Event> _events = [];
    private readonly Dictionary<long, string> _idToLabel = [];
    private readonly Dictionary<string, int> _deliveredInCycle = new(StringComparer.Ordinal);

    private ServiceProvider _provider = null!;
    private SqliteConnection? _connection;
    private InMemoryOutboxStore? _inMemoryStore;
    private Meter _meter = null!;
    private MeterListener _meterListener = null!;
    private OutboxDispatcher _dispatcher = null!;

    private Func<OutboxP0Harness, bool>? _donePredicate;
    private int _maxCycles;
    private TaskCompletionSource? _doneSignal;
    private bool _runInvoked;
    private long _retriedRowsCounterTotal;

    private OutboxP0Harness(OutboxOptions options, TimeSpan clockStepPerCycle, OutboxP0StoreKind kind)
    {
        _options = options;
        _clockStepPerCycle = clockStepPerCycle;
        _kind = kind;
        Clock = new FakeTimeProvider(T0);
    }

    /// <summary>The store's clock — advanced only by the cycle-counting decorator, once per real dispatch cycle.</summary>
    internal FakeTimeProvider Clock { get; }

    /// <summary>Number of real dispatch cycles observed so far (a cycle whose <c>GetPendingAsync</c> call was not the post-completion no-op).</summary>
    internal int CycleCount { get; private set; }

    /// <summary>The ordered event log — see <see cref="OutboxP0Event"/>.</summary>
    internal IReadOnlyList<OutboxP0Event> Events => _events;

    /// <summary>Running total of the <c>barewire.outbox.rows.retried</c> counter, isolated to this harness's own <see cref="Meter"/>.</summary>
    internal long RetriedRowsCounterTotal => Interlocked.Read(ref _retriedRowsCounterTotal);

    /// <summary>
    /// Creates a harness for <paramref name="kind"/>, wiring a real <c>OutboxDispatcher</c> against a
    /// cycle-counting decorator over the chosen store. The dispatcher is constructed but not started —
    /// call <see cref="SaveAsync"/> / <see cref="SeedAsDueRetriesAsync"/> to seed data, then
    /// <see cref="RunUntilAsync"/> exactly once to run the loop.
    /// </summary>
    /// <param name="kind">Which store implementation to drive.</param>
    /// <param name="options">Outbox options for both the store and the dispatcher.</param>
    /// <param name="clockStepPerCycle">Amount the store's clock advances on every real dispatch cycle.</param>
    /// <param name="isConfirmed">Decides, for a given label and 1-based attempt number, whether the scripted transport confirms that send.</param>
    internal static async Task<OutboxP0Harness> CreateAsync(
        OutboxP0StoreKind kind,
        OutboxOptions options,
        TimeSpan clockStepPerCycle,
        Func<string, int, bool> isConfirmed)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(isConfirmed);

        var harness = new OutboxP0Harness(options, clockStepPerCycle, kind);
        await harness.InitializeAsync(isConfirmed).ConfigureAwait(false);
        return harness;
    }

    private async Task InitializeAsync(Func<string, int, bool> isConfirmed)
    {
        var services = new ServiceCollection();

        if (_kind == OutboxP0StoreKind.EfCoreSqlite)
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            await _connection.OpenAsync().ConfigureAwait(false);

            services.AddDbContext<OutboxDbContext>(o => o.UseSqlite(_connection));
            services.AddSingleton(_options);
            services.AddSingleton(new OutboxInstanceId("p0"));
            services.AddSingleton<IOutboxSqlDialect, PostgresOutboxSqlDialect>();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IOutboxJitterSource>(new OutboxP0FixedJitterSource(0.5));
            // Mirrors AddBareWireOutbox's factory-lambda registration — OutboxSingleSlotTurn has no
            // public constructor, so the container cannot construct it via the type-only overload.
            services.AddSingleton(_ => new OutboxSingleSlotTurn());
            services.AddScoped(sp => new EfCoreOutboxStore(
                sp.GetRequiredService<OutboxDbContext>(),
                sp.GetRequiredService<OutboxInstanceId>(),
                sp.GetRequiredService<IOutboxSqlDialect>(),
                sp.GetRequiredService<OutboxOptions>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IOutboxJitterSource>(),
                sp.GetRequiredService<OutboxSingleSlotTurn>()));
            services.AddScoped<IOutboxStore>(sp =>
                new CycleCountingOutboxStore(sp.GetRequiredService<EfCoreOutboxStore>(), this));
        }
        else
        {
            _inMemoryStore = new InMemoryOutboxStore(
                _options, timeProvider: Clock, jitterSource: new OutboxP0FixedJitterSource(0.5));
            services.AddSingleton(_inMemoryStore);
            services.AddScoped<IOutboxStore>(sp =>
                new CycleCountingOutboxStore(sp.GetRequiredService<InMemoryOutboxStore>(), this));
        }

        _provider = services.BuildServiceProvider();

        if (_kind == OutboxP0StoreKind.EfCoreSqlite)
        {
            await using AsyncServiceScope schemaScope = _provider.CreateAsyncScope();
            OutboxDbContext dbContext = schemaScope.ServiceProvider.GetRequiredService<OutboxDbContext>();
            await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);
        }

        // Per-test Meter, isolated by reference (not by name — see the shared testing convention):
        // the listener only enables measurement events for instruments created on THIS harness's
        // Meter, so barewire.outbox.rows.retried activity from other tests running concurrently in
        // the same process can never be attributed to this harness's counter total.
        _meter = new Meter("BareWire.Test." + Guid.NewGuid());
        Meter capturedMeter = _meter;
        var meterFactory = Substitute.For<IMeterFactory>();
        meterFactory.Create(Arg.Any<MeterOptions>()).Returns(_meter);

        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, capturedMeter))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _meterListener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == OutboxRetryDiagnostics.RetriedRowsCounterName)
            {
                Interlocked.Add(ref _retriedRowsCounterTotal, measurement);
            }
        });
        _meterListener.Start();

        var adapter = new OutboxP0ScriptedTransportAdapter(this, isConfirmed);
        var logger = new OutboxP0EventLogger(this);

        _dispatcher = new OutboxDispatcher(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            adapter,
            _options,
            logger,
            StartedLifetime(),
            Clock,
            meterFactory);
    }

    /// <summary>
    /// Persists <paramref name="labels"/> as new outbox rows (routing key <c><see cref="LabelPrefix"/>label</c>),
    /// optionally stamping the configured ordering-key header. Must be called before
    /// <see cref="RunUntilAsync"/>.
    /// </summary>
    internal async Task SaveAsync(IReadOnlyList<string> labels, string? orderingKey = null)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ThrowIfRunning();

        if (labels.Count == 0)
        {
            return;
        }

        Dictionary<string, string> headers = [];
        if (orderingKey is not null)
        {
            if (string.IsNullOrEmpty(_options.OrderingKeyHeaderName))
            {
                throw new InvalidOperationException(
                    "An ordering key was supplied, but OutboxOptions.OrderingKeyHeaderName is not configured.");
            }

            headers[_options.OrderingKeyHeaderName] = orderingKey;
        }

        OutboundMessage[] messages = [.. labels.Select(label => new OutboundMessage(
            routingKey: LabelPrefix + label,
            headers: headers,
            body: "{}"u8.ToArray(),
            contentType: "application/json"))];

        await using AsyncServiceScope scope = _provider.CreateAsyncScope();
        IOutboxStore store = ResolveInnerStore(scope.ServiceProvider);
        await store.SaveMessagesAsync(messages).ConfigureAwait(false);

        if (_kind == OutboxP0StoreKind.EfCoreSqlite)
        {
            // The EF store never calls SaveChanges itself (production relies on the ambient
            // transactional-middleware unit of work) — the harness owns the equivalent commit here.
            await scope.ServiceProvider.GetRequiredService<OutboxDbContext>()
                .SaveChangesAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Claims exactly <paramref name="labels"/> on the store directly (bypassing the cycle-counting
    /// decorator, so this does not count as a dispatch cycle), nacks them once, and advances the clock
    /// past the worst-case first-nack deferral so they are due retries (RetryCount == 1) before the
    /// dispatch loop's first claim. Throws if the claim did not return exactly the given labels — a
    /// silent partial seed would invalidate the scenario without turning red.
    /// </summary>
    internal async Task SeedAsDueRetriesAsync(IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ThrowIfRunning();

        if (labels.Count == 0)
        {
            return;
        }

        await using AsyncServiceScope scope = _provider.CreateAsyncScope();
        IOutboxStore store = ResolveInnerStore(scope.ServiceProvider);

        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(labels.Count).ConfigureAwait(false);
        try
        {
            var claimed = new HashSet<string>(batch.Select(e => StripLabel(e.RoutingKey)), StringComparer.Ordinal);
            var expected = new HashSet<string>(labels, StringComparer.Ordinal);
            if (!claimed.SetEquals(expected))
            {
                throw new InvalidOperationException(
                    "Seeding due retries claimed an unexpected set of rows: expected "
                    + $"[{string.Join(", ", expected)}], got [{string.Join(", ", claimed)}]. Every label "
                    + "must already be saved, and no other unclaimed row may exist, before seeding it.");
            }

            long[] ids = [.. batch.Select(e => e.Id)];
            await store.ReleaseLockAsync(ids, [], CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            foreach (OutboxEntry entry in batch)
            {
                ArrayPool<byte>.Shared.Return(entry.PooledBody);
            }
        }

        // The first nack's base deferral is one PollingInterval, plus up to +20% jitter on top (never
        // below PollingInterval) — 1.2 x PollingInterval bounds the worst case. Advancing by that plus
        // one cycle step guarantees every seeded row is already due before the loop's first claim.
        Clock.Advance(TimeSpan.FromTicks((long)(1.2 * _options.PollingInterval.Ticks)) + _clockStepPerCycle);
    }

    /// <summary>
    /// Starts the dispatcher loop, waits until <paramref name="done"/> reports completion (evaluated by
    /// the loop thread on every real cycle, before that cycle's clock advance) or <paramref name="maxCycles"/>
    /// real cycles have elapsed, then stops the dispatcher — only returning once the loop has fully
    /// stopped. May be called at most once per harness.
    /// </summary>
    internal async Task RunUntilAsync(Func<OutboxP0Harness, bool> done, int maxCycles)
    {
        ArgumentNullException.ThrowIfNull(done);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxCycles, 0);

        if (_runInvoked)
        {
            throw new InvalidOperationException("RunUntilAsync may only be called once per harness.");
        }

        _runInvoked = true;
        _donePredicate = done;
        _maxCycles = maxCycles;
        _doneSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await _dispatcher.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            // A safety net for CI only — no assertion in any P0 scenario depends on wall-clock time;
            // every one of them counts cycles and reads the FakeTimeProvider clock.
            await _doneSignal.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        }
        finally
        {
            await _dispatcher.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>The cycle a label was marked delivered in, or <see langword="null"/> when it has not been delivered yet.</summary>
    internal int? DeliveredInCycle(string label)
        => _deliveredInCycle.TryGetValue(label, out int cycle) ? cycle : null;

    /// <summary>Every <see cref="Sent"/> event recorded for <paramref name="label"/>, in cycle order.</summary>
    internal IReadOnlyList<OutboxP0Event> SendsOf(string label)
        => [.. _events.OfType<Sent>().Where(s => s.Label == label)];

    public async ValueTask DisposeAsync()
    {
        _meterListener.Dispose();
        _meter.Dispose();
        await _dispatcher.DisposeAsync().ConfigureAwait(false);

        if (_inMemoryStore is not null)
        {
            await _inMemoryStore.DisposeAsync().ConfigureAwait(false);
        }

        await _provider.DisposeAsync().ConfigureAwait(false);

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ── Decorator hooks — internal, called only from CycleCountingOutboxStore / the scripted
    //    transport adapter / the event logger, all on the dispatcher's polling-loop thread. ──────────

    internal bool ShouldStop() => CycleCount >= _maxCycles || (_donePredicate?.Invoke(this) ?? false);

    internal void SignalDone() => _doneSignal?.TrySetResult();

    internal void BeginCycle()
    {
        Clock.Advance(_clockStepPerCycle);
        CycleCount++;
        _events.Add(new CycleStarted(CycleCount, Clock.GetUtcNow()));
    }

    internal void RegisterClaimedBatch(IReadOnlyList<OutboxEntry> batch)
    {
        foreach (OutboxEntry entry in batch)
        {
            _idToLabel[entry.Id] = StripLabel(entry.RoutingKey);
        }
    }

    internal void RecordDelivered(IReadOnlyList<long> ids)
    {
        foreach (long id in ids)
        {
            if (_idToLabel.TryGetValue(id, out string? label))
            {
                _deliveredInCycle[label] = CycleCount;
                _events.Add(new Delivered(CycleCount, label));
            }
        }
    }

    internal void RecordEvent(OutboxP0Event evt) => _events.Add(evt);

    // ── Private helpers ──────────────────────────────────────────────────────

    private IOutboxStore ResolveInnerStore(IServiceProvider scopedProvider)
        => _kind == OutboxP0StoreKind.EfCoreSqlite
            ? scopedProvider.GetRequiredService<EfCoreOutboxStore>()
            : _inMemoryStore!;

    private void ThrowIfRunning()
    {
        if (_runInvoked)
        {
            throw new InvalidOperationException("Cannot save or seed messages after RunUntilAsync has started.");
        }
    }

    private static string StripLabel(string routingKey)
        => routingKey.StartsWith(LabelPrefix, StringComparison.Ordinal)
            ? routingKey[LabelPrefix.Length..]
            : routingKey;

    // A lifetime whose ApplicationStarted has already fired, so the dispatcher's loop starts as soon
    // as RunUntilAsync calls StartAsync — same pattern as OutboxDispatcherRetryObservabilityTests.
    private static IHostApplicationLifetime StartedLifetime()
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        var started = new CancellationTokenSource();
        started.Cancel();
        lifetime.ApplicationStarted.Returns(started.Token);
        return lifetime;
    }
}

// ── Store decorator ──────────────────────────────────────────────────────────

// Wraps the real store the dispatcher talks to: on every GetPendingAsync call it either signals
// completion (the harness's done predicate is satisfied, or the cycle budget is exhausted) and
// returns an empty batch without counting a cycle, or advances the harness's clock by one step,
// counts the cycle, and records the id -> label mapping the harness needs to translate MarkDeliveredAsync
// ids back to labels. Everything else is forwarded 1:1 — including both ReleaseLockAsync overloads (so
// the per-key ordering barrier's two-list release is preserved exactly) and the retry-backlog probe.
// Only GetPendingAsync counts as a cycle; the probe never does.
internal sealed class CycleCountingOutboxStore(IOutboxStore inner, OutboxP0Harness harness)
    : IOutboxStore, IOutboxRetryBacklogProbe
{
    public ValueTask SaveMessagesAsync(
        IReadOnlyList<OutboundMessage> messages, CancellationToken cancellationToken = default)
        => inner.SaveMessagesAsync(messages, cancellationToken);

    public async ValueTask<IReadOnlyList<OutboxEntry>> GetPendingAsync(
        int batchSize, CancellationToken cancellationToken = default)
    {
        if (harness.ShouldStop())
        {
            harness.SignalDone();
            return Array.Empty<OutboxEntry>();
        }

        harness.BeginCycle();

        IReadOnlyList<OutboxEntry> batch = await inner.GetPendingAsync(batchSize, cancellationToken)
            .ConfigureAwait(false);
        harness.RegisterClaimedBatch(batch);
        return batch;
    }

    public ValueTask MarkDeliveredAsync(
        IReadOnlyList<long> ids, CancellationToken cancellationToken = default)
    {
        harness.RecordDelivered(ids);
        return inner.MarkDeliveredAsync(ids, cancellationToken);
    }

    public ValueTask<IReadOnlySet<long>> ReleaseLockAsync(
        IReadOnlyList<long> ids, CancellationToken cancellationToken = default)
        => inner.ReleaseLockAsync(ids, cancellationToken);

    public ValueTask<IReadOnlySet<long>> ReleaseLockAsync(
        IReadOnlyList<long> nackedIds,
        IReadOnlyList<long> barrierReleasedIds,
        CancellationToken cancellationToken = default)
        => inner.ReleaseLockAsync(nackedIds, barrierReleasedIds, cancellationToken);

    public ValueTask CleanupAsync(TimeSpan retention, CancellationToken cancellationToken = default)
        => inner.CleanupAsync(retention, cancellationToken);

    public ValueTask<DateTimeOffset?> GetOldestDueRetryAsync(
        DateTimeOffset now, CancellationToken cancellationToken = default)
        => inner is IOutboxRetryBacklogProbe probe
            ? probe.GetOldestDueRetryAsync(now, cancellationToken)
            : ValueTask.FromResult<DateTimeOffset?>(null);
}

// ── Scripted transport ───────────────────────────────────────────────────────

// Confirms or nacks every send by label and 1-based attempt number, per the caller's script, and
// records a Sent event for each message at the harness's current cycle and clock instant. Every other
// ITransportAdapter member is unused by the outbox dispatch path and throws.
internal sealed class OutboxP0ScriptedTransportAdapter(OutboxP0Harness harness, Func<string, int, bool> isConfirmed)
    : ITransportAdapter
{
    private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);

    public string TransportName => "OutboxP0.Scripted";

    public TransportCapabilities Capabilities => TransportCapabilities.PublisherConfirms;

    public Task<IReadOnlyList<SendResult>> SendBatchAsync(
        IReadOnlyList<OutboundMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var results = new SendResult[messages.Count];

        for (int i = 0; i < messages.Count; i++)
        {
            string label = messages[i].RoutingKey.StartsWith(OutboxP0Harness.LabelPrefix, StringComparison.Ordinal)
                ? messages[i].RoutingKey[OutboxP0Harness.LabelPrefix.Length..]
                : messages[i].RoutingKey;
            int attempt = _attempts.TryGetValue(label, out int prior) ? prior + 1 : 1;
            _attempts[label] = attempt;

            bool confirmed = isConfirmed(label, attempt);
            results[i] = new SendResult(IsConfirmed: confirmed, DeliveryTag: (ulong)i);
            harness.RecordEvent(new Sent(harness.CycleCount, label, attempt, confirmed, harness.Clock.GetUtcNow()));
        }

        return Task.FromResult<IReadOnlyList<SendResult>>(results);
    }

    public IAsyncEnumerable<InboundMessage> ConsumeAsync(
        string endpointName,
        FlowControlOptions flowControl,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The P0 liveness harness never consumes inbound messages.");

    public Task SettleAsync(
        SettlementAction action,
        InboundMessage message,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The P0 liveness harness never settles inbound messages.");

    public Task DeployTopologyAsync(
        TopologyDeclaration topology,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The P0 liveness harness never deploys topology.");
}

// ── Event-capturing logger ───────────────────────────────────────────────────

// Captures exactly the two dispatcher log entries the P0 scenarios care about: the Debug "drain streak
// ended" pause (recorded as a Paused event carrying its BatchCount property) and the rate-limited
// retry Warning (recorded as a RetryWarning event carrying its RowId and RetryCount properties). Every
// other log entry is observed and ignored. IsEnabled always reports true — a default NSubstitute-style
// stub would report false and silently drop every [LoggerMessage] call.
internal sealed class OutboxP0EventLogger(OutboxP0Harness harness) : ILogger<OutboxDispatcher>
{
    private const string DrainStreakEndedPrefix = "Outbox drain streak ended";

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        IReadOnlyList<KeyValuePair<string, object?>> properties =
            state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];

        if (logLevel == LogLevel.Debug)
        {
            string message = formatter(state, exception);
            if (message.StartsWith(DrainStreakEndedPrefix, StringComparison.Ordinal))
            {
                int batchCount = properties.FirstOrDefault(kv => kv.Key == "BatchCount").Value is int value ? value : 0;
                harness.RecordEvent(new Paused(harness.CycleCount, batchCount));
            }

            return;
        }

        if (logLevel == LogLevel.Warning)
        {
            long rowId = properties.FirstOrDefault(kv => kv.Key == "RowId").Value is long id ? id : 0L;
            int retryCount = properties.FirstOrDefault(kv => kv.Key == "RetryCount").Value is int rc ? rc : 0;
            harness.RecordEvent(new RetryWarning(harness.CycleCount, rowId, retryCount));
        }
    }
}

// ── Deterministic jitter ─────────────────────────────────────────────────────

// A constant jitter draw, so neither store's nack-deferral escalation nor (for the EF store) its
// single-slot turn depends on randomness — every P0 assertion is about counted cycles, never chance.
internal sealed class OutboxP0FixedJitterSource(double value) : IOutboxJitterSource
{
    public double NextDouble() => value;
}
