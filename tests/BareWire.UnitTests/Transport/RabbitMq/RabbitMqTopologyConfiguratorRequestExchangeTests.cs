using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Internal;

namespace BareWire.UnitTests.Transport.RabbitMq;

public sealed class RabbitMqTopologyConfiguratorRequestExchangeTests
{
    private sealed record PaymentRequested(decimal Amount);
    private sealed record OrderSubmitted(Guid OrderId);

    private static RabbitMqTopologyConfigurator CreateConfigurator() => new();

    // ── DeclareRequestExchange<T> ──────────────────────────────────────────────

    [Fact]
    public void DeclareRequestExchange_AddsExchangeWithCorrectName()
    {
        // Arrange
        var sut = CreateConfigurator();
        string expectedName = RequestExchangeNameFormatter.Format<PaymentRequested>();

        // Act
        sut.DeclareRequestExchange<PaymentRequested>();
        TopologyDeclaration result = sut.Build();

        // Assert
        result.Exchanges.Should().ContainSingle(e => e.Name == expectedName);
    }

    [Fact]
    public void DeclareRequestExchange_ExchangeIsFanout()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.DeclareRequestExchange<PaymentRequested>();
        TopologyDeclaration result = sut.Build();

        // Assert
        result.Exchanges[0].Type.Should().Be(ExchangeType.Fanout);
    }

    [Fact]
    public void DeclareRequestExchange_ExchangeIsDurable()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.DeclareRequestExchange<PaymentRequested>();
        TopologyDeclaration result = sut.Build();

        // Assert
        result.Exchanges[0].Durable.Should().BeTrue();
    }

    [Fact]
    public void DeclareRequestExchange_ExchangeAutoDeleteIsFalse()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.DeclareRequestExchange<PaymentRequested>();
        TopologyDeclaration result = sut.Build();

        // Assert
        result.Exchanges[0].AutoDelete.Should().BeFalse();
    }

    [Fact]
    public void DeclareRequestExchange_DifferentTypes_AddSeparateExchanges()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.DeclareRequestExchange<PaymentRequested>();
        sut.DeclareRequestExchange<OrderSubmitted>();
        TopologyDeclaration result = sut.Build();

        // Assert
        result.Exchanges.Should().HaveCount(2);
        result.Exchanges.Should().Contain(e => e.Name == RequestExchangeNameFormatter.Format<PaymentRequested>());
        result.Exchanges.Should().Contain(e => e.Name == RequestExchangeNameFormatter.Format<OrderSubmitted>());
    }

    // ── BindRequestExchangeToQueue<T> ─────────────────────────────────────────

    [Fact]
    public void BindRequestExchangeToQueue_AddsBindingWithCorrectExchangeName()
    {
        // Arrange
        var sut = CreateConfigurator();
        string expectedExchange = RequestExchangeNameFormatter.Format<PaymentRequested>();

        // Act
        sut.BindRequestExchangeToQueue<PaymentRequested>("payment-responders");
        TopologyDeclaration result = sut.Build();

        // Assert
        result.ExchangeQueueBindings.Should().ContainSingle()
            .Which.ExchangeName.Should().Be(expectedExchange);
    }

    [Fact]
    public void BindRequestExchangeToQueue_AddsBindingWithCorrectQueueName()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        sut.BindRequestExchangeToQueue<PaymentRequested>("payment-responders");
        TopologyDeclaration result = sut.Build();

        // Assert
        result.ExchangeQueueBindings[0].QueueName.Should().Be("payment-responders");
    }

    [Fact]
    public void BindRequestExchangeToQueue_RoutingKeyIsEmpty()
    {
        // Arrange — fanout exchanges ignore routing key; it must be explicitly empty (not null)
        var sut = CreateConfigurator();

        // Act
        sut.BindRequestExchangeToQueue<PaymentRequested>("payment-responders");
        TopologyDeclaration result = sut.Build();

        // Assert
        result.ExchangeQueueBindings[0].RoutingKey.Should().Be(string.Empty);
    }

    [Fact]
    public void BindRequestExchangeToQueue_NullQueue_ThrowsArgumentException()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        Action act = () => sut.BindRequestExchangeToQueue<PaymentRequested>(null!);

        // Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BindRequestExchangeToQueue_EmptyQueue_ThrowsArgumentException()
    {
        // Arrange
        var sut = CreateConfigurator();

        // Act
        Action act = () => sut.BindRequestExchangeToQueue<PaymentRequested>(string.Empty);

        // Assert
        act.Should().Throw<ArgumentException>();
    }
}
