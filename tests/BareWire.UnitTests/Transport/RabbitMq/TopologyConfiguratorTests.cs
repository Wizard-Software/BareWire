using System.Collections.ObjectModel;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Transport.RabbitMQ;

namespace BareWire.UnitTests.Transport.RabbitMq;

public sealed class TopologyConfiguratorTests
{
    private static RabbitMqTopologyConfigurator CreateConfigurator() => new();

    // ── DeclareExchange ────────────────────────────────────────────────────────

    [Fact]
    public void DeclareExchange_AddsToDeclaration()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.DeclareExchange("orders", ExchangeType.Topic);
        TopologyDeclaration declaration = sut.Build();

        // Assert
        declaration.Exchanges.Should().HaveCount(1);
        declaration.Exchanges[0].Name.Should().Be("orders");
        declaration.Exchanges[0].Type.Should().Be(ExchangeType.Topic);
        declaration.Exchanges[0].Durable.Should().BeTrue();
        declaration.Exchanges[0].AutoDelete.Should().BeFalse();
    }

    [Fact]
    public void DeclareExchange_MultipleExchanges_AccumulatesAll()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.DeclareExchange("ex-alpha", ExchangeType.Direct);
        sut.DeclareExchange("ex-beta", ExchangeType.Fanout, durable: false);
        TopologyDeclaration declaration = sut.Build();

        // Assert
        declaration.Exchanges.Should().HaveCount(2);
        declaration.Exchanges[0].Name.Should().Be("ex-alpha");
        declaration.Exchanges[1].Name.Should().Be("ex-beta");
        declaration.Exchanges[1].Durable.Should().BeFalse();
    }

    [Fact]
    public void DeclareExchange_NullName_ThrowsArgumentNullException()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        Action act = () => sut.DeclareExchange(null!, ExchangeType.Direct);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DeclareExchange_EmptyName_ThrowsArgumentException()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        Action act = () => sut.DeclareExchange(string.Empty, ExchangeType.Direct);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    // ── DeclareQueue ──────────────────────────────────────────────────────────

    [Fact]
    public void DeclareQueue_AddsToDeclaration()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.DeclareQueue("payments");
        TopologyDeclaration declaration = sut.Build();

        // Assert
        declaration.Queues.Should().HaveCount(1);
        declaration.Queues[0].Name.Should().Be("payments");
        declaration.Queues[0].Durable.Should().BeTrue();
        declaration.Queues[0].AutoDelete.Should().BeFalse();
        declaration.Queues[0].Exclusive.Should().BeFalse();
    }

    [Fact]
    public void DeclareQueue_EmptyName_ThrowsArgumentException()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        Action act = () => sut.DeclareQueue(string.Empty);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DeclareQueue_NullName_ThrowsArgumentNullException()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        Action act = () => sut.DeclareQueue(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DeclareQueue_WithArguments_IncludedInBuild()
    {
        // Arrange
        var sut = CreateConfigurator();
        Dictionary<string, object> arguments = new() { ["x-dead-letter-exchange"] = "dlx" };

        // Act
        sut.DeclareQueue("test-queue", arguments: arguments);
        TopologyDeclaration result = sut.Build();

        // Assert
        result.Queues[0].Arguments.Should().NotBeNull();
        result.Queues[0].Arguments!["x-dead-letter-exchange"].Should().Be("dlx");
    }

    [Fact]
    public void DeclareQueue_WithoutArguments_ArgumentsIsNull()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.DeclareQueue("test-queue");
        TopologyDeclaration result = sut.Build();

        // Assert
        result.Queues[0].Arguments.Should().BeNull();
    }

    // ── BindExchangeToQueue ───────────────────────────────────────────────────

    [Fact]
    public void BindExchangeToQueue_AddsBinding()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.BindExchangeToQueue("orders-ex", "orders-q", "order.created");
        TopologyDeclaration declaration = sut.Build();

        // Assert
        declaration.ExchangeQueueBindings.Should().HaveCount(1);
        declaration.ExchangeQueueBindings[0].ExchangeName.Should().Be("orders-ex");
        declaration.ExchangeQueueBindings[0].QueueName.Should().Be("orders-q");
        declaration.ExchangeQueueBindings[0].RoutingKey.Should().Be("order.created");
    }

    // ── BindExchangeToExchange ────────────────────────────────────────────────

    [Fact]
    public void BindExchangeToExchange_AddsBinding()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.BindExchangeToExchange("source-ex", "dest-ex", "routing.#");
        TopologyDeclaration declaration = sut.Build();

        // Assert
        declaration.ExchangeExchangeBindings.Should().HaveCount(1);
        declaration.ExchangeExchangeBindings[0].SourceExchangeName.Should().Be("source-ex");
        declaration.ExchangeExchangeBindings[0].DestinationExchangeName.Should().Be("dest-ex");
        declaration.ExchangeExchangeBindings[0].RoutingKey.Should().Be("routing.#");
    }

    // ── Build ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_ReturnsImmutableDeclaration()
    {
        // Arrange
        var sut = CreateConfigurator();
        sut.DeclareExchange("ex", ExchangeType.Direct);
        sut.DeclareQueue("q");
        sut.BindExchangeToQueue("ex", "q", "key");

        // Act
        TopologyDeclaration declaration = sut.Build();

        // Assert
        declaration.Should().NotBeNull();
        declaration.Exchanges.Should().HaveCount(1);
        declaration.Queues.Should().HaveCount(1);
        declaration.ExchangeQueueBindings.Should().HaveCount(1);
        declaration.ExchangeExchangeBindings.Should().BeEmpty();
    }

    [Fact]
    public void Build_CalledTwice_ReturnsIndependentSnapshots()
    {
        // Arrange
        var sut = CreateConfigurator();
        sut.DeclareExchange("ex-one", ExchangeType.Direct);

        // Act
        TopologyDeclaration first = sut.Build();

        // Add another exchange after the first Build — second snapshot must reflect it
        // but first snapshot must remain unchanged (arrays are independent copies).
        sut.DeclareExchange("ex-two", ExchangeType.Fanout);
        TopologyDeclaration second = sut.Build();

        // Assert
        first.Exchanges.Should().HaveCount(1);
        second.Exchanges.Should().HaveCount(2);
        first.Should().NotBeSameAs(second);
    }

    [Fact]
    public void Build_EmptyConfigurator_ReturnsEmptyDeclaration()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        TopologyDeclaration declaration = sut.Build();

        // Assert
        declaration.Exchanges.Should().BeEmpty();
        declaration.Queues.Should().BeEmpty();
        declaration.ExchangeQueueBindings.Should().BeEmpty();
        declaration.ExchangeExchangeBindings.Should().BeEmpty();
    }

    // ── DeclareQueue (fluent overload) ───────────────────────────────────────

    [Fact]
    public void DeclareQueue_WithFluentConfigure_ProducesCorrectArguments()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.DeclareQueue("order-queue", durable: true, autoDelete: false, configure: q =>
        {
            q.DeadLetterExchange("orders.dlx")
             .MessageTtl(TimeSpan.FromHours(24))
             .SetQueueType(QueueType.Quorum);
        });
        TopologyDeclaration result = sut.Build();

        // Assert
        result.Queues.Should().HaveCount(1);
        result.Queues[0].Name.Should().Be("order-queue");
        result.Queues[0].Durable.Should().BeTrue();
        result.Queues[0].Arguments.Should().NotBeNull();
        result.Queues[0].Arguments!["x-dead-letter-exchange"].Should().Be("orders.dlx");
        result.Queues[0].Arguments!["x-message-ttl"].Should().Be(86400000L);
        result.Queues[0].Arguments!["x-queue-type"].Should().Be("quorum");
    }

    [Fact]
    public void DeclareQueue_WithFluentConfigure_NullAction_ThrowsArgumentNullException()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        Action act = () => sut.DeclareQueue("test-queue", durable: true, autoDelete: false,
            configure: null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DeclareQueue_FluentAndDictionary_ProduceIdenticalDeclarations()
    {
        // Arrange
        var fluentConfigurator = CreateConfigurator();
        var dictConfigurator = CreateConfigurator();
        var arguments = new ReadOnlyDictionary<string, object>(
            new Dictionary<string, object>
            {
                ["x-dead-letter-exchange"] = "dlx",
                ["x-message-ttl"] = 60000L,
            });

        // Act
        fluentConfigurator.DeclareQueue("test-q", durable: true, autoDelete: false, configure: q =>
        {
            q.DeadLetterExchange("dlx")
             .MessageTtl(TimeSpan.FromMinutes(1));
        });

        dictConfigurator.DeclareQueue("test-q", durable: true, arguments: arguments);

        TopologyDeclaration fluentResult = fluentConfigurator.Build();
        TopologyDeclaration dictResult = dictConfigurator.Build();

        // Assert
        fluentResult.Queues[0].Name.Should().Be(dictResult.Queues[0].Name);
        fluentResult.Queues[0].Durable.Should().Be(dictResult.Queues[0].Durable);
        fluentResult.Queues[0].Arguments!["x-dead-letter-exchange"]
            .Should().Be(dictResult.Queues[0].Arguments!["x-dead-letter-exchange"]);
        fluentResult.Queues[0].Arguments!["x-message-ttl"]
            .Should().Be(dictResult.Queues[0].Arguments!["x-message-ttl"]);
    }
}
