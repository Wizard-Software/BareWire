using AwesomeAssertions;
using BareWire.Abstractions.Configuration;
using BareWire.Configuration;

namespace BareWire.UnitTests.Abstractions;

/// <summary>
/// Unit tests for the per-key consumer-ordering configuration surface (R8.4): the three
/// <c>OrderedBy</c>/<c>OrderedByHeader</c> entry points on <see cref="IReceiveEndpointConfigurator"/> and
/// the <see cref="IConsumerOrderingConfigurator"/> block form, verified against the internal Core carrier.
/// </summary>
public sealed class ConsumerOrderingConfiguratorTests
{
    private sealed record OrderTestMessage(string CustomerId);

    private static ReceiveEndpointConfiguration CreateEndpoint() => new("orders");

    [Fact]
    public void OrderedByHeader_WithHeaderName_StoresHeaderAsKeySource()
    {
        var endpoint = CreateEndpoint();

        endpoint.OrderedByHeader("ordering-key");

        endpoint.Ordering.Should().NotBeNull();
        endpoint.Ordering!.HeaderName.Should().Be("ordering-key");
        endpoint.Ordering.Selector.Should().BeNull();
        endpoint.Ordering.UseCorrelationId.Should().BeFalse();
    }

    [Fact]
    public void OrderedBySelector_WithTypedSelector_StoresSelectorAndMessageType()
    {
        var endpoint = CreateEndpoint();

        endpoint.OrderedBy<OrderTestMessage>(m => m.CustomerId);

        endpoint.Ordering.Should().NotBeNull();
        endpoint.Ordering!.Selector.Should().NotBeNull();
        endpoint.Ordering.SelectorMessageType.Should().Be<OrderTestMessage>();
        endpoint.Ordering.HeaderName.Should().BeNull();
    }

    [Fact]
    public void OrderedByBlock_WithFullConfiguration_StoresAllSettings()
    {
        var endpoint = CreateEndpoint();

        endpoint.OrderedBy(o =>
        {
            o.ByHeader("ordering-key");
            o.Concurrency(4);
            o.Strategy(ConsumerOrderingStrategy.LocalPartitioned);
            o.TransportAffinity(TransportAffinity.SingleActiveConsumer);
            o.MaxDeliveryAttempts(3);
        });

        endpoint.Ordering.Should().NotBeNull();
        endpoint.Ordering!.HeaderName.Should().Be("ordering-key");
        endpoint.Ordering.Concurrency_.Should().Be(4);
        endpoint.Ordering.Strategy_.Should().Be(ConsumerOrderingStrategy.LocalPartitioned);
        endpoint.Ordering.TransportAffinity_.Should().Be(TransportAffinity.SingleActiveConsumer);
        endpoint.Ordering.MaxDeliveryAttempts_.Should().Be(3);
    }

    [Fact]
    public void OrderedByBlock_WithCorrelationId_StoresCorrelationIdFlag()
    {
        var endpoint = CreateEndpoint();

        endpoint.OrderedBy(o => o.ByCorrelationId());

        endpoint.Ordering.Should().NotBeNull();
        endpoint.Ordering!.UseCorrelationId.Should().BeTrue();
        endpoint.Ordering.HeaderName.Should().BeNull();
        endpoint.Ordering.Selector.Should().BeNull();
    }

    [Fact]
    public void OrderedByBlock_DefaultStrategy_IsAuto()
    {
        var endpoint = CreateEndpoint();

        endpoint.OrderedBy(o => o.ByHeader("k"));

        endpoint.Ordering!.Strategy_.Should().Be(ConsumerOrderingStrategy.Auto);
    }

    [Fact]
    public void OrderedByBlock_DefaultTransportAffinity_IsNone()
    {
        var endpoint = CreateEndpoint();

        endpoint.OrderedBy(o => o.ByHeader("k"));

        endpoint.Ordering!.TransportAffinity_.Should().Be(TransportAffinity.None);
    }

    [Fact]
    public void OrderedByBlock_DefaultMaxDeliveryAttempts_IsZero()
    {
        var endpoint = CreateEndpoint();

        endpoint.OrderedBy(o => o.ByHeader("k"));

        endpoint.Ordering!.MaxDeliveryAttempts_.Should().Be(0);
    }

    [Fact]
    public void OrderedByBlock_WithNullConfigure_Throws()
    {
        var endpoint = CreateEndpoint();

        var act = () => endpoint.OrderedBy((Action<IConsumerOrderingConfigurator>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void OrderedBySelector_WithNullSelector_Throws()
    {
        var endpoint = CreateEndpoint();

        var act = () => endpoint.OrderedBy<OrderTestMessage>(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void OrderedByHeader_WithNullOrEmptyName_Throws(string? headerName)
    {
        var endpoint = CreateEndpoint();

        var act = () => endpoint.OrderedByHeader(headerName!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Endpoint_WithoutOrderedBy_HasNullOrdering()
    {
        var endpoint = CreateEndpoint();

        endpoint.Ordering.Should().BeNull();
    }

    [Fact]
    public void ByMethods_ReturnConfiguratorInstance_ForChaining()
    {
        var configuration = new ConsumerOrderingConfiguration();

        configuration.ByHeader("k").Should().BeSameAs(configuration);
        configuration.By<OrderTestMessage>(m => m.CustomerId).Should().BeSameAs(configuration);
        configuration.ByCorrelationId().Should().BeSameAs(configuration);
        configuration.Concurrency(2).Should().BeSameAs(configuration);
        configuration.Strategy(ConsumerOrderingStrategy.TransportNative).Should().BeSameAs(configuration);
        configuration.TransportAffinity(TransportAffinity.ConsistentHash).Should().BeSameAs(configuration);
        configuration.MaxDeliveryAttempts(5).Should().BeSameAs(configuration);
    }

    [Fact]
    public void OrderedBy_CalledTwice_LastConfigurationWins()
    {
        var endpoint = CreateEndpoint();

        endpoint.OrderedByHeader("first");
        endpoint.OrderedBy<OrderTestMessage>(m => m.CustomerId);

        endpoint.Ordering!.HeaderName.Should().BeNull();
        endpoint.Ordering.SelectorMessageType.Should().Be<OrderTestMessage>();
    }
}
