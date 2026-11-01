using System.Buffers;
using System.Collections.Concurrent;
using BareWire.Abstractions.Transport;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// The receiving side of one in-memory request client: the registry hands a reply to it by address, and
/// the client correlates the reply with its pending request.
/// </summary>
internal interface IInMemoryReplySink
{
    /// <summary>
    /// Cheap pre-check, performed before the registry copies the reply body: returns whether the headers
    /// carry a correlation that matches a request the sink is still waiting for.
    /// </summary>
    /// <param name="headers">The publisher-supplied headers of the reply.</param>
    bool CanAccept(IReadOnlyDictionary<string, string> headers);

    /// <summary>
    /// Offers the reply to the sink. Returns <see langword="false"/> when the reply does not match a pending
    /// request (late, duplicate or unknown); the caller then disposes the message. When it returns
    /// <see langword="true"/>, the sink owns <paramref name="response"/> and must dispose it.
    /// </summary>
    /// <param name="response">The reply, backed by a pooled buffer owned by the message.</param>
    bool TryAccept(InboundMessage response);
}

/// <summary>The outcome of <see cref="InMemoryReplyRegistry.TryDeliver"/>.</summary>
internal enum ReplyDeliveryResult
{
    /// <summary>The reply was accepted by a registered request client.</summary>
    Delivered,

    /// <summary>The address is registered but the reply matches no pending request; the reply was dropped.</summary>
    UnknownCorrelation,

    /// <summary>No client is registered for the address (for example a reply after the client was disposed); the reply was dropped.</summary>
    UnknownAddress,
}

/// <summary>
/// Maps reply addresses (<c>bw.reply.</c> followed by 32 lowercase hexadecimal characters) to the in-memory
/// request clients that own them. The sender consults it before routing: a send to the default exchange whose
/// routing key is a reply address never reaches the topology, it is handed to the owning client instead.
/// </summary>
/// <remarks>
/// Logs never contain the reply address, correlation values or body. One instance per container, shared by the
/// adapter (through its sender) and the request client factory — never a static.
/// </remarks>
internal sealed partial class InMemoryReplyRegistry
{
    /// <summary>The queue-name prefix reserved for reply addresses.</summary>
    internal const string ReservedPrefix = "bw.reply.";

    // A Guid formatted with "N": 32 lowercase hexadecimal characters.
    private const int GuidDigits = 32;

    private readonly ConcurrentDictionary<string, IInMemoryReplySink> _sinks = new(StringComparer.Ordinal);
    private readonly ILogger<InMemoryReplyRegistry> _logger;

    /// <param name="logger">Receives the Debug entries describing dropped replies. Must not be <see langword="null"/>.</param>
    internal InMemoryReplyRegistry(ILogger<InMemoryReplyRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>Gets the number of currently registered reply addresses (test hook).</summary>
    internal int Count => _sinks.Count;

    /// <summary>
    /// Returns whether a send with the given exchange and routing key targets a reply address: the default
    /// exchange (empty name) and a routing key of exactly <see cref="ReservedPrefix"/> followed by 32
    /// lowercase hexadecimal characters, compared ordinally. Anything else takes the normal routing path.
    /// </summary>
    internal static bool IsReplyAddress(string exchange, string routingKey)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(routingKey);

        if (exchange.Length != 0 || routingKey.Length != ReservedPrefix.Length + GuidDigits)
        {
            return false;
        }

        if (!routingKey.StartsWith(ReservedPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char c in routingKey.AsSpan(ReservedPrefix.Length))
        {
            if (!char.IsAsciiDigit(c) && !(c >= 'a' && c <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns whether a queue name uses the reserved prefix (ordinal, case-sensitive). The topology
    /// interpreter rejects such names so a declared queue can never collide with a reply address.
    /// </summary>
    internal static bool IsReservedName(string queueName)
    {
        ArgumentNullException.ThrowIfNull(queueName);
        return queueName.StartsWith(ReservedPrefix, StringComparison.Ordinal);
    }

    /// <summary>Registers <paramref name="sink"/> under a fresh, unpredictable reply address and returns it.</summary>
    internal string Register(IInMemoryReplySink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        while (true)
        {
            string address = ReservedPrefix + Guid.NewGuid().ToString("N");
            if (_sinks.TryAdd(address, sink))
            {
                return address;
            }
        }
    }

    /// <summary>Removes <paramref name="address"/>; returns whether it was registered.</summary>
    internal bool Unregister(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return _sinks.TryRemove(address, out _);
    }

    /// <summary>
    /// Hands a reply to the client registered for <paramref name="address"/>. The body is copied into a
    /// buffer rented from <paramref name="pool"/> and owned by the delivered <see cref="InboundMessage"/>;
    /// the copy is made only after the client confirmed the correlation matches a pending request.
    /// </summary>
    /// <param name="address">The reply address (the send's routing key).</param>
    /// <param name="headers">The publisher-supplied headers.</param>
    /// <param name="body">The reply body; copied, never retained.</param>
    /// <param name="contentType">The reply content type; wins over a publisher <c>content-type</c> header when non-empty.</param>
    /// <param name="pool">The sender's buffer pool the body copy is rented from.</param>
    internal ReplyDeliveryResult TryDeliver(
        string address,
        IReadOnlyDictionary<string, string> headers,
        ReadOnlySpan<byte> body,
        string contentType,
        InMemoryBufferPool pool)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(contentType);
        ArgumentNullException.ThrowIfNull(pool);

        if (!_sinks.TryGetValue(address, out IInMemoryReplySink? sink))
        {
            LogDroppedUnknownAddress();
            return ReplyDeliveryResult.UnknownAddress;
        }

        if (!sink.CanAccept(headers))
        {
            LogDroppedUnknownCorrelation();
            return ReplyDeliveryResult.UnknownCorrelation;
        }

        Dictionary<string, string> inboundHeaders = BuildHeaders(headers, contentType);

        byte[] buffer = pool.Rent(body.Length);
        body.CopyTo(buffer);
        var message = new InboundMessage(
            inboundHeaders[InMemoryHeaderNames.MessageId],
            inboundHeaders,
            new ReadOnlySequence<byte>(buffer, 0, body.Length),
            deliveryTag: 0,
            pooledBuffer: buffer);

        bool accepted;
        try
        {
            accepted = sink.TryAccept(message);
        }
        catch
        {
            message.Dispose();
            throw;
        }

        if (accepted)
        {
            return ReplyDeliveryResult.Delivered;
        }

        message.Dispose();
        LogDroppedUnknownCorrelation();
        return ReplyDeliveryResult.UnknownCorrelation;
    }

    // Mirrors InMemoryHeaderSet.Stamp for an inbound copy: every BW- header is stripped (case-insensitively)
    // except the exact BW-MessageType, a non-empty content type wins over the publisher's header, and a
    // message id is always present (the publisher's non-empty value, else a generated one).
    private static Dictionary<string, string> BuildHeaders(IReadOnlyDictionary<string, string> publisherHeaders, string contentType)
    {
        var result = new Dictionary<string, string>(publisherHeaders.Count + 2, StringComparer.Ordinal);

        foreach (KeyValuePair<string, string> entry in publisherHeaders)
        {
            bool reserved = entry.Key.StartsWith(InMemoryHeaderNames.ReservedPrefix, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entry.Key, InMemoryHeaderNames.MessageType, StringComparison.Ordinal);
            if (!reserved)
            {
                result[entry.Key] = entry.Value;
            }
        }

        if (contentType.Length > 0)
        {
            result[InMemoryHeaderNames.ContentType] = contentType;
        }

        if (!result.TryGetValue(InMemoryHeaderNames.MessageId, out string? messageId) || string.IsNullOrEmpty(messageId))
        {
            result[InMemoryHeaderNames.MessageId] = Guid.NewGuid().ToString();
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "A reply to an unregistered in-memory reply address was dropped.")]
    private partial void LogDroppedUnknownAddress();

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "A reply that matches no pending in-memory request was dropped.")]
    private partial void LogDroppedUnknownCorrelation();
}
