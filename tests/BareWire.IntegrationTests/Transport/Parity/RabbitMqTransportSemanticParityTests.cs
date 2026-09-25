using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.IntegrationTests.Transport.Parity;

/// <summary>
/// RabbitMQ factory for <see cref="TransportSemanticParityTests"/>. Builds a real
/// <see cref="RabbitMqTransportAdapter"/> against the shared test broker provisioned by
/// <see cref="AspireFixture"/>, augmenting the transport-neutral <see cref="ParitySetup"/> topology
/// with the RabbitMQ-specific declarations (bounded-queue overflow, deferred-redelivery DLX/TTL
/// chain) that the in-memory transport implements natively but RabbitMQ needs wired up by hand.
/// </summary>
/// <remarks>
/// RabbitMQ is the reference transport for this suite: every parity scenario (P1-P16) runs and is
/// asserted identically here, and every difference test (D1-D7) either runs and asserts the real
/// broker behavior or is skipped for one of the two structural reasons this class returns —
/// <see cref="ParityDifference.BoundedRequeue"/> (a classic queue has no delivery-limit concept) and
/// <see cref="ParityDifference.Confidentiality"/> (the shared broker provisions no extra virtual
/// hosts or restricted credentials for this suite).
/// </remarks>
public sealed class RabbitMqTransportSemanticParityTests(AspireFixture fixture)
    : TransportSemanticParityTests, IClassFixture<AspireFixture>
{
    private const string BoundedRequeueReason =
        "a classic RabbitMQ queue has no delivery-limit concept, so nothing dead-letters a Requeue " +
        "past a redelivery count on this transport — that bounded-redelivery behavior exists only on " +
        "the in-memory transport.";

    private const string ConfidentialityReason =
        "the shared test broker provisions no additional virtual hosts or restricted credentials for " +
        "this suite; this row is documentation only on every transport this suite runs against.";

    /// <summary>The BareWire canonical header the D4 scenario forges, and the AMQP header name it is mapped to.</summary>
    private const string ForgedHeaderName = "BW-Forged";
    private const string ForgedHeaderTransportName = "x-bw-forged";

    /// <inheritdoc />
    protected override async Task<ITransportAdapter> CreateAdapterAsync(ParitySetup setup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setup);

        string connectionString = fixture.GetRabbitMqConnectionString();

        var headerMappingConfigurator = new RabbitMqHeaderMappingConfigurator();
        // The forged canonical header is stripped as a reserved "BW-" header on send and dropped on
        // receive unless mapped — mapping it to a broker-native name is what lets it round-trip in
        // both directions.
        headerMappingConfigurator.MapHeader(ForgedHeaderName, ForgedHeaderTransportName);
        var headerMapper = new RabbitMqHeaderMapper(headerMappingConfigurator);

        var options = new RabbitMqTransportOptions
        {
            ConnectionString = connectionString,
            GuaranteedRouting = setup.GuaranteedRouting,
            DeferEnabled = setup.Defer is not null,
        };

        var adapter = new RabbitMqTransportAdapter(options, NullLogger<RabbitMqTransportAdapter>.Instance, headerMapper);

        await adapter.DeployTopologyAsync(AugmentTopology(setup), cancellationToken);

        return adapter;
    }

    /// <inheritdoc />
    protected override string? SkipReason(ParityDifference difference) => difference switch
    {
        ParityDifference.Durability => null,
        ParityDifference.FullDeadLetterQueue => null,
        ParityDifference.UnboundedRequeue => null,
        ParityDifference.BoundedRequeue => BoundedRequeueReason,
        ParityDifference.BwHeaderStrip => null,
        ParityDifference.RuntimeTopology => null,
        ParityDifference.HeadersExchangeTtlMaxLength => null,
        ParityDifference.Confidentiality => ConfidentialityReason,
        _ => throw new ArgumentOutOfRangeException(nameof(difference), difference, "Unknown parity difference."),
    };

    /// <summary>
    /// Expands a transport-neutral <see cref="ParitySetup"/> into the topology RabbitMQ actually needs:
    /// <see cref="ParitySetup.BoundedQueues"/> become an <c>x-max-length</c> / <c>x-overflow</c> pair on
    /// each named queue, and an opt-in <see cref="ParitySetup.Defer"/> becomes a dedicated fanout
    /// dead-letter exchange plus a TTL-bound delay queue that redelivers straight back onto the source
    /// queue once the delay elapses. Every other declaration passes through unchanged.
    /// </summary>
    private static TopologyDeclaration AugmentTopology(ParitySetup setup)
    {
        TopologyDeclaration topology = setup.Topology;
        List<QueueDeclaration> queues = [.. topology.Queues];
        List<ExchangeDeclaration> exchanges = [.. topology.Exchanges];
        List<ExchangeQueueBinding> exchangeQueueBindings = [.. topology.ExchangeQueueBindings];

        ApplyBoundedQueueOverflow(queues, setup.BoundedQueues);

        if (setup.Defer is { } defer)
        {
            ApplyDeferTopology(queues, exchanges, exchangeQueueBindings, defer);
        }

        return topology with
        {
            Queues = queues,
            Exchanges = exchanges,
            ExchangeQueueBindings = exchangeQueueBindings,
        };
    }

    /// <summary>
    /// Adds <c>x-max-length</c> (the bound) and <c>x-overflow = "reject-publish"</c> to every queue named
    /// in <paramref name="boundedQueues"/>, so a publish to a full queue is nacked at the publisher-confirm
    /// level (<c>SendResult.IsConfirmed = false</c>) instead of silently dropping the oldest message.
    /// Queues not named in <paramref name="boundedQueues"/> are left untouched.
    /// </summary>
    private static void ApplyBoundedQueueOverflow(List<QueueDeclaration> queues, IReadOnlyDictionary<string, int> boundedQueues)
    {
        if (boundedQueues.Count == 0)
        {
            return;
        }

        for (int i = 0; i < queues.Count; i++)
        {
            if (!boundedQueues.TryGetValue(queues[i].Name, out int capacity))
            {
                continue;
            }

            queues[i] = queues[i] with
            {
                Arguments = MergeArguments(
                    queues[i].Arguments,
                    new Dictionary<string, object>
                    {
                        ["x-max-length"] = capacity,
                        ["x-overflow"] = "reject-publish",
                    }),
            };
        }
    }

    /// <summary>
    /// Wires the opt-in deferred-redelivery chain onto <paramref name="defer"/>'s source queue: a
    /// dedicated fanout dead-letter exchange, a TTL-bound delay queue bound to it, and (unless the
    /// scenario already declared one) an <c>x-dead-letter-exchange</c> on the source queue pointing at
    /// that fanout exchange. A plain <c>Nack</c>/<c>requeue: false</c> (what <c>SettleAsync(Defer, ...)</c>
    /// sends once <see cref="RabbitMqTransportOptions.DeferEnabled"/> is on) then dead-letters into the
    /// delay queue; once its TTL elapses, the delay queue's own dead-letter arguments redeliver the
    /// message on the source queue via the default exchange.
    /// </summary>
    private static void ApplyDeferTopology(
        List<QueueDeclaration> queues,
        List<ExchangeDeclaration> exchanges,
        List<ExchangeQueueBinding> exchangeQueueBindings,
        ParityDefer defer)
    {
        string deferExchange = $"{defer.SourceQueue}.defer";
        string delayQueue = $"{defer.SourceQueue}.delay";
        int delayMs = (int)defer.Delay.TotalMilliseconds;

        exchanges.Add(new ExchangeDeclaration(deferExchange, ExchangeType.Fanout, Durable: false));
        queues.Add(new QueueDeclaration(
            delayQueue,
            Durable: false,
            Arguments: new Dictionary<string, object>
            {
                ["x-message-ttl"] = delayMs,
                ["x-dead-letter-exchange"] = string.Empty,
                ["x-dead-letter-routing-key"] = defer.SourceQueue,
            }));
        exchangeQueueBindings.Add(new ExchangeQueueBinding(deferExchange, delayQueue, string.Empty));

        for (int i = 0; i < queues.Count; i++)
        {
            if (queues[i].Name != defer.SourceQueue)
            {
                continue;
            }

            // Never clobber an x-dead-letter-exchange a scenario already set on its own source queue
            // (e.g. the P8/P9 dead-letter scenarios use a different source queue than defer does, but
            // this guard keeps the two concerns independent even if a future scenario combines them).
            queues[i] = queues[i] with
            {
                Arguments = MergeArguments(
                    queues[i].Arguments,
                    new Dictionary<string, object> { ["x-dead-letter-exchange"] = deferExchange }),
            };
        }
    }

    /// <summary>Merges <paramref name="additions"/> into <paramref name="existing"/> without overwriting any key already present.</summary>
    private static Dictionary<string, object> MergeArguments(
        IReadOnlyDictionary<string, object>? existing, IReadOnlyDictionary<string, object> additions)
    {
        Dictionary<string, object> merged = existing is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(existing);

        foreach (KeyValuePair<string, object> addition in additions)
        {
            merged.TryAdd(addition.Key, addition.Value);
        }

        return merged;
    }
}
