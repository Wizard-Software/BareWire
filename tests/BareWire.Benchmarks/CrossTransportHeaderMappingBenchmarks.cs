using Amazon.SQS.Model;
using Azure.Messaging.ServiceBus;
using BareWire.Transport.AzureServiceBus;
using BareWire.Transport.AWS.SQS;
using BareWire.Transport.Google.PubSub;
using BareWire.Transport.Kafka;
using BareWire.Transport.RabbitMQ;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Confluent.Kafka;

namespace BareWire.Benchmarks;

/// <summary>
/// Compares the BareWire adapter overhead on the deterministic surface
/// <c>*HeaderMapper.MapOutbound</c> across all five transports
/// (zero I/O — no broker connections).
///
/// <para>
/// <b>Primary metric:</b> the <c>Allocated</c> column (B/op) from
/// <see cref="MemoryDiagnoserAttribute"/>. The <c>Mean</c> column (ns/op)
/// serves as a proxy throughput indicator (R-2).
/// </para>
///
/// <para>
/// <b>Baseline:</b> <see cref="MapOutbound_RabbitMq"/> allocates a tuple plus two
/// collections (<c>BasicProperties</c> + <c>Dictionary</c>), so <c>Ratio &lt; 1.0</c>
/// for lighter transports reflects differences in the SDK object model,
/// not adapter inefficiency (R-1). The <c>Allocated</c> column is the primary metric
/// over <c>Ratio</c>.
/// </para>
///
/// <para>
/// <b>Azure Service Bus</b> is reported separately in the
/// <c>AsbMutateInPlace</c> category — see <see cref="MapOutbound_AzureServiceBus"/> (D-2).
/// </para>
///
/// <para>
/// This benchmark is a diagnostic tool — CI does not gate merges
/// on its results (same convention as
/// <see cref="CloudEventsBinaryBenchmarks"/>).
/// </para>
/// </summary>
[MemoryDiagnoser(displayGenColumns: true)]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class CrossTransportHeaderMappingBenchmarks
{
    /// <summary>Shared read-only dictionary of canonical BareWire headers
    /// used by all benchmark methods. Built once in <see cref="GlobalSetup"/>
    /// — does not count toward measured allocations.</summary>
    private IReadOnlyDictionary<string, string> _headers = null!;

    /// <summary>RabbitMQ mapper instance (the only non-static mapper class).</summary>
    private RabbitMqHeaderMapper _rabbitMapper = null!;

    /// <summary>Shared ASB message reused across benchmark iterations (D-2).</summary>
    private ServiceBusMessage _asbMessage = null!;

    /// <summary>
    /// Builds the shared set of canonical BareWire input headers
    /// and initialises transport-specific state.
    /// Called once before the entire measurement suite.
    ///
    /// <para>Header count is 9 — safe for the SQS 10-attribute limit
    /// (4 passthrough headers + 5 canonical; R-7).</para>
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        _headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["message-id"]    = "550e8400-e29b-41d4-a716-446655440000",
            ["correlation-id"] = "7c9e6679-7425-40de-944b-e07fc1f90ae7",
            ["content-type"]  = "application/json",
            ["BW-MessageType"] = "OrderCreated",
            ["traceparent"]   = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            ["x-tenant-id"]   = "acme-corp",
            ["x-environment"] = "production",
            ["x-region"]      = "eu-west-1",
            ["x-version"]     = "2",
        };

        _rabbitMapper = new RabbitMqHeaderMapper(config: null);
        _asbMessage   = new ServiceBusMessage();
    }

    /// <summary>
    /// Maps BareWire headers to RabbitMQ AMQP properties
    /// (<see cref="RabbitMQ.Client.BasicProperties"/> + AMQP headers dictionary).
    /// Serves as the baseline for the <c>Ratio</c> column — transports in the
    /// <c>CrossTransport</c> category (R-1).
    /// </summary>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("CrossTransport")]
    public (RabbitMQ.Client.BasicProperties Props, Dictionary<string, object?> Headers) MapOutbound_RabbitMq() =>
        _rabbitMapper.MapOutbound(_headers);

    /// <summary>
    /// Maps BareWire headers to Kafka <see cref="Confluent.Kafka.Headers"/>.
    /// All values are encoded as UTF-8.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("CrossTransport")]
    public Headers MapOutbound_Kafka() =>
        KafkaHeaderMapper.MapOutbound(_headers);

    /// <summary>
    /// Maps BareWire headers to an SQS
    /// <see cref="MessageAttributeValue"/> dictionary.
    /// Header count does not exceed 10 — no BareWireTransportException is thrown.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("CrossTransport")]
    public Dictionary<string, MessageAttributeValue> MapOutbound_Sqs() =>
        SqsHeaderMapper.MapOutbound(_headers);

    /// <summary>
    /// Maps BareWire headers to a Google Pub/Sub attribute dictionary
    /// (<c>PubsubMessage.Attributes</c>).
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("CrossTransport")]
    public Dictionary<string, string> MapOutbound_PubSub() =>
        PubSubHeaderMapper.MapOutbound(_headers);

    /// <summary>
    /// Maps BareWire headers into <see cref="ServiceBusMessage.ApplicationProperties"/>
    /// via in-place mutation of an existing message (<c>static void</c>).
    ///
    /// <para>
    /// <b>Steady-state mutate-in-place (D-2):</b> After the first iteration the keys already
    /// exist in the <c>ApplicationProperties</c> dictionary; subsequent iterations overwrite
    /// existing slots (string = reference type, no boxing, no resize) — allocation approaches
    /// zero in steady state. For this reason the benchmark is allocation-wise incomparable
    /// to transports that return a fresh object, and is reported <b>separately</b>,
    /// outside the <c>Ratio</c> column of the <c>CrossTransport</c> group (D-2/R-3).
    /// </para>
    ///
    /// <para>
    /// The <c>_asbMessage</c> instance is NOT reset inside the timed region — see D-2.
    /// </para>
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("AsbMutateInPlace")]
    public void MapOutbound_AzureServiceBus() =>
        AzureServiceBusHeaderMapper.MapOutbound(_headers, _asbMessage);
}
