using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using ExchangeType = BareWire.Abstractions.ExchangeType;

namespace BareWire.IntegrationTests.Transport;

/// <summary>
/// Integration tests for the consistent-hash exchange type (R8.2): the
/// <see cref="ExchangeType.ConsistentHash"/> member, its <c>x-consistent-hash</c> AMQP mapping in
/// <see cref="RabbitMqTransportAdapter"/>, and the deploy-time error contract when the broker lacks
/// (or rejects) the exchange type. All tests use a real RabbitMQ instance provisioned via
/// <see cref="AspireFixture"/> with the <c>rabbitmq_consistent_hash_exchange</c> plugin enabled in
/// <c>BareWire.AppHost</c>.
/// </summary>
public sealed class RabbitMqConsistentHashTopologyTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    // A fixed, small message count keeps the affinity test deterministic and fast under a slow
    // Docker broker (the 30s CTS is the hard ceiling, not the expected duration).
    private const int AffinityMessageCount = 30;

    private RabbitMqTransportAdapter CreateAdapter() =>
        new(
            new RabbitMqTransportOptions
            {
                ConnectionString = fixture.GetRabbitMqConnectionString(),
            },
            NullLogger<RabbitMqTransportAdapter>.Instance);

    private async Task<IConnection> CreateRawConnectionAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(fixture.GetRabbitMqConnectionString()),
            AutomaticRecoveryEnabled = false,
        };

        return await factory.CreateConnectionAsync(ct);
    }

    // ── Test (1): deploy consistent-hash exchange + per-key queues ─────────────

    [Fact]
    public async Task DeployTopologyAsync_ConsistentHashExchangeWithPerKeyQueues_CreatesSuccessfully()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");
        string exchangeName = $"ch-ex-{suffix}";
        string queue1 = $"ch-q1-{suffix}";
        string queue2 = $"ch-q2-{suffix}";

        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(exchangeName, ExchangeType.ConsistentHash, durable: false, autoDelete: false);
        configurator.DeclareQueue(queue1, durable: false, autoDelete: false);
        configurator.DeclareQueue(queue2, durable: false, autoDelete: false);
        // For a consistent-hash exchange the binding routing key is the queue weight (an integer).
        configurator.BindExchangeToQueue(exchangeName, queue1, routingKey: "1");
        configurator.BindExchangeToQueue(exchangeName, queue2, routingKey: "1");
        TopologyDeclaration topology = configurator.Build();

        // Act — declaring an x-consistent-hash exchange succeeds only when the plugin is enabled.
        Func<Task> act = async () => await adapter.DeployTopologyAsync(topology, cts.Token);

        // Assert
        await act.Should().NotThrowAsync();
    }

    // ── Test (2): same routing key always routes to the same bound queue ───────

    [Fact]
    public async Task ConsistentHashRouting_SameRoutingKey_AlwaysRoutesToSameQueue()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");
        string exchangeName = $"ch-aff-ex-{suffix}";
        string queue1 = $"ch-aff-q1-{suffix}";
        string queue2 = $"ch-aff-q2-{suffix}";
        string queue3 = $"ch-aff-q3-{suffix}";
        const string routingKey = "order-key-42";

        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(exchangeName, ExchangeType.ConsistentHash, durable: false, autoDelete: false);
        configurator.DeclareQueue(queue1, durable: false, autoDelete: false);
        configurator.DeclareQueue(queue2, durable: false, autoDelete: false);
        configurator.DeclareQueue(queue3, durable: false, autoDelete: false);
        configurator.BindExchangeToQueue(exchangeName, queue1, routingKey: "1");
        configurator.BindExchangeToQueue(exchangeName, queue2, routingKey: "1");
        configurator.BindExchangeToQueue(exchangeName, queue3, routingKey: "1");
        TopologyDeclaration topology = configurator.Build();

        await adapter.DeployTopologyAsync(topology, cts.Token);

        // Publish AffinityMessageCount messages with the SAME routing key.
        await using IConnection connection = await CreateRawConnectionAsync(cts.Token);
        await using IChannel channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true),
            cts.Token);

        for (int i = 0; i < AffinityMessageCount; i++)
        {
            await channel.BasicPublishAsync(
                exchange: exchangeName,
                routingKey: routingKey,
                mandatory: false,
                basicProperties: new BasicProperties(),
                body: new ReadOnlyMemory<byte>([(byte)i]),
                cancellationToken: cts.Token);
        }

        // Give the broker a moment to route all confirmed publishes before draining.
        await Task.Delay(500, cts.Token);

        // Act — drain each queue with a bounded BasicGet loop and count where the messages landed.
        int count1 = await DrainQueueAsync(channel, queue1, cts.Token);
        int count2 = await DrainQueueAsync(channel, queue2, cts.Token);
        int count3 = await DrainQueueAsync(channel, queue3, cts.Token);

        // Assert — all messages sharing one routing key land in exactly one queue (key affinity);
        // no spreading across queues.
        int[] counts = [count1, count2, count3];
        counts.Sum().Should().Be(AffinityMessageCount, "every published message must be routed somewhere");
        counts.Count(c => c > 0).Should().Be(1, "the same routing key must map to exactly one bound queue");
        counts.Max().Should().Be(AffinityMessageCount, "all messages for one key must land in the same queue");
    }

    // ── Test (3): broker-rejected exchange declaration → BareWireTransportException ──

    [Fact]
    public async Task DeployTopologyAsync_ConsistentHashExchangeRejectedByBroker_ThrowsTransportException()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");
        string exchangeName = $"ch-conflict-ex-{suffix}";

        // First declare the exchange as a plain 'direct' type.
        var seed = new RabbitMqTopologyConfigurator();
        seed.DeclareExchange(exchangeName, ExchangeType.Direct, durable: false, autoDelete: false);
        await adapter.DeployTopologyAsync(seed.Build(), cts.Token);

        // Then re-declare the SAME name as a consistent-hash exchange. RabbitMQ rejects a type change on
        // an existing exchange (PRECONDITION_FAILED), surfacing as OperationInterruptedException, which
        // DeployTopologyAsync wraps in TopologyDeploymentException (a BareWireTransportException). This is
        // the IDENTICAL wrap path a missing rabbitmq_consistent_hash_exchange plugin produces (the broker
        // rejects the declaration), proving the deploy-time error contract for the new exchange type
        // without requiring a second plugin-less broker.
        var conflicting = new RabbitMqTopologyConfigurator();
        conflicting.DeclareExchange(exchangeName, ExchangeType.ConsistentHash, durable: false, autoDelete: false);
        TopologyDeclaration topology = conflicting.Build();

        // Act
        Func<Task> act = async () => await adapter.DeployTopologyAsync(topology, cts.Token);

        // Assert — deploy failure surfaces as a BareWireTransportException (TopologyDeploymentException),
        // carrying the offending element name and a broker error detail.
        var assertion = await act.Should().ThrowAsync<BareWireTransportException>();

        assertion.Which.Should().BeOfType<TopologyDeploymentException>();
        TopologyDeploymentException ex = (TopologyDeploymentException)assertion.Which;
        ex.TopologyElement.Should().Be(exchangeName);
        ex.BrokerError.Should().NotBeNullOrEmpty("the broker rejection reason must be surfaced for diagnosis");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static async Task<int> DrainQueueAsync(IChannel channel, string queue, CancellationToken ct)
    {
        int count = 0;

        // Bounded loop: stop at the first empty BasicGet (null) or AffinityMessageCount drained.
        while (count <= AffinityMessageCount)
        {
            BasicGetResult? result = await channel.BasicGetAsync(queue, autoAck: true, cancellationToken: ct);
            if (result is null)
            {
                break;
            }

            count++;
        }

        return count;
    }
}
