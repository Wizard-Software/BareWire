using System.Globalization;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using ExchangeType = BareWire.Abstractions.ExchangeType;

namespace BareWire.IntegrationTests.Transport;

/// <summary>
/// Integration tests that verify the mapping-epoch stamping behaviour of
/// <see cref="RabbitMqTransportAdapter.SendBatchAsync"/>.
///
/// <para>
/// The <c>BW-MappingEpoch</c> AMQP header carries a topology-derived, deterministic long value
/// computed from the sorted set of queue names bound to a consistent-hash exchange. Tests cover
/// the stamping happy path, the no-stamp paths (Direct exchange, null topology), and the re-map
/// scenario where changing the bound queue set bumps the epoch.
/// </para>
///
/// <para>All tests require a running RabbitMQ instance with the
/// <c>rabbitmq_consistent_hash_exchange</c> plugin enabled, provisioned via
/// <see cref="AspireFixture"/>.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class RabbitMqMappingEpochStampingTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    private const string MappingEpochHeader = "BW-MappingEpoch";

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a transport adapter with the given topology explicitly set on the options.
    /// IMPORTANT: options.Topology MUST be set for the epoch stamp to fire — DeployTopologyAsync
    /// does not write _options.Topology (it reads the parameter only).
    /// </summary>
    private RabbitMqTransportAdapter CreateAdapter(TopologyDeclaration? topology = null)
    {
        var options = new RabbitMqTransportOptions
        {
            ConnectionString = fixture.GetRabbitMqConnectionString(),
            Topology = topology,
        };

        return new RabbitMqTransportAdapter(options, NullLogger<RabbitMqTransportAdapter>.Instance);
    }

    private async Task<IConnection> CreateRawConnectionAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(fixture.GetRabbitMqConnectionString()),
            AutomaticRecoveryEnabled = false,
        };

        return await factory.CreateConnectionAsync(ct);
    }

    /// <summary>
    /// Drains a queue with BasicGet until empty or <paramref name="maxMessages"/> reached.
    /// Returns all result objects so callers can inspect headers.
    /// </summary>
    private static async Task<IReadOnlyList<BasicGetResult>> DrainQueueResultsAsync(
        IChannel channel,
        string queue,
        int maxMessages,
        CancellationToken ct)
    {
        var results = new List<BasicGetResult>();

        while (results.Count < maxMessages)
        {
            BasicGetResult? result = await channel.BasicGetAsync(queue, autoAck: true, cancellationToken: ct);

            if (result is null)
            {
                break;
            }

            results.Add(result);
        }

        return results;
    }

    // ── Test (1): consistent-hash exchange → header IS stamped ────────────────

    [Fact]
    public async Task Publish_ViaConsistentHashExchange_StampsMappingEpochHeader()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        string suffix = Guid.NewGuid().ToString("N");
        string exchangeName = $"ep-ch-ex-{suffix}";
        string queue1 = $"ep-ch-q1-{suffix}";
        string queue2 = $"ep-ch-q2-{suffix}";

        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(exchangeName, ExchangeType.ConsistentHash, durable: false, autoDelete: false);
        configurator.DeclareQueue(queue1, durable: false, autoDelete: false);
        configurator.DeclareQueue(queue2, durable: false, autoDelete: false);
        configurator.BindExchangeToQueue(exchangeName, queue1, routingKey: "1");
        configurator.BindExchangeToQueue(exchangeName, queue2, routingKey: "1");
        TopologyDeclaration topology = configurator.Build();

        // Adapter is constructed with options.Topology set — the epoch calculator will fire.
        await using RabbitMqTransportAdapter adapter = CreateAdapter(topology);
        await adapter.DeployTopologyAsync(topology, cts.Token);

        // Compute the expected epoch using the same calculator so we can assert the exact value.
        long? expectedEpoch = MappingEpochCalculator.Compute(topology);
        expectedEpoch.Should().NotBeNull("topology has a consistent-hash exchange with bound queues");

        // Act — publish one message routed to the consistent-hash exchange.
        var outbound = new OutboundMessage(
            routingKey: "some-key",
            headers: new Dictionary<string, string>
            {
                ["BW-Exchange"] = exchangeName,
            },
            body: ReadOnlyMemory<byte>.Empty,
            contentType: "application/octet-stream");

        await adapter.SendBatchAsync([outbound], cts.Token);

        // Give the broker a moment to route before draining.
        await Task.Delay(500, cts.Token);

        // Assert — drain both queues; exactly one will contain the message.
        await using IConnection connection = await CreateRawConnectionAsync(cts.Token);
        await using IChannel channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: false, publisherConfirmationTrackingEnabled: false),
            cts.Token);

        var results1 = await DrainQueueResultsAsync(channel, queue1, 10, cts.Token);
        var results2 = await DrainQueueResultsAsync(channel, queue2, 10, cts.Token);

        IReadOnlyList<BasicGetResult> allResults = [.. results1, .. results2];
        allResults.Should().HaveCount(1, "exactly one message was published");

        BasicGetResult delivered = allResults[0];
        delivered.BasicProperties.Headers.Should().NotBeNull("the message must have AMQP headers");

        IDictionary<string, object?> headers = delivered.BasicProperties.Headers!;
        headers.Should().ContainKey(MappingEpochHeader, "BW-MappingEpoch must be stamped for consistent-hash routing");

        // The header value is a boxed long; assert it is convertible and matches the expected epoch.
        object? rawValue = headers[MappingEpochHeader];
        rawValue.Should().NotBeNull();
        long actualEpoch = Convert.ToInt64(rawValue, CultureInfo.InvariantCulture);
        actualEpoch.Should().Be(expectedEpoch!.Value);
    }

    // ── Test (2): Direct exchange → header is NOT stamped ─────────────────────

    [Fact]
    public async Task Publish_ViaDirectExchange_DoesNotStampMappingEpoch()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        string suffix = Guid.NewGuid().ToString("N");
        string exchangeName = $"ep-direct-ex-{suffix}";
        string queueName = $"ep-direct-q-{suffix}";

        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(exchangeName, ExchangeType.Direct, durable: false, autoDelete: false);
        configurator.DeclareQueue(queueName, durable: false, autoDelete: false);
        configurator.BindExchangeToQueue(exchangeName, queueName, routingKey: queueName);
        TopologyDeclaration topology = configurator.Build();

        // Direct-only topology → MappingEpochCalculator.Compute returns null → no stamp.
        await using RabbitMqTransportAdapter adapter = CreateAdapter(topology);
        await adapter.DeployTopologyAsync(topology, cts.Token);

        // Act
        var outbound = new OutboundMessage(
            routingKey: queueName,
            headers: new Dictionary<string, string>
            {
                ["BW-Exchange"] = exchangeName,
            },
            body: ReadOnlyMemory<byte>.Empty,
            contentType: "application/octet-stream");

        await adapter.SendBatchAsync([outbound], cts.Token);
        await Task.Delay(500, cts.Token);

        // Assert — drain queue, verify NO BW-MappingEpoch header.
        await using IConnection connection = await CreateRawConnectionAsync(cts.Token);
        await using IChannel channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: false, publisherConfirmationTrackingEnabled: false),
            cts.Token);

        var results = await DrainQueueResultsAsync(channel, queueName, 10, cts.Token);
        results.Should().HaveCount(1, "exactly one message was published");

        BasicGetResult delivered = results[0];

        // No BW-MappingEpoch header: either headers dict is null or key is absent.
        bool hasEpochHeader = delivered.BasicProperties.Headers is not null
            && delivered.BasicProperties.Headers.ContainsKey(MappingEpochHeader);

        hasEpochHeader.Should().BeFalse("Direct exchange topology has no consistent-hash exchange → no epoch stamp");
    }

    // ── Test (3): null topology → header is NOT stamped ───────────────────────

    [Fact]
    public async Task Publish_WithNoTopologyConfigured_DoesNotStampMappingEpoch()
    {
        // Arrange — deploy a consistent-hash exchange to the broker for delivery, but construct
        // the adapter with options.Topology = null. The epoch source of truth is _options.Topology,
        // not the broker state, so the stamp must be absent even though the exchange exists.
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        string suffix = Guid.NewGuid().ToString("N");
        string exchangeName = $"ep-notopo-ex-{suffix}";
        string queueName = $"ep-notopo-q-{suffix}";

        // Deploy to broker via a temporary adapter (topology set, so deploy works).
        var deployConfigurator = new RabbitMqTopologyConfigurator();
        deployConfigurator.DeclareExchange(exchangeName, ExchangeType.ConsistentHash, durable: false, autoDelete: false);
        deployConfigurator.DeclareQueue(queueName, durable: false, autoDelete: false);
        deployConfigurator.BindExchangeToQueue(exchangeName, queueName, routingKey: "1");
        TopologyDeclaration deployTopology = deployConfigurator.Build();

        await using RabbitMqTransportAdapter deployAdapter = CreateAdapter(deployTopology);
        await deployAdapter.DeployTopologyAsync(deployTopology, cts.Token);

        // Publishing adapter: topology = null → MappingEpochCalculator.Compute(null) = null → no stamp.
        await using RabbitMqTransportAdapter publishAdapter = CreateAdapter(topology: null);

        // Act
        var outbound = new OutboundMessage(
            routingKey: "any-key",
            headers: new Dictionary<string, string>
            {
                ["BW-Exchange"] = exchangeName,
            },
            body: ReadOnlyMemory<byte>.Empty,
            contentType: "application/octet-stream");

        await publishAdapter.SendBatchAsync([outbound], cts.Token);
        await Task.Delay(500, cts.Token);

        // Assert — drain, verify NO BW-MappingEpoch header.
        await using IConnection connection = await CreateRawConnectionAsync(cts.Token);
        await using IChannel channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: false, publisherConfirmationTrackingEnabled: false),
            cts.Token);

        var results = await DrainQueueResultsAsync(channel, queueName, 10, cts.Token);
        results.Should().HaveCount(1, "exactly one message was published");

        BasicGetResult delivered = results[0];

        bool hasEpochHeader = delivered.BasicProperties.Headers is not null
            && delivered.BasicProperties.Headers.ContainsKey(MappingEpochHeader);

        hasEpochHeader.Should().BeFalse(
            "the source of truth is _options.Topology; null topology must produce no epoch stamp " +
            "even when a consistent-hash exchange exists on the broker");
    }

    // ── Test (4): re-map — adding a bound queue bumps the epoch ───────────────

    [Fact]
    public async Task ReMap_AddingBoundQueue_BumpsMappingEpoch()
    {
        // Arrange — T1: consistent-hash exchange with 2 bound queues.
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        string suffix = Guid.NewGuid().ToString("N");
        string exchangeName = $"ep-remap-ex-{suffix}";
        string queue1 = $"ep-remap-q1-{suffix}";
        string queue2 = $"ep-remap-q2-{suffix}";
        string queue3 = $"ep-remap-q3-{suffix}";  // added for T2

        // Build T1 (2 queues).
        var configurator1 = new RabbitMqTopologyConfigurator();
        configurator1.DeclareExchange(exchangeName, ExchangeType.ConsistentHash, durable: false, autoDelete: false);
        configurator1.DeclareQueue(queue1, durable: false, autoDelete: false);
        configurator1.DeclareQueue(queue2, durable: false, autoDelete: false);
        configurator1.BindExchangeToQueue(exchangeName, queue1, routingKey: "1");
        configurator1.BindExchangeToQueue(exchangeName, queue2, routingKey: "1");
        TopologyDeclaration topology1 = configurator1.Build();

        // Adapter constructed with T1 — publish first message and read epoch E1.
        await using RabbitMqTransportAdapter adapter = CreateAdapter(topology1);
        await adapter.DeployTopologyAsync(topology1, cts.Token);

        var outbound1 = new OutboundMessage(
            routingKey: "key-1",
            headers: new Dictionary<string, string>
            {
                ["BW-Exchange"] = exchangeName,
            },
            body: ReadOnlyMemory<byte>.Empty,
            contentType: "application/octet-stream");

        await adapter.SendBatchAsync([outbound1], cts.Token);
        await Task.Delay(500, cts.Token);

        await using IConnection connection = await CreateRawConnectionAsync(cts.Token);
        await using IChannel channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: false, publisherConfirmationTrackingEnabled: false),
            cts.Token);

        // Drain all queues from T1 to find E1.
        var t1Results = new List<BasicGetResult>();
        t1Results.AddRange(await DrainQueueResultsAsync(channel, queue1, 5, cts.Token));
        t1Results.AddRange(await DrainQueueResultsAsync(channel, queue2, 5, cts.Token));
        t1Results.Should().HaveCount(1, "one message published via T1");

        long epochE1 = Convert.ToInt64(t1Results[0].BasicProperties.Headers![MappingEpochHeader], CultureInfo.InvariantCulture);

        // Build T2 (3 queues — adds queue3 to broker and to topology).
        var configurator2 = new RabbitMqTopologyConfigurator();
        configurator2.DeclareExchange(exchangeName, ExchangeType.ConsistentHash, durable: false, autoDelete: false);
        configurator2.DeclareQueue(queue1, durable: false, autoDelete: false);
        configurator2.DeclareQueue(queue2, durable: false, autoDelete: false);
        configurator2.DeclareQueue(queue3, durable: false, autoDelete: false);
        configurator2.BindExchangeToQueue(exchangeName, queue1, routingKey: "1");
        configurator2.BindExchangeToQueue(exchangeName, queue2, routingKey: "1");
        configurator2.BindExchangeToQueue(exchangeName, queue3, routingKey: "1");
        TopologyDeclaration topology2 = configurator2.Build();

        // Deploy T2 to the broker (adds queue3 + binding physically).
        await adapter.DeployTopologyAsync(topology2, cts.Token);

        // Re-assign options.Topology on the SAME adapter to T2 — this is what triggers epoch recomputation.
        // (RabbitMqTransportOptions.Topology has a public setter; the memoization key is the reference.)
        var adapterOptions = new RabbitMqTransportOptions
        {
            ConnectionString = fixture.GetRabbitMqConnectionString(),
            Topology = topology2,
        };

        // Use a fresh adapter instance constructed with T2 so its _options.Topology = topology2.
        // The memoization guard (ReferenceEquals) will detect the new reference on the first SendBatchAsync call.
        await using RabbitMqTransportAdapter adapter2 = new(adapterOptions, NullLogger<RabbitMqTransportAdapter>.Instance);

        var outbound2 = new OutboundMessage(
            routingKey: "key-2",
            headers: new Dictionary<string, string>
            {
                ["BW-Exchange"] = exchangeName,
            },
            body: ReadOnlyMemory<byte>.Empty,
            contentType: "application/octet-stream");

        await adapter2.SendBatchAsync([outbound2], cts.Token);
        await Task.Delay(500, cts.Token);

        // Drain all 3 queues to find the T2 message.
        var t2Results = new List<BasicGetResult>();
        t2Results.AddRange(await DrainQueueResultsAsync(channel, queue1, 5, cts.Token));
        t2Results.AddRange(await DrainQueueResultsAsync(channel, queue2, 5, cts.Token));
        t2Results.AddRange(await DrainQueueResultsAsync(channel, queue3, 5, cts.Token));
        t2Results.Should().HaveCount(1, "one message published via T2");

        long epochE2 = Convert.ToInt64(t2Results[0].BasicProperties.Headers![MappingEpochHeader], CultureInfo.InvariantCulture);

        // Assert — E2 must differ from E1 because the bound queue set changed (re-map signal).
        epochE2.Should().NotBe(epochE1, "adding a bound queue must bump the mapping epoch");
    }
}
