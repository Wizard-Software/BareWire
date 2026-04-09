namespace BareWire.Samples.MassTransitInterop.Messages;

/// <summary>
/// Dedicated message type for the publish-only bridge demo.
/// Kept separate from <see cref="OrderCreated"/> so that the per-type serializer mapping
/// (<c>MapSerializer&lt;BridgeOrderCreated, MassTransitEnvelopeSerializer&gt;()</c>) only affects
/// the bridge endpoint and does not change the serialization of <c>OrderCreated</c> messages
/// published via <c>/barewire/publish</c> (which must stay raw JSON per ADR-001).
/// </summary>
public record BridgeOrderCreated(string OrderId, decimal Amount, string Currency);
