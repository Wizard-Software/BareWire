using BareWire.Abstractions.Transport;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Internal;
using InMemoryAdapter = BareWire.Transport.InMemory.InMemoryTransportAdapter;

namespace BareWire.IntegrationTests.Transport.Parity;

/// <summary>
/// In-memory transport factory for <see cref="TransportSemanticParityTests"/>. Builds a fresh
/// <see cref="InMemoryTransportOptions"/> / <see cref="InMemoryBroker"/> / <see cref="InMemoryAdapter"/>
/// per test from the transport-neutral <see cref="ParitySetup"/>, and reports the fixed reasons the
/// in-memory transport skips a row of the "differences vs RabbitMQ" table.
/// </summary>
/// <remarks>
/// Not the same type as <c>BareWire.Testing.InMemoryTransportAdapter</c> — this class always
/// references <see cref="InMemoryAdapter"/>, the internal transport type aliased at the top of this
/// file, never the DI-facing test-harness wrapper.
/// </remarks>
public sealed class InMemoryTransportSemanticParityTests : TransportSemanticParityTests
{
    private const string DurabilityReason =
        "the in-memory transport is explicitly at-most-once: a new adapter starts a new broker with no persisted state.";

    private const string FullDeadLetterQueueReason =
        "x-max-length is not honoured by the in-memory transport (rejected at topology build time).";

    private const string UnboundedRequeueReason =
        "in-memory Requeue always enforces MaxRedeliveries and dead-letters past the limit; it has no unbounded classic-queue mode.";

    private const string BwHeaderStripReason =
        "the in-memory transport strips every publisher-supplied BW-* header (case-insensitively) as its anti-spoofing default.";

    private const string RuntimeTopologyReason =
        "the in-memory topology is sealed when the adapter is built; DeployTopologyAsync accepts only an identical declaration afterwards.";

    private const string HeadersExchangeTtlMaxLengthReason =
        "the in-memory transport rejects ExchangeType.Headers and the x-message-ttl / x-max-length queue arguments at topology build time.";

    private const string ConfidentialityReason =
        "all publishers and consumers wired through the in-memory transport share the trust boundary of a single process; there is no virtual-host isolation to deny.";

    /// <inheritdoc />
    protected override async Task<ITransportAdapter> CreateAdapterAsync(ParitySetup setup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setup);

        var options = new InMemoryTransportOptions
        {
            Topology = setup.Topology,
            GuaranteedRouting = setup.GuaranteedRouting,
        };

        // Assign only what the scenario actually sets. Every other option keeps its documented
        // default (InMemoryTransportOptions.DefaultQueueCapacity / DefaultMaxRedeliveries) — never the
        // uninitialized 0, which Validate() below would reject anyway.
        if (setup.BoundedQueues.Count > 0)
        {
            options.QueueCapacity = setup.BoundedQueues.Values.Min();
        }

        if (setup.MaxRedeliveries is int maxRedeliveries)
        {
            options.MaxRedeliveries = maxRedeliveries;
        }

        if (setup.Defer is ParityDefer defer)
        {
            options.DeferEnabled = true;
            options.DeferDelay = defer.Delay;
        }

        options.Validate();

        InMemoryBroker broker = new(options);
        InMemoryAdapter adapter = new(options, broker);

        // No-op: the registry was already built from this exact Topology instance at construction, so
        // this only exercises the same idempotent path a real caller (and the RabbitMQ factory) uses.
        await adapter.DeployTopologyAsync(setup.Topology, cancellationToken);

        return adapter;
    }

    /// <inheritdoc />
    protected override string? SkipReason(ParityDifference difference) => difference switch
    {
        ParityDifference.Durability => DurabilityReason,
        ParityDifference.FullDeadLetterQueue => FullDeadLetterQueueReason,
        ParityDifference.UnboundedRequeue => UnboundedRequeueReason,
        ParityDifference.BoundedRequeue => null,
        ParityDifference.BwHeaderStrip => BwHeaderStripReason,
        ParityDifference.RuntimeTopology => RuntimeTopologyReason,
        ParityDifference.HeadersExchangeTtlMaxLength => HeadersExchangeTtlMaxLengthReason,
        ParityDifference.Confidentiality => ConfidentialityReason,
        _ => throw new ArgumentOutOfRangeException(nameof(difference), difference, "Unknown parity difference."),
    };
}
