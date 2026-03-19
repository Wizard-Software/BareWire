using System.Text.Json;
using BareWire.Samples.RawMessageInterop.Messages;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace BareWire.Samples.RawMessageInterop.Services;

/// <summary>
/// Simulates a legacy external system publishing raw JSON messages directly to RabbitMQ
/// using the bare <c>RabbitMQ.Client</c> library — without any BareWire infrastructure.
/// Publishes one <see cref="ExternalEvent"/> every 5 seconds while the host is running.
/// </summary>
/// <remarks>
/// This service demonstrates the interop scenario: an external system (e.g. a Python or Java
/// service) publishes plain JSON with custom headers. BareWire's header mapping
/// (<c>ConfigureHeaderMapping</c> in <c>Program.cs</c>) translates those headers into
/// BareWire canonical form before the message reaches <c>RawEventConsumer</c> or
/// <c>TypedEventConsumer</c>.
/// </remarks>
internal sealed partial class LegacyPublisher(
    IConfiguration configuration,
    ILogger<LegacyPublisher> logger) : BackgroundService, IAsyncDisposable
{
    // Exchange declared by Program.cs topology — the legacy publisher writes to it directly.
    private const string ExchangeName = "legacy.events";

    private IConnection? _connection;
    private IChannel? _channel;

    /// <summary>
    /// Publishes a single <see cref="ExternalEvent"/> immediately (invoked via the HTTP endpoint).
    /// </summary>
    public async Task PublishOnceAsync(CancellationToken cancellationToken)
    {
        IChannel channel = await GetOrCreateChannelAsync(cancellationToken).ConfigureAwait(false);
        await PublishEventAsync(channel, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Delay startup to allow the broker to become ready (Aspire WaitFor handles this in
        // orchestrated mode; the delay here covers standalone Docker / manual runs).
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);

        LogStarting(logger);

        IChannel channel = await GetOrCreateChannelAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishEventAsync(channel, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown — exit gracefully.
                break;
            }
            catch (Exception ex)
            {
                LogPublishFailed(logger, ex);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }

        LogStopped(logger);
    }

    private async Task<IChannel> GetOrCreateChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is not null)
        {
            return _channel;
        }

        // Retrieve connection string from configuration — never log it (CONSTITUTION: NEVER log secrets).
        string connectionString =
            configuration.GetConnectionString("rabbitmq")
            ?? "amqp://guest:guest@localhost:5672/";

        ConnectionFactory factory = new()
        {
            Uri = new Uri(connectionString),
        };

        _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
        _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        LogConnected(logger);

        return _channel;
    }

    private async Task PublishEventAsync(IChannel channel, CancellationToken cancellationToken)
    {
        string correlationId = Guid.NewGuid().ToString();

        ExternalEvent evt = new(
            EventType: "order.created",
            Payload: $"{{\"orderId\":\"{Guid.NewGuid()}\",\"amount\":99.99}}",
            SourceSystem: "LegacyOrderSystem");

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(evt);

        BasicProperties props = new()
        {
            ContentType = "application/json",
            // DeliveryModes.Persistent = messages survive broker restarts.
            DeliveryMode = DeliveryModes.Persistent,
            Headers = new Dictionary<string, object?>
            {
                // These custom headers are mapped by ConfigureHeaderMapping in Program.cs:
                //   X-Correlation-Id → CorrelationId (canonical)
                //   X-Message-Type   → MessageType   (canonical)
                //   X-Source-System  → SourceSystem  (canonical, custom mapping)
                ["X-Correlation-Id"] = correlationId,
                ["X-Message-Type"] = "ExternalEvent",
                ["X-Source-System"] = evt.SourceSystem,
            },
        };

        await channel.BasicPublishAsync<BasicProperties>(
            exchange: ExchangeName,
            routingKey: string.Empty,
            mandatory: false,
            basicProperties: props,
            body: body,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        LogPublished(logger, evt.EventType, correlationId);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await DisposeConnectionAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeConnectionAsync().ConfigureAwait(false);
        Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task DisposeConnectionAsync()
    {
        if (_channel is not null)
        {
            await _channel.CloseAsync().ConfigureAwait(false);
            _channel.Dispose();
            _channel = null;
        }

        if (_connection is not null)
        {
            await _connection.CloseAsync().ConfigureAwait(false);
            _connection.Dispose();
            _connection = null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "LegacyPublisher: starting periodic publish loop (every 5 seconds)")]
    private static partial void LogStarting(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "LegacyPublisher: failed to publish event; will retry in 5 seconds")]
    private static partial void LogPublishFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "LegacyPublisher: stopped")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "LegacyPublisher: connected to RabbitMQ broker")]
    private static partial void LogConnected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "LegacyPublisher: published {EventType} (correlationId={CorrelationId})")]
    private static partial void LogPublished(ILogger logger, string eventType, string correlationId);
}
