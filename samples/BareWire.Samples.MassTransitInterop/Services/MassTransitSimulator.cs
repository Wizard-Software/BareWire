using System.Text.Json;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace BareWire.Samples.MassTransitInterop.Services;

/// <summary>
/// Simulates a MassTransit producer publishing <c>OrderCreated</c> messages in MassTransit's
/// envelope format (<c>application/vnd.masstransit+json</c>) directly to RabbitMQ using the
/// bare <c>RabbitMQ.Client</c> library — without any BareWire infrastructure.
/// Publishes one message every 5 seconds while the host is running.
/// </summary>
/// <remarks>
/// This service demonstrates the MassTransit interop scenario: an existing MassTransit producer
/// publishes messages with a full envelope (messageId, correlationId, messageType URN, etc.).
/// BareWire's <c>ContentTypeDeserializerRouter</c> detects <c>application/vnd.masstransit+json</c>
/// and routes the payload to <c>MassTransitEnvelopeDeserializer</c>, which unwraps the envelope
/// before passing the plain <c>OrderCreated</c> record to <c>MassTransitOrderConsumer</c>.
/// </remarks>
internal sealed partial class MassTransitSimulator(
    IConfiguration configuration,
    ILogger<MassTransitSimulator> logger) : BackgroundService, IAsyncDisposable
{
    // Exchange declared by Program.cs topology — the simulator writes to it directly.
    private const string ExchangeName = "mt-orders";

    private IConnection? _connection;
    private IChannel? _channel;

    /// <summary>
    /// Publishes a single MassTransit-envelope message immediately (invoked via the HTTP endpoint).
    /// </summary>
    public async Task PublishOnceAsync(CancellationToken cancellationToken)
    {
        IChannel channel = await GetOrCreateChannelAsync(cancellationToken).ConfigureAwait(false);
        await PublishOrderAsync(channel, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
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
                    await PublishOrderAsync(channel, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogPublishFailed(logger, ex);
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown — exit gracefully.
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

        // Declare the direct exchange so it exists before publishing — the simulator uses a raw
        // RabbitMQ connection independent of BareWire's topology deployment. Both declarations
        // are idempotent; the second one (from BareWire topology) is a no-op if the simulator
        // starts first.
        await _channel.ExchangeDeclareAsync(
            exchange: ExchangeName,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        LogConnected(logger);

        return _channel;
    }

    private async Task PublishOrderAsync(IChannel channel, CancellationToken cancellationToken)
    {
        string messageId = Guid.NewGuid().ToString();
        string correlationId = Guid.NewGuid().ToString();
        string conversationId = Guid.NewGuid().ToString();
        string orderId = Guid.NewGuid().ToString();

        // MassTransit envelope format — matches what a real MassTransit producer sends.
        var envelope = new
        {
            messageId,
            correlationId,
            conversationId,
            sourceAddress = "rabbitmq://localhost/masstransit-producer",
            destinationAddress = "rabbitmq://localhost/mt-orders-queue",
            messageType = new[]
            {
                "urn:message:BareWire.Samples.MassTransitInterop.Messages:OrderCreated",
            },
            sentTime = DateTimeOffset.UtcNow,
            headers = new { },
            message = new
            {
                orderId,
                amount = 99.99m,
                currency = "PLN",
            },
        };

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(envelope);

        BasicProperties props = new()
        {
            // MassTransit content type — triggers MassTransitEnvelopeDeserializer in BareWire.
            ContentType = "application/vnd.masstransit+json",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = messageId,
            CorrelationId = correlationId,
        };

        await channel.BasicPublishAsync<BasicProperties>(
            exchange: ExchangeName,
            routingKey: string.Empty,
            mandatory: false,
            basicProperties: props,
            body: body,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        LogPublished(logger, orderId, correlationId);
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
            try
            {
                await _channel.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best-effort cleanup — ignore close failures.
            }

            _channel.Dispose();
            _channel = null;
        }

        if (_connection is not null)
        {
            try
            {
                await _connection.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best-effort cleanup — ignore close failures.
            }

            _connection.Dispose();
            _connection = null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "MassTransitSimulator: starting periodic publish loop (every 5 seconds)")]
    private static partial void LogStarting(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "MassTransitSimulator: failed to publish order; will retry in 5 seconds")]
    private static partial void LogPublishFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "MassTransitSimulator: stopped")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "MassTransitSimulator: connected to RabbitMQ broker")]
    private static partial void LogConnected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "MassTransitSimulator: published OrderCreated (orderId={OrderId}, correlationId={CorrelationId})")]
    private static partial void LogPublished(ILogger logger, string orderId, string correlationId);
}
