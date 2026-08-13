using BareWire.Abstractions.Configuration;
using BareWire.Samples.ConsumerDefinitionShowcase.Consumers;

namespace BareWire.Samples.ConsumerDefinitionShowcase.Definitions;

/// <summary>
/// Colocates <see cref="TransferConsumer"/>'s per-consumer settings — routing-key patterns and its
/// retry policy — in a single, discoverable block next to the consumer itself.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Discovered only via explicit DI registration</strong> — there is no assembly scanning.
/// <c>Program.cs</c> registers this type as
/// <c>builder.Services.AddSingleton&lt;ConsumerDefinition&lt;TransferConsumer&gt;, TransferConsumerDefinition&gt;()</c>;
/// the core resolves it once at bus start-up and merges its settings into the
/// <see cref="TransferConsumer"/> registration created by
/// <c>e.Consumer&lt;TransferConsumer, TransferInitiated&gt;(...)</c> on the receive endpoint.
/// </para>
/// <para>
/// Two configuration axes are grouped here:
/// <list type="number">
///   <item>
///     <strong>Routing keys</strong> (accumulating, client-side dispatcher predicate) —
///     <c>"transfer.eu.*"</c> (wildcard) and the exact <c>"transfer.eu.priority"</c>.
///   </item>
///   <item>
///     <strong>Retry policy</strong> (scalar, last-call-wins) — a short exponential backoff
///     (4 attempts, 200 ms to 2 s) chosen so the sample's fail-then-succeed proof completes well
///     within the smoke test's outer timeout.
///   </item>
/// </list>
/// The opt-in transport topology (<c>DeclareTopology</c>) is a separate, transport-level seam applied
/// at endpoint registration in <c>Program.cs</c> — it is not a member of this transport-agnostic type.
/// </para>
/// </remarks>
internal sealed class TransferConsumerDefinition : ConsumerDefinition<TransferConsumer>
{
    protected override void Configure(
        IReceiveEndpointConfigurator endpoint,
        IConsumerConfigurator<TransferConsumer> consumer)
    {
        // Axis 1 — routing keys (dispatcher predicate, client-side, accumulates).
        consumer.RoutingKeys("transfer.eu.*", "transfer.eu.priority");

        // Axis 2 — retry (scalar, last-call-wins). Exponential(retryCount, minInterval, maxInterval).
        consumer.Retry(r => r.Exponential(4, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2)));
    }
}
