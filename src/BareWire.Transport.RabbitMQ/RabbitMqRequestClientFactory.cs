using BareWire.Abstractions;
using BareWire.Abstractions.Routing;
using BareWire.Abstractions.Serialization;
using BareWire.Transport.RabbitMQ.Configuration;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace BareWire.Transport.RabbitMQ;

/// <summary>
/// RabbitMQ implementation of <see cref="IRequestClientFactory"/>.
/// Maintains a lazily-established, shared <see cref="IConnection"/> for all request clients
/// created by this factory. The connection mirrors the options and TLS settings from
/// <see cref="RabbitMqTransportOptions"/>.
/// </summary>
internal sealed partial class RabbitMqRequestClientFactory : IRequestClientFactory, IAsyncDisposable
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    private readonly RabbitMqTransportOptions _options;
    private readonly ISerializerResolver _serializerResolver;
    private readonly IDeserializerResolver _deserializerResolver;
    private readonly IExchangeResolver _exchangeResolver;
    private readonly IRoutingKeyResolver _routingKeyResolver;
    private readonly RabbitMqHeaderMapper _headerMapper;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RabbitMqRequestClientFactory> _logger;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    private IConnection? _connection;
    private int _disposed;

    internal RabbitMqRequestClientFactory(
        RabbitMqTransportOptions options,
        ISerializerResolver serializerResolver,
        IDeserializerResolver deserializerResolver,
        IExchangeResolver exchangeResolver,
        IRoutingKeyResolver routingKeyResolver,
        RabbitMqHeaderMapper headerMapper,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serializerResolver);
        ArgumentNullException.ThrowIfNull(deserializerResolver);
        ArgumentNullException.ThrowIfNull(exchangeResolver);
        ArgumentNullException.ThrowIfNull(routingKeyResolver);
        ArgumentNullException.ThrowIfNull(headerMapper);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _options = options;
        _serializerResolver = serializerResolver;
        _deserializerResolver = deserializerResolver;
        _exchangeResolver = exchangeResolver;
        _routingKeyResolver = routingKeyResolver;
        _headerMapper = headerMapper;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RabbitMqRequestClientFactory>();
    }

    /// <inheritdoc/>
    public async ValueTask<IRequestClient<T>> CreateRequestClientAsync<T>(
        CancellationToken cancellationToken = default) where T : class
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        (IMessageSerializer serializer, string targetExchange, string routingKey) = ResolveDispatch<T>();
        ILogger clientLogger = _loggerFactory.CreateLogger<RabbitMqRequestClient<T>>();

        // Parse connection URI and vhost once per client creation.
        // The URI is already parsed in CreateConnectionAsync; we re-parse here to extract host/vhost
        // for building rabbitmq:// endpoint addresses without passing the full Uri through the factory.
        (Uri connectionUri, string? vhost) = ParseConnectionInfo();

        var client = new RabbitMqRequestClient<T>(
            connection: _connection!,
            serializer: serializer,
            deserializerResolver: _deserializerResolver,
            logger: clientLogger,
            targetExchange: targetExchange,
            routingKey: routingKey,
            timeout: DefaultRequestTimeout,
            connectionUri: connectionUri,
            vhost: vhost,
            headerMapper: _headerMapper);

        await client.InitializeAsync(cancellationToken).ConfigureAwait(false);

        LogRequestClientCreated(typeof(T).Name, targetExchange, routingKey);
        return client;
    }

    /// <summary>
    /// Resolves the per-message-type dispatch settings for request type <typeparamref name="T"/> —
    /// the serializer, target exchange, and routing key — using the same resolvers consulted by the
    /// publish path (<c>BareWireBus.PublishAsync</c>). This is what makes request clients honour
    /// <c>MapSerializer&lt;T&gt;</c>, <c>MapExchange&lt;T&gt;</c>, and <c>MapRoutingKey&lt;T&gt;</c>
    /// configuration instead of silently falling back to transport defaults (issue #13).
    /// </summary>
    /// <typeparam name="T">The request message type.</typeparam>
    /// <returns>The serializer, target exchange, and routing key to use for the request client.</returns>
    /// <remarks>
    /// Exchange precedence mirrors the publish path: an explicit <c>MapExchange&lt;T&gt;</c> mapping
    /// wins, otherwise the transport <see cref="RabbitMqTransportOptions.DefaultExchange"/> is used.
    /// </remarks>
    internal (IMessageSerializer Serializer, string TargetExchange, string RoutingKey) ResolveDispatch<T>()
        where T : class
    {
        IMessageSerializer serializer = _serializerResolver.Resolve<T>();
        string routingKey = _routingKeyResolver.Resolve<T>();

        // Exchange precedence mirrors the publish path: explicit MapExchange<T> mapping wins,
        // otherwise fall back to the transport DefaultExchange (issue #13).
        string targetExchange = _exchangeResolver.Resolve<T>() ?? _options.DefaultExchange;

        return (serializer, targetExchange, routingKey);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return;

        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                try
                {
                    await _connection.CloseAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogConnectionCloseError(ex);
                }

                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }
        }
        finally
        {
            _connectionLock.Release();
        }

        _connectionLock.Dispose();
    }

    // ── Connection management ─────────────────────────────────────────────────

    private async ValueTask EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        // Fast path — volatile read avoids entering the lock when already connected.
        IConnection? conn = Volatile.Read(ref _connection);
        if (conn is not null && conn.IsOpen)
            return;

        bool acquired = await _connectionLock.WaitAsync(_options.ConnectionTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!acquired)
            throw new TimeoutException(
                $"Timed out after {_options.ConnectionTimeout} waiting to acquire connection lock.");

        try
        {
            // Double-check inside the lock to avoid duplicate connections from concurrent callers.
            conn = Volatile.Read(ref _connection);
            if (conn is not null && conn.IsOpen)
                return;

            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            Volatile.Write(ref _connection,
                await CreateConnectionAsync(cancellationToken).ConfigureAwait(false));

            LogConnectionEstablished(_connection!.Endpoint.HostName, _connection.Endpoint.Port);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Parses the connection URI from <see cref="RabbitMqTransportOptions.ConnectionString"/> and
    /// extracts the virtual host so that <see cref="RabbitMqRequestClient{TRequest}"/> can build
    /// correct <c>rabbitmq://</c> endpoint addresses for request envelopes.
    /// </summary>
    /// <returns>
    /// The connection <see cref="Uri"/> and the resolved vhost string.
    /// An empty or "/" path segment is normalized to <see langword="null"/> (default vhost).
    /// </returns>
    private (Uri ConnectionUri, string? Vhost) ParseConnectionInfo()
    {
        var uri = new Uri(_options.ConnectionString);

        // AbsolutePath for amqp://host/vhost is "/vhost"; strip the leading "/".
        // An empty path or "/" means the default vhost — return null so the address builder omits it.
        string rawPath = uri.AbsolutePath.TrimStart('/');
        string? vhost = string.IsNullOrEmpty(rawPath) ? null : rawPath;

        return (uri, vhost);
    }

    private async Task<IConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        var uri = new Uri(_options.ConnectionString);

        var factory = new ConnectionFactory
        {
            Uri = uri,
            AutomaticRecoveryEnabled = _options.AutomaticRecoveryEnabled,
            NetworkRecoveryInterval = _options.NetworkRecoveryInterval,
        };

        if (_options.ConfigureTls is not null)
        {
            string serverName = uri.Host;
            var tlsConfigurator = new RabbitMqTlsConfigurator();
            _options.ConfigureTls(tlsConfigurator);
            factory.Ssl = tlsConfigurator.Build(serverName);
        }
        else if (_options.SslOptions is not null)
        {
            factory.Ssl = _options.SslOptions;
        }

        return await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    // ── Logger messages ───────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information,
        Message = "RabbitMQ request client factory connection established to {Host}:{Port}.")]
    private partial void LogConnectionEstablished(string host, int port);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Request client created for '{MessageType}' (exchange='{Exchange}', routingKey='{RoutingKey}').")]
    private partial void LogRequestClientCreated(string messageType, string exchange, string routingKey);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Exception while closing RabbitMQ request client factory connection during dispose.")]
    private partial void LogConnectionCloseError(Exception ex);
}
