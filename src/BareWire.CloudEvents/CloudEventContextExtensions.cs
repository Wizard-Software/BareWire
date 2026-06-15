using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;

namespace BareWire.CloudEvents;

/// <summary>
/// Provides extension methods on <see cref="ConsumeContext"/> for accessing CloudEvents 1.0
/// attributes encoded in the standard <c>ce-*</c> transport headers.
/// </summary>
/// <remarks>
/// Header values are sender-controlled and therefore constitute an external trust boundary.
/// All parsing is delegated to <see cref="CloudEventBinaryHeaderMapper.TryFromHeaders"/>, which uses
/// defensive <c>Try*</c> methods — no call site ever throws on malformed input. If parsing fails,
/// the message is treated as a non-CloudEvent and <see langword="null"/> is returned.
/// </remarks>
public static class CloudEventContextExtensions
{
    /// <summary>
    /// Attempts to extract CloudEvents 1.0 context attributes from the <c>ce-*</c> transport headers
    /// of the consumed message.
    /// </summary>
    /// <param name="context">The consume context of the inbound message. Must not be <see langword="null"/>.</param>
    /// <returns>
    /// An <see cref="ICloudEventAttributes"/> instance populated from the <c>ce-*</c> headers,
    /// or <see langword="null"/> when:
    /// <list type="bullet">
    ///   <item><description>Any of the four mandatory CE headers (<c>ce-id</c>, <c>ce-source</c>,
    ///   <c>ce-specversion</c>, <c>ce-type</c>) is absent.</description></item>
    ///   <item><description>A mandatory header is present but its value cannot be parsed
    ///   (e.g. <c>ce-source</c> is not a valid URI).</description></item>
    /// </list>
    /// This method never throws on malformed header values — the contract is "return null, never throw".
    /// Optional attributes that fail to parse are silently omitted (left <see langword="null"/>).
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="context"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// This method does NOT validate the <c>specversion</c> value — it returns non-null even
    /// when <c>ce-specversion</c> is not <c>"1.0"</c>. Use
    /// <see cref="GetCloudEventOrThrow"/> to enforce CE 1.0 compliance and reject unsupported
    /// spec versions.
    /// </remarks>
    public static ICloudEventAttributes? GetCloudEvent(this ConsumeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return CloudEventBinaryHeaderMapper.TryFromHeaders(context.Headers, out ICloudEventAttributes? attrs)
            ? attrs
            : null;
    }

    /// <summary>
    /// Extracts and validates CloudEvents 1.0 context attributes from the <c>ce-*</c> transport
    /// headers of the consumed message, throwing on any validation failure.
    /// </summary>
    /// <param name="context">The consume context of the inbound message. Must not be <see langword="null"/>.</param>
    /// <returns>
    /// A validated <see cref="ICloudEventAttributes"/> instance populated from the <c>ce-*</c> headers.
    /// All four mandatory CE 1.0 attributes are guaranteed to be present and <c>specversion</c> is
    /// guaranteed to equal <c>"1.0"</c>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="context"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="BareWireSerializationException">
    /// Thrown when:
    /// <list type="bullet">
    ///   <item><description>Any of the four mandatory CE headers (<c>ce-id</c>, <c>ce-source</c>,
    ///   <c>ce-specversion</c>, <c>ce-type</c>) is absent or unparseable — the message is not a
    ///   valid CloudEvent.</description></item>
    ///   <item><description>The <c>ce-specversion</c> header is present but its value is not
    ///   <c>"1.0"</c> — the specversion is unsupported.</description></item>
    ///   <item><description>Any mandatory attribute value is empty or whitespace after
    ///   parsing.</description></item>
    /// </list>
    /// Exception messages that echo sender-controlled values (e.g. <c>specversion</c>) are sanitized
    /// (capped to 32 characters, control/CR/LF characters replaced) to prevent log-injection (SEC-1).
    /// </exception>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="GetCloudEvent"/>, which follows the SEC-1 "return null, never throw"
    /// contract, this method is the opt-in throwing variant intended for consumers that require
    /// strict CE 1.0 compliance. It closes the zero-call-site gap in
    /// <c>CloudEventAttributeValidator.ValidateMandatory</c> (task 13.3): calling this method
    /// is the only production path that enforces specversion validation on the read side.
    /// </para>
    /// <para>
    /// The <c>ce-*</c> header parsing is still performed via defensive <c>Try*</c> methods
    /// (same as <see cref="GetCloudEvent"/>); failures are converted to
    /// <see cref="BareWireSerializationException"/> rather than silently returning
    /// <see langword="null"/>.
    /// </para>
    /// </remarks>
    public static ICloudEventAttributes GetCloudEventOrThrow(this ConsumeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!CloudEventBinaryHeaderMapper.TryFromHeaders(context.Headers, out ICloudEventAttributes? attrs))
        {
            throw new BareWireSerializationException(
                "Message is not a valid CloudEvent: mandatory ce-* headers missing or unparseable.",
                CloudEventsBinaryContentType.Value);
        }

        CloudEventAttributeValidator.ValidateMandatory(attrs!, CloudEventsBinaryContentType.Value);
        return attrs!;
    }
}
