using System.Text.Json;
using BareWire.Samples.ConsumerRoutingKeys.Messages;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

// RabbitMQ.Client.ExchangeType is a static class with string constants (e.g. "topic").
// BareWire.Abstractions.ExchangeType is the BareWire enum used in Program.cs topology.
// The two types are in separate files so there is no ambiguous-reference collision.

namespace BareWire.Samples.ConsumerRoutingKeys.Services;

/// <summary>
/// Simulates a non-BareWire upstream producer publishing directly to the topic exchange
/// using <c>RabbitMQ.Client</c>. Demonstrates per-message routing-key control and the
/// presence or absence of the <c>BW-MessageType</c> header for typed vs. type-less dispatch.
/// </summary>
/// <remarks>
/// SECURITY: Never log the connection string — it contains credentials
/// (CONSTITUTION: NEVER log secrets).
/// </remarks>
internal sealed partial class UpstreamPublisher(
    IConfiguration configuration,
    ILogger<UpstreamPublisher> logger) : IAsyncDisposable
{
    private const string ExchangeName = "consumer-routing-keys.transfers";

    // Camelcase options for STJ: matches JsonSerializerDefaults.Web used by the BareWire
    // raw-first deserializer so property names round-trip correctly.
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private IConnection? _connection;
    private IChannel? _channel;

    /// <summary>
    /// Publishes 3 deliveries to the topic exchange for the given <paramref name="runId"/>:
    /// <list type="number">
    ///   <item><c>transfer.eu.priority</c> + BW-MessageType → expect PriorityTransferConsumer (exact wins)</item>
    ///   <item><c>transfer.eu.standard</c> + BW-MessageType → expect RegionTransferConsumer (wildcard)</item>
    ///   <item><c>legacy.audit.created</c> with no BW-MessageType → expect LegacyNotificationConsumer (type-less)</item>
    /// </list>
    /// </summary>
    public async Task PublishScenarioAsync(string runId, CancellationToken cancellationToken)
    {
        IChannel channel = await GetOrCreateChannelAsync(cancellationToken).ConfigureAwait(false);

        // Delivery 1: typed, exact routing key — PriorityTransferConsumer wins over wildcard.
        TransferInitiated priority = new(
            RunId: runId,
            TransferId: Guid.NewGuid().ToString("N"),
            Region: "eu",
            Kind: "priority",
            Amount: 5000m);

        await PublishTypedAsync(channel, "transfer.eu.priority", priority, cancellationToken)
            .ConfigureAwait(false);

        // Delivery 2: typed, standard routing key — only RegionTransferConsumer's "transfer.eu.*" matches.
        TransferInitiated standard = new(
            RunId: runId,
            TransferId: Guid.NewGuid().ToString("N"),
            Region: "eu",
            Kind: "standard",
            Amount: 250m);

        await PublishTypedAsync(channel, "transfer.eu.standard", standard, cancellationToken)
            .ConfigureAwait(false);

        // Delivery 3: foreign, no BW-MessageType header — type-less dispatch to LegacyNotificationConsumer.
        LegacyNotification notification = new(
            RunId: runId,
            NotificationId: Guid.NewGuid().ToString("N"),
            Source: "LegacyAudit",
            Detail: $"audit-event-for-{runId}");

        await PublishUntypedAsync(channel, "legacy.audit.created", notification, cancellationToken)
            .ConfigureAwait(false);

        LogScenarioPublished(logger, runId);
    }

    private static async Task PublishTypedAsync<T>(
        IChannel channel,
        string routingKey,
        T message,
        CancellationToken cancellationToken)
        where T : class
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, SerializeOptions);

        BasicProperties props = new()
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Transient,
            Headers = new Dictionary<string, object?>
            {
                // BW-MessageType = simple type name — identical to what BareWireBus sets on publish.
                // The dispatcher matches this value (Ordinal) against the consumer's message type name
                // to select typed consumers before falling through to the type-less layer.
                ["BW-MessageType"] = typeof(T).Name,
            },
        };

        await channel.BasicPublishAsync<BasicProperties>(
            exchange: ExchangeName,
            routingKey: routingKey,
            mandatory: false,
            basicProperties: props,
            body: body,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task PublishUntypedAsync<T>(
        IChannel channel,
        string routingKey,
        T message,
        CancellationToken cancellationToken)
        where T : class
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, SerializeOptions);

        // Deliberately no BW-MessageType header: the dispatcher cannot resolve a message type,
        // so only consumers that have called AcceptUntyped() and whose routing-key pattern
        // matches are eligible. The raw payload is deserialized into TMessage (raw-first interop).
        BasicProperties props = new()
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Transient,
        };

        await channel.BasicPublishAsync<BasicProperties>(
            exchange: ExchangeName,
            routingKey: routingKey,
            mandatory: false,
            basicProperties: props,
            body: body,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<IChannel> GetOrCreateChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is not null)
        {
            return _channel;
        }

        // Retrieve the connection string from configuration — never log it.
        string connectionString =
            configuration.GetConnectionString("rabbitmq")
            ?? "amqp://guest:guest@localhost:5672/";

        ConnectionFactory factory = new() { Uri = new Uri(connectionString) };

        _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
        _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Declare the exchange with bit-identical parameters to the BareWire topology declaration
        // in Program.cs: Topic / durable:false / autoDelete:true.
        // Any parameter mismatch causes PRECONDITION_FAILED on the broker.
        // ExchangeType here is RabbitMQ.Client.ExchangeType (static class with string constants),
        // imported via 'using RabbitMQ.Client;' above. This is a separate file from Program.cs
        // which uses BareWire.Abstractions.ExchangeType (enum) — no collision.
        await _channel.ExchangeDeclareAsync(
            exchange: ExchangeName,
            type: ExchangeType.Topic,
            durable: false,
            autoDelete: true,
            arguments: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        LogConnected(logger);

        return _channel;
    }

    public async ValueTask DisposeAsync()
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
        Message = "UpstreamPublisher: connected to RabbitMQ broker")]
    private static partial void LogConnected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "UpstreamPublisher: published scenario for run {RunId}")]
    private static partial void LogScenarioPublished(ILogger logger, string runId);
}
