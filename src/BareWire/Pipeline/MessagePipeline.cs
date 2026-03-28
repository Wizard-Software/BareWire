using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Observability;
using BareWire.Abstractions.Pipeline;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Buffers;
using Microsoft.Extensions.Logging;

namespace BareWire.Pipeline;

// Orchestrates the full inbound and outbound message lifecycle:
// inbound: transport → middleware chain → settlement
// outbound: serialize via pooled writer → OutboundMessage (ready for the bounded publish channel)
internal sealed partial class MessagePipeline
{
    private static readonly IReadOnlyDictionary<string, string> s_emptyHeaders =
        new Dictionary<string, string>();

    private readonly MiddlewareChain _middlewareChain;
    private readonly IDeserializerResolver _deserializerResolver;
    private readonly ILogger<MessagePipeline> _logger;
    private readonly IBareWireInstrumentation _instrumentation;

    internal MessagePipeline(
        MiddlewareChain middlewareChain,
        IDeserializerResolver deserializerResolver,
        ILogger<MessagePipeline> logger,
        IBareWireInstrumentation instrumentation)
    {
        _middlewareChain = middlewareChain ?? throw new ArgumentNullException(nameof(middlewareChain));
        _deserializerResolver = deserializerResolver ?? throw new ArgumentNullException(nameof(deserializerResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _instrumentation = instrumentation ?? throw new ArgumentNullException(nameof(instrumentation));
    }

    // Processes an inbound message through the middleware chain and settles it with the transport.
    // Returns the settlement action that was applied.
    internal async Task<SettlementAction> ProcessInboundAsync(
        InboundMessage message,
        ITransportAdapter adapter,
        IServiceProvider serviceProvider,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        Guid messageId = ParseOrHashMessageId(message.MessageId);

        // Derive message type and endpoint from headers / routing key for metrics tags.
        message.Headers.TryGetValue("BW-MessageType", out string? msgType);
        string messageType = msgType ?? "unknown";
        string endpoint = message.Headers.TryGetValue("BW-Endpoint", out string? ep) ? ep : "unknown";

        int bodyLength = (int)Math.Min(message.Body.Length, int.MaxValue);

        _instrumentation.RecordInflight(endpoint, messageType, +1, bodyLength);

        Activity? activity = _instrumentation.StartConsumeActivity(messageType, endpoint, messageId, message.Headers);
        Stopwatch sw = Stopwatch.StartNew();

        MessageContext context = new(
            messageId: messageId,
            headers: message.Headers,
            rawBody: message.Body,
            serviceProvider: serviceProvider,
            cancellationToken: ct);

        // The terminator is a no-op placeholder. Higher-level wiring (BareWireBus / receive endpoint
        // configuration) injects the actual typed dispatch via middleware registered before this call.
        // For now the terminator simply completes to allow middleware under test to run end-to-end.
        NextMiddleware terminator = static _ => Task.CompletedTask;

        SettlementAction action;
        try
        {
            await _middlewareChain.InvokeAsync(context, terminator).ConfigureAwait(false);
            action = SettlementAction.Ack;
        }
        catch (UnknownPayloadException ex)
        {
            LogUnknownPayload(_logger, ex, messageId);
            _instrumentation.RecordFailure(endpoint, messageType, ex.GetType().Name);
            action = SettlementAction.Reject;
        }
        catch (Exception ex)
        {
            LogProcessingError(_logger, ex, messageId);
            _instrumentation.RecordFailure(endpoint, messageType, ex.GetType().Name);
            action = SettlementAction.Nack;
        }
        finally
        {
            sw.Stop();
            _instrumentation.RecordConsume(endpoint, messageType, sw.Elapsed.TotalMilliseconds, bodyLength);
            _instrumentation.RecordInflight(endpoint, messageType, -1, -bodyLength);
            activity?.Dispose();
        }

        await adapter.SettleAsync(action, message, ct).ConfigureAwait(false);
        return action;
    }

    // Serializes a typed message into an OutboundMessage using a pooled buffer.
    // The returned OutboundMessage owns its body bytes (copied from the pool at the serialization boundary).
    // Callers are responsible for routing the OutboundMessage through the publish channel (ADR-006).
    internal static OutboundMessage ProcessOutboundAsync<T>(
        T message,
        IMessageSerializer serializer,
        string routingKey,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(routingKey);

        using PooledBufferWriter writer = new();
        serializer.Serialize(message, writer);

        // Copy from pooled buffer to owned memory at the serialization boundary.
        // The OutboundMessage will outlive the using scope, so we must own the bytes.
        ReadOnlyMemory<byte> body = writer.WrittenMemory.ToArray();

        return new OutboundMessage(
            routingKey: routingKey,
            headers: headers ?? s_emptyHeaders,
            body: body,
            contentType: serializer.ContentType);
    }

    // Converts a transport-provided string message ID to a Guid.
    // If the string is a valid Guid format it is parsed directly.
    // Otherwise a deterministic Guid is derived from the first 16 bytes of the SHA256 hash of the ID string,
    // ensuring we never throw FormatException and always produce a stable, collision-resistant identifier.
    private static Guid ParseOrHashMessageId(string messageId)
    {
        if (Guid.TryParse(messageId, out Guid parsed))
            return parsed;

        byte[] idBytes = Encoding.UTF8.GetBytes(messageId);
        byte[] hash = SHA256.HashData(idBytes);

        // Use the first 16 bytes of the hash as the Guid bytes.
        return new Guid(hash.AsSpan(0, 16));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unknown payload received for message {MessageId}. Rejecting.")]
    private static partial void LogUnknownPayload(ILogger logger, Exception ex, Guid messageId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error processing message {MessageId}. Nacking.")]
    private static partial void LogProcessingError(ILogger logger, Exception ex, Guid messageId);
}
