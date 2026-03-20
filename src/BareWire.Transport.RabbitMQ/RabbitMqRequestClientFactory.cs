using BareWire.Abstractions;
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
    private readonly IMessageSerializer _serializer;
    private readonly IMessageDeserializer _deserializer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RabbitMqRequestClientFactory> _logger;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    private IConnection? _connection;
    private int _disposed;

    internal RabbitMqRequestClientFactory(
        RabbitMqTransportOptions options,
        IMessageSerializer serializer,
        IMessageDeserializer deserializer,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(deserializer);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _options = options;
        _serializer = serializer;
        _deserializer = deserializer;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RabbitMqRequestClientFactory>();
    }

    /// <inheritdoc/>
    public async ValueTask<IRequestClient<T>> CreateRequestClientAsync<T>(
        CancellationToken cancellationToken = default) where T : class
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        string routingKey = typeof(T).FullName ?? typeof(T).Name;
        ILogger clientLogger = _loggerFactory.CreateLogger<RabbitMqRequestClient<T>>();

        var client = new RabbitMqRequestClient<T>(
            connection: _connection!,
            serializer: _serializer,
            deserializer: _deserializer,
            logger: clientLogger,
            targetExchange: _options.DefaultExchange,
            routingKey: routingKey,
            timeout: DefaultRequestTimeout);

        await client.InitializeAsync(cancellationToken).ConfigureAwait(false);

        LogRequestClientCreated(typeof(T).Name, routingKey);
        return client;
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
        Message = "Request client created for '{MessageType}' (routingKey='{RoutingKey}').")]
    private partial void LogRequestClientCreated(string messageType, string routingKey);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Exception while closing RabbitMQ request client factory connection during dispose.")]
    private partial void LogConnectionCloseError(Exception ex);
}
