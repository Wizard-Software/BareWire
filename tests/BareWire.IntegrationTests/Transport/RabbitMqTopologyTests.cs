using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.IntegrationTests.Transport;

/// <summary>
/// Integration tests for topology deployment via <see cref="RabbitMqTransportAdapter.DeployTopologyAsync"/>
/// and declaration accumulation in <see cref="RabbitMqTopologyConfigurator"/>.
/// All tests use a real RabbitMQ instance provisioned via <see cref="AspireFixture"/>.
/// </summary>
public sealed class RabbitMqTopologyTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    // ── Factory ───────────────────────────────────────────────────────────────

    private RabbitMqTransportAdapter CreateAdapter() =>
        new(
            new RabbitMqTransportOptions
            {
                ConnectionString = fixture.GetRabbitMqConnectionString(),
            },
            NullLogger<RabbitMqTransportAdapter>.Instance);

    // ── TopologyConfigurator — unit-level contract (no broker) ─────────────────

    [Fact]
    public void TopologyConfigurator_BuildsCorrectDeclaration()
    {
        // Arrange
        var configurator = new RabbitMqTopologyConfigurator();

        string exchange = "test-exchange";
        string queue = "test-queue";
        string routingKey = "test.key";

        // Act
        configurator.DeclareExchange(exchange, ExchangeType.Direct);
        configurator.DeclareQueue(queue);
        configurator.BindExchangeToQueue(exchange, queue, routingKey);
        TopologyDeclaration declaration = configurator.Build();

        // Assert — all declarations are accumulated correctly
        declaration.Exchanges.Should().HaveCount(1);
        declaration.Exchanges[0].Name.Should().Be(exchange);
        declaration.Exchanges[0].Type.Should().Be(ExchangeType.Direct);

        declaration.Queues.Should().HaveCount(1);
        declaration.Queues[0].Name.Should().Be(queue);

        declaration.ExchangeQueueBindings.Should().HaveCount(1);
        declaration.ExchangeQueueBindings[0].ExchangeName.Should().Be(exchange);
        declaration.ExchangeQueueBindings[0].QueueName.Should().Be(queue);
        declaration.ExchangeQueueBindings[0].RoutingKey.Should().Be(routingKey);

        declaration.ExchangeExchangeBindings.Should().BeEmpty();
    }

    // ── DeployTopologyAsync — exchange + queue ─────────────────────────────────

    [Fact]
    public async Task DeployTopologyAsync_ExchangeAndQueue_CreatesSuccessfully()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");
        string exchangeName = $"test-ex-{suffix}";
        string queueName = $"test-q-{suffix}";

        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(exchangeName, ExchangeType.Direct, durable: false, autoDelete: true);
        configurator.DeclareQueue(queueName, durable: false, autoDelete: true);
        TopologyDeclaration topology = configurator.Build();

        // Act — should not throw when broker accepts both declarations
        Func<Task> act = async () => await adapter.DeployTopologyAsync(topology, cts.Token);

        // Assert
        await act.Should().NotThrowAsync();
    }

    // ── DeployTopologyAsync — exchange-to-queue binding ────────────────────────

    [Fact]
    public async Task DeployTopologyAsync_WithBinding_BindsSuccessfully()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");
        string exchangeName = $"test-bind-ex-{suffix}";
        string queueName = $"test-bind-q-{suffix}";
        string routingKey = "order.created";

        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(exchangeName, ExchangeType.Direct, durable: false, autoDelete: true);
        configurator.DeclareQueue(queueName, durable: false, autoDelete: false);
        configurator.BindExchangeToQueue(exchangeName, queueName, routingKey);
        TopologyDeclaration topology = configurator.Build();

        // Act — binding creation should succeed
        Func<Task> act = async () => await adapter.DeployTopologyAsync(topology, cts.Token);

        // Assert
        await act.Should().NotThrowAsync();
    }

    // ── DeployTopologyAsync — idempotency ──────────────────────────────────────

    [Fact]
    public async Task DeployTopologyAsync_CalledTwice_IsIdempotent()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");
        string exchangeName = $"test-idem-ex-{suffix}";
        string queueName = $"test-idem-q-{suffix}";

        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(exchangeName, ExchangeType.Fanout, durable: false, autoDelete: false);
        configurator.DeclareQueue(queueName, durable: false, autoDelete: false);
        configurator.BindExchangeToQueue(exchangeName, queueName, routingKey: string.Empty);
        TopologyDeclaration topology = configurator.Build();

        // Act — second call with identical parameters must be a no-op (idempotent)
        await adapter.DeployTopologyAsync(topology, cts.Token);
        Func<Task> secondDeploy = async () => await adapter.DeployTopologyAsync(topology, cts.Token);

        // Assert — no exception on the second call
        await secondDeploy.Should().NotThrowAsync();
    }

    // ── DeployTopologyAsync — exchange-to-exchange binding ────────────────────

    [Fact]
    public async Task DeployTopologyAsync_WithExchangeToExchange_BindsSuccessfully()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");
        string sourceExchange = $"test-src-ex-{suffix}";
        string destExchange = $"test-dst-ex-{suffix}";
        string queueName = $"test-e2e-q-{suffix}";

        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(sourceExchange, ExchangeType.Topic, durable: false, autoDelete: false);
        configurator.DeclareExchange(destExchange, ExchangeType.Direct, durable: false, autoDelete: false);
        configurator.DeclareQueue(queueName, durable: false, autoDelete: false);
        configurator.BindExchangeToExchange(sourceExchange, destExchange, routingKey: "#");
        configurator.BindExchangeToQueue(destExchange, queueName, routingKey: string.Empty);
        TopologyDeclaration topology = configurator.Build();

        // Act
        Func<Task> act = async () => await adapter.DeployTopologyAsync(topology, cts.Token);

        // Assert — exchange-to-exchange binding must succeed
        await act.Should().NotThrowAsync();
    }
}
