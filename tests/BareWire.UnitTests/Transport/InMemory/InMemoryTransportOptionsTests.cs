using AwesomeAssertions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Configuration;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class InMemoryTransportOptionsTests
{
    private static InMemoryEndpointConfiguration OrderedEndpoint(string queueName, TransportAffinity affinity)
    {
        var endpoint = new InMemoryEndpointConfiguration(queueName);
        endpoint.OrderedBy(configure => configure.By<object>(static m => m).TransportAffinity(affinity));
        return endpoint;
    }
    [Fact]
    public void Constructor_Defaults_MatchDocumentedValues()
    {
        var o = new InMemoryTransportOptions();
        o.QueueCapacity.Should().Be(1_000);
        o.SendTimeout.Should().Be(TimeSpan.FromMilliseconds(100));
        o.MaxMessageSize.Should().Be(16 * 1024 * 1024);
        o.MaxRedeliveries.Should().Be(20);
        o.DrainTimeout.Should().Be(TimeSpan.FromSeconds(10));
        o.DeferEnabled.Should().BeFalse();
        o.DeferDelay.Should().Be(TimeSpan.FromSeconds(30));
        o.GuaranteedRouting.Should().BeFalse();
        o.AutoDeclareEndpointQueues.Should().BeFalse();
        o.DefaultExchange.Should().BeNull();
    }

    [Fact]
    public void Validate_WithDefaults_DoesNotThrow() =>
        new InMemoryTransportOptions().Invoking(o => o.Validate()).Should().NotThrow();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WhenQueueCapacityNotPositive_ThrowsConfigurationException(int value)
    {
        var o = new InMemoryTransportOptions { QueueCapacity = value };
        o.Invoking(x => x.Validate()).Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(InMemoryTransportOptions.QueueCapacity));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WhenMaxMessageSizeNotPositive_ThrowsConfigurationException(int value)
    {
        var o = new InMemoryTransportOptions { MaxMessageSize = value };
        o.Invoking(x => x.Validate()).Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(InMemoryTransportOptions.MaxMessageSize));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WhenMaxRedeliveriesNotPositive_ThrowsConfigurationException(int value)
    {
        var o = new InMemoryTransportOptions { MaxRedeliveries = value };
        o.Invoking(x => x.Validate()).Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(InMemoryTransportOptions.MaxRedeliveries));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WhenDrainTimeoutNotPositive_ThrowsConfigurationException(int milliseconds)
    {
        var o = new InMemoryTransportOptions { DrainTimeout = TimeSpan.FromMilliseconds(milliseconds) };
        o.Invoking(x => x.Validate()).Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(InMemoryTransportOptions.DrainTimeout));
    }

    [Fact]
    public void Validate_WhenSendTimeoutNegative_ThrowsConfigurationException()
    {
        var o = new InMemoryTransportOptions { SendTimeout = TimeSpan.FromMilliseconds(-1) };
        o.Invoking(x => x.Validate()).Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(InMemoryTransportOptions.SendTimeout));
    }

    [Fact]
    public void Validate_WhenSendTimeoutZero_DoesNotThrow() =>
        new InMemoryTransportOptions { SendTimeout = TimeSpan.Zero }
            .Invoking(o => o.Validate()).Should().NotThrow();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WhenDeferEnabledAndDeferDelayNotPositive_ThrowsConfigurationException(int milliseconds)
    {
        var o = new InMemoryTransportOptions
        {
            DeferEnabled = true,
            DeferDelay = TimeSpan.FromMilliseconds(milliseconds),
        };
        o.Invoking(x => x.Validate()).Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(InMemoryTransportOptions.DeferDelay));
    }

    [Fact]
    public void Validate_WhenDeferDisabledAndDeferDelayZero_DoesNotThrow() =>
        new InMemoryTransportOptions { DeferEnabled = false, DeferDelay = TimeSpan.Zero }
            .Invoking(o => o.Validate()).Should().NotThrow();

    [Theory]
    [InlineData(TransportAffinity.None)]
    [InlineData(TransportAffinity.SingleActiveConsumer)]
    public void Validate_WhenDeferEnabledAndAnEndpointDeclaresOrdering_ThrowsConfigurationException(
        TransportAffinity affinity)
    {
        var o = new InMemoryTransportOptions
        {
            DeferEnabled = true,
            EndpointConfigurations = [OrderedEndpoint("orders", affinity)],
        };

        o.Invoking(x => x.Validate()).Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be("EnableDefer");
    }

    [Fact]
    public void Validate_WhenDeferEnabledAndNoEndpointDeclaresOrdering_DoesNotThrow()
    {
        var o = new InMemoryTransportOptions
        {
            DeferEnabled = true,
            EndpointConfigurations = [new InMemoryEndpointConfiguration("orders")],
        };

        o.Invoking(x => x.Validate()).Should().NotThrow();
    }

    [Theory]
    [InlineData(TransportAffinity.None)]
    [InlineData(TransportAffinity.SingleActiveConsumer)]
    public void Validate_WhenDeferDisabledAndAnEndpointDeclaresOrdering_DoesNotThrow(TransportAffinity affinity)
    {
        var o = new InMemoryTransportOptions
        {
            DeferEnabled = false,
            EndpointConfigurations = [OrderedEndpoint("orders", affinity)],
        };

        o.Invoking(x => x.Validate()).Should().NotThrow();
    }
}
