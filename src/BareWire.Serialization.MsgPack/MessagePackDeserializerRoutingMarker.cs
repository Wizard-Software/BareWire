namespace BareWire.Serialization.MsgPack;

/// <summary>
/// A dependency-injection marker that signals MessagePack Content-Type routing activation.
/// Registered as a singleton by <see cref="ServiceCollectionExtensions.AddBareWireMessagePackDeserializerRouting"/>.
/// </summary>
/// <remarks>
/// <para>
/// This marker serves as the idempotency guard used by
/// <c>AddBareWireMessagePackDeserializerRouting()</c>: a second call checks for the presence
/// of this singleton descriptor and exits early, preventing decorator stacking on the
/// <see cref="BareWire.Abstractions.Serialization.IDeserializerResolver"/>.
/// </para>
/// <para>
/// A factory-based descriptor has <c>ImplementationType == null</c>, so descriptor-type
/// inspection cannot detect the decorator. This marker is the only reliable idempotency guard.
/// Mirrors the role of <c>CloudEventsEnvelopeActivation</c> in <c>BareWire.CloudEvents</c>.
/// </para>
/// </remarks>
internal sealed class MessagePackDeserializerRoutingMarker
{
}
