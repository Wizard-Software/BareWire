using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Routing;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.InMemory;

/// <summary>
/// In-memory implementation of <see cref="IRequestClientFactory"/>. Each client sends send-style requests
/// (target exchange from <see cref="IExchangeResolver"/> or the transport's default exchange, routing key from
/// <see cref="IRoutingKeyResolver"/>) and receives replies through the shared <see cref="InMemoryReplyRegistry"/>.
/// </summary>
/// <remarks>
/// The factory sends through whatever <see cref="ITransportAdapter"/> the container resolves, which may be a
/// decorator around the in-memory adapter (the test harness wraps it); it never casts to the concrete adapter.
/// The adapter is obtained through an accessor at client-creation time, not at construction: the bus resolves this
/// factory eagerly while it is being built, and a container without a transport adapter must still surface the
/// friendly start-up configuration error rather than a raw resolution failure.
/// </remarks>
internal sealed partial class InMemoryRequestClientFactory : IRequestClientFactory
{
    internal static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<ITransportAdapter> _adapterAccessor;
    private readonly InMemoryReplyRegistry _replies;
    private readonly InMemoryTransportOptions _options;
    private readonly ISerializerResolver _serializerResolver;
    private readonly IDeserializerResolver _deserializerResolver;
    private readonly IExchangeResolver _exchangeResolver;
    private readonly IRoutingKeyResolver _routingKeyResolver;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<InMemoryRequestClientFactory> _logger;
    private readonly TimeSpan _requestTimeout;

    internal InMemoryRequestClientFactory(
        Func<ITransportAdapter> adapterAccessor,
        InMemoryReplyRegistry replies,
        InMemoryTransportOptions options,
        ISerializerResolver serializerResolver,
        IDeserializerResolver deserializerResolver,
        IExchangeResolver exchangeResolver,
        IRoutingKeyResolver routingKeyResolver,
        ILoggerFactory loggerFactory,
        TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(adapterAccessor);
        ArgumentNullException.ThrowIfNull(replies);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serializerResolver);
        ArgumentNullException.ThrowIfNull(deserializerResolver);
        ArgumentNullException.ThrowIfNull(exchangeResolver);
        ArgumentNullException.ThrowIfNull(routingKeyResolver);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _adapterAccessor = adapterAccessor;
        _replies = replies;
        _options = options;
        _serializerResolver = serializerResolver;
        _deserializerResolver = deserializerResolver;
        _exchangeResolver = exchangeResolver;
        _routingKeyResolver = routingKeyResolver;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<InMemoryRequestClientFactory>();
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
    }

    /// <inheritdoc />
    public ValueTask<IRequestClient<T>> CreateRequestClientAsync<T>(CancellationToken cancellationToken = default)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();

        (IMessageSerializer serializer, string targetExchange, string routingKey) = ResolveDispatch<T>();

        var client = new InMemoryRequestClient<T>(
            _adapterAccessor(),
            _replies,
            serializer,
            _deserializerResolver,
            _loggerFactory.CreateLogger<InMemoryRequestClient<T>>(),
            targetExchange,
            routingKey,
            _requestTimeout);

        LogRequestClientCreated(typeof(T).Name, targetExchange, routingKey);
        return ValueTask.FromResult<IRequestClient<T>>(client);
    }

    /// <summary>
    /// Resolves the per-message-type dispatch settings for request type <typeparamref name="T"/> — serializer,
    /// target exchange and routing key — with the same resolvers the publish path consults, so
    /// <c>MapSerializer</c>, <c>MapExchange</c> and <c>MapRoutingKey</c> are honoured instead of silently
    /// falling back to transport defaults.
    /// </summary>
    /// <typeparam name="T">The request message type.</typeparam>
    /// <exception cref="BareWireConfigurationException">
    /// Thrown when neither a per-type exchange mapping nor a default exchange is configured.
    /// </exception>
    internal (IMessageSerializer Serializer, string TargetExchange, string RoutingKey) ResolveDispatch<T>()
        where T : class
    {
        IMessageSerializer serializer = _serializerResolver.Resolve<T>();
        string routingKey = _routingKeyResolver.Resolve<T>();
        string targetExchange = _exchangeResolver.Resolve<T>()
            ?? _options.DefaultExchange
            ?? throw new BareWireConfigurationException(
                optionName: "DefaultExchange",
                optionValue: "(not set)",
                expectedValue: $"an exchange for request type '{typeof(T).Name}': call MapExchange<{typeof(T).Name}>(...) " +
                               "or DefaultExchange(...) on the in-memory transport configurator");

        return (serializer, targetExchange, routingKey);
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "In-memory request client created for '{MessageType}' (exchange='{Exchange}', routingKey='{RoutingKey}').")]
    private partial void LogRequestClientCreated(string messageType, string exchange, string routingKey);
}
