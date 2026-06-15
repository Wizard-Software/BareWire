using BareWire.Abstractions;

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
    public static ICloudEventAttributes? GetCloudEvent(this ConsumeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return CloudEventBinaryHeaderMapper.TryFromHeaders(context.Headers, out ICloudEventAttributes? attrs)
            ? attrs
            : null;
    }
}
