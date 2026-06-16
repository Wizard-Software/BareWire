namespace BareWire.CloudEvents;

/// <summary>
/// A dependency-injection marker that signals CloudEvents structured-mode (envelope) activation.
/// Registered as a singleton by <see cref="ServiceCollectionExtensions.AddCloudEventsEnvelope"/>.
/// </summary>
/// <remarks>
/// <para>
/// This marker serves two purposes:
/// <list type="bullet">
///   <item><description>
///     It is an explicit, resolvable signal that structured-mode was activated via
///     <c>AddCloudEventsEnvelope()</c>. Other pipeline components can depend on or resolve
///     this type to detect that the CE envelope routing is in place.
///   </description></item>
///   <item><description>
///     It provides the idempotency guard (GAP-4) used by <c>AddCloudEventsEnvelope()</c>:
///     a second call checks for the presence of this singleton descriptor and exits early,
///     preventing decorator stacking on the <see cref="BareWire.Abstractions.Serialization.IDeserializerResolver"/>.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// This mirrors the role of <see cref="CloudEventsBinaryActivation"/> for binary mode,
/// adapted for structured/envelope mode.
/// </para>
/// </remarks>
internal sealed class CloudEventsEnvelopeActivation
{
}
