using System.Text;
using Amazon.SQS.Model;
using BareWire.Abstractions.Exceptions;

namespace BareWire.Transport.AWS.SQS;

/// <summary>
/// Maps BareWire canonical headers to Amazon SQS <c>MessageAttributes</c> and vice versa.
/// </summary>
/// <remarks>
/// <para>
/// <b>Attribute limit (SEC-4 / OQ-2):</b> SQS allows at most 10 message attributes per message.
/// <see cref="MapOutbound"/> throws <see cref="BareWireTransportException"/> when the header count
/// exceeds 10. The exception message contains only the count — never header names or values —
/// to prevent accidental secret exposure (SEC-4).
/// </para>
/// <para>
/// <b>Body encoding (OQ-1):</b>
/// SQS <c>MessageBody</c> is a <c>string</c> field. BareWire <c>OutboundMessage.Body</c> is
/// <c>ReadOnlyMemory&lt;byte&gt;</c>. Encoding depends on the MIME <c>ContentType</c>:
/// <list type="bullet">
/// <item>
/// <term>Textual content types</term>
/// <description>
/// <c>application/json</c>, <c>text/*</c> — UTF-8 decoded directly to string via
/// <see cref="EncodeBodyAsString"/>. The body is assumed to be valid UTF-8.
/// </description>
/// </item>
/// <item>
/// <term>Binary content types</term>
/// <description>
/// <c>application/x-msgpack</c>, <c>application/octet-stream</c>, and any other non-textual
/// type — Base64-encoded to string via <see cref="EncodeBodyAsString"/>. The receiver must
/// reverse the encoding using <see cref="DecodeBodyBytes"/> based on the same
/// <c>ContentType</c> header.
/// </description>
/// </item>
/// </list>
/// This gate prevents silent corruption of binary payloads (e.g. MessagePack from R3)
/// that would occur if they were naively UTF-8-decoded.
/// </para>
/// </remarks>
internal sealed class SqsHeaderMapper
{
    private const int MaxSqsMessageAttributes = 10;

    /// <summary>
    /// Copies BareWire headers to a <see cref="Dictionary{TKey,TValue}"/> of
    /// <see cref="MessageAttributeValue"/> instances suitable for use in an SQS request.
    /// All values use <c>DataType = "String"</c>.
    /// </summary>
    /// <param name="bareWireHeaders">The BareWire headers from the outbound message. Must not be null.</param>
    /// <returns>
    /// A <see cref="Dictionary{TKey,TValue}"/> of attribute name → <see cref="MessageAttributeValue"/>.
    /// Returns an empty dictionary when <paramref name="bareWireHeaders"/> is empty.
    /// </returns>
    /// <exception cref="BareWireTransportException">
    /// Thrown when the header count exceeds 10 (SQS hard limit). The exception message contains
    /// only the count — never header names or values (SEC-4).
    /// </exception>
    internal static Dictionary<string, MessageAttributeValue> MapOutbound(
        IReadOnlyDictionary<string, string> bareWireHeaders)
    {
        ArgumentNullException.ThrowIfNull(bareWireHeaders);

        if (bareWireHeaders.Count > MaxSqsMessageAttributes)
        {
            // SEC-4: message contains ONLY the count — never header names or values.
            throw new BareWireTransportException(
                message: $"Cannot send SQS message: {bareWireHeaders.Count} headers exceed the " +
                         $"SQS limit of {MaxSqsMessageAttributes} message attributes per message.",
                transportName: "AWS.SQS",
                endpointAddress: null);
        }

        var result = new Dictionary<string, MessageAttributeValue>(
            bareWireHeaders.Count, StringComparer.Ordinal);

        foreach (KeyValuePair<string, string> header in bareWireHeaders)
        {
            result[header.Key] = new MessageAttributeValue
            {
                DataType = "String",
                StringValue = header.Value,
            };
        }

        return result;
    }

    /// <summary>
    /// Maps the <c>MessageAttributes</c> of a received SQS message to a BareWire header dictionary.
    /// All attribute values are extracted from the <c>StringValue</c> field.
    /// </summary>
    /// <param name="messageAttributes">
    /// The message attributes from a received SQS <c>Message</c>.
    /// May be <see langword="null"/> or empty.
    /// </param>
    /// <returns>
    /// A <see cref="Dictionary{TKey,TValue}"/> (Ordinal) of BareWire header name → string value.
    /// Returns an empty dictionary when <paramref name="messageAttributes"/> is null or empty.
    /// </returns>
    internal static Dictionary<string, string> MapInbound(
        IReadOnlyDictionary<string, MessageAttributeValue>? messageAttributes)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (messageAttributes is null)
        {
            return result;
        }

        foreach (KeyValuePair<string, MessageAttributeValue> attr in messageAttributes)
        {
            result[attr.Key] = attr.Value.StringValue ?? string.Empty;
        }

        return result;
    }

    /// <summary>
    /// Encodes a BareWire message body (<see cref="ReadOnlyMemory{T}"/>) to a string suitable
    /// for the SQS <c>MessageBody</c> field, based on the MIME <paramref name="contentType"/>.
    /// </summary>
    /// <param name="body">The raw serialized body bytes.</param>
    /// <param name="contentType">The MIME content type of the body (e.g. <c>application/json</c>).</param>
    /// <returns>
    /// <list type="bullet">
    /// <item>
    /// <term>Textual content types (<c>application/json</c>, <c>text/*</c>)</term>
    /// <description>UTF-8 decoded string.</description>
    /// </item>
    /// <item>
    /// <term>Binary content types (all others)</term>
    /// <description>
    /// Base64-encoded string. The receiver must call <see cref="DecodeBodyBytes"/> with the same
    /// <paramref name="contentType"/> to recover the original bytes.
    /// </description>
    /// </item>
    /// </list>
    /// </returns>
    internal static string EncodeBodyAsString(ReadOnlyMemory<byte> body, string contentType)
    {
        if (IsTextualContentType(contentType))
        {
            return Encoding.UTF8.GetString(body.Span);
        }

        // Binary content type (e.g. application/x-msgpack, application/octet-stream) —
        // Base64-encode to avoid silent UTF-8 corruption of binary payloads.
        return Convert.ToBase64String(body.Span);
    }

    /// <summary>
    /// Decodes the SQS <c>MessageBody</c> string back to raw bytes based on the MIME
    /// <paramref name="contentType"/>. Reverses <see cref="EncodeBodyAsString"/>.
    /// </summary>
    /// <param name="messageBody">The SQS message body string.</param>
    /// <param name="contentType">The MIME content type from the message headers.</param>
    /// <returns>The decoded raw body bytes.</returns>
    internal static ReadOnlyMemory<byte> DecodeBodyBytes(string messageBody, string contentType)
    {
        if (IsTextualContentType(contentType))
        {
            return Encoding.UTF8.GetBytes(messageBody);
        }

        return Convert.FromBase64String(messageBody);
    }

    private static bool IsTextualContentType(string contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        // Normalize: strip parameters (e.g. "; charset=utf-8")
        int semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        ReadOnlySpan<char> mediaType = semicolon >= 0
            ? contentType.AsSpan(0, semicolon).Trim()
            : contentType.AsSpan().Trim();

        return mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase);
    }
}
