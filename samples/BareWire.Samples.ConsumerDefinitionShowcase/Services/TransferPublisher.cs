using System.Text.Json;
using BareWire.Samples.ConsumerDefinitionShowcase.Messages;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

// RabbitMQ.Client.ExchangeType is a static class with string constants (e.g. "topic").
// BareWire.Abstractions.ExchangeType is the BareWire enum used in Program.cs's DeclareTopology call.
// The two types are in separate files so there is no ambiguous-reference collision.

namespace BareWire.Samples.ConsumerDefinitionShowcase.Services;

/// <summary>
/// Simulates a non-BareWire upstream producer publishing directly to the exchange declared
/// (opt-in) by <c>DeclareTopology</c> in <c>Program.cs</c>, using <c>RabbitMQ.Client</c>.
/// </summary>
/// <remarks>
/// <para>
/// SECURITY: Never log the connection string — it contains credentials.
/// </para>
/// <para>
/// <strong>CRITICAL — exchange declaration must match <c>DeclareTopology</c> exactly.</strong> This
/// publisher declares the exchange with <em>bit-identical</em> parameters to the
/// <c>c.DeclareTopology(...)</c> call in <c>Program.cs</c>: <c>type=topic, durable=false,
/// autoDelete=false</c>. Any parameter mismatch — including <c>autoDelete</c> — causes the broker to
/// reject the second declaration with <c>PRECONDITION_FAILED</c> (406).
/// </para>
/// </remarks>
internal sealed partial class TransferPublisher(
    IConfiguration configuration,
    ILogger<TransferPublisher> logger) : IAsyncDisposable
{
    private const string ExchangeName = "consumer-definition-showcase.exchange";

    // Camelcase options for STJ: matches JsonSerializerDefaults.Web used by the BareWire raw-first
    // deserializer so property names round-trip correctly.
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private IConnection? _connection;
    private IChannel? _channel;

    /// <summary>
    /// Publishes a single <see cref="TransferInitiated"/> delivery for the given
    /// <paramref name="runId"/> on routing key <c>transfer.eu.priority</c> — matched by both
    /// routing-key patterns declared on <c>TransferConsumerDefinition</c> (the wildcard
    /// <c>"transfer.eu.*"</c> and the exact <c>"transfer.eu.priority"</c>); since
    /// <c>TransferConsumer</c> is the only consumer registered on this endpoint, the
    /// most-specific-wins tie-break is not exercised here.
    /// </summary>
    public async Task PublishTransferAsync(string runId, CancellationToken cancellationToken)
    {
        IChannel channel = await GetOrCreateChannelAsync(cancellationToken).ConfigureAwait(false);

        TransferInitiated message = new(
            RunId: runId,
            TransferId: Guid.NewGuid().ToString("N"),
            Region: "eu",
            Amount: 5000m);

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, SerializeOptions);

        BasicProperties props = new()
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Transient,
            Headers = new Dictionary<string, object?>
            {
                // BW-MessageType = simple type name — identical to what BareWireBus sets on publish.
                // The dispatcher matches this value (Ordinal) against the consumer's message type
                // name to select typed consumers before falling through to the type-less layer.
                ["BW-MessageType"] = nameof(TransferInitiated),
            },
        };

        await channel.BasicPublishAsync<BasicProperties>(
            exchange: ExchangeName,
            routingKey: "transfer.eu.priority",
            mandatory: false,
            basicProperties: props,
            body: body,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        LogPublished(logger, runId);
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

        // Bit-identical to the DeclareTopology call in Program.cs: Topic / durable:false / autoDelete:false.
        // ExchangeType here is RabbitMQ.Client.ExchangeType (static class with string constants),
        // imported via 'using RabbitMQ.Client;' above — a separate file from Program.cs, which uses
        // BareWire.Abstractions.ExchangeType (enum). No ambiguous-reference collision.
        await _channel.ExchangeDeclareAsync(
            exchange: ExchangeName,
            type: ExchangeType.Topic,
            durable: false,
            autoDelete: false,
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
        Message = "TransferPublisher: connected to RabbitMQ broker")]
    private static partial void LogConnected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "TransferPublisher: published transfer scenario for run {RunId}")]
    private static partial void LogPublished(ILogger logger, string runId);
}
