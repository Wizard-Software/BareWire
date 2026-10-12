using Azure.Messaging.ServiceBus;
using AwesomeAssertions;
using BareWire.Transport.AzureServiceBus.Internal;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Transport.AzureServiceBus;

public sealed class AzureServiceBusConsumerRegistrySessionIndexTests
{
    [Fact]
    public void TryEvictMessage_SessionMessage_RemovesTagFromSessionPendingCount()
    {
        // Arrange
        var registry = new AzureServiceBusConsumerRegistry();
        registry.RegisterSession("c1");
        ServiceBusReceiver receiver = Substitute.For<ServiceBusReceiver>();
        registry.StoreMessage("c1", 1UL, AzureServiceBusShutdownTestSupport.Message("m1", "s1"), receiver, "s1");
        registry.StoreMessage("c1", 2UL, AzureServiceBusShutdownTestSupport.Message("m2", "s1"), receiver, "s1");
        registry.StoreMessage("c1", 3UL, AzureServiceBusShutdownTestSupport.Message("m3", "s2"), receiver, "s2");

        // Act
        registry.TryEvictMessage("c1", 1UL);

        // Assert
        registry.SessionPendingSettlementCount("c1", "s1").Should().Be(1);
        registry.SessionPendingSettlementCount("c1", "s2").Should().Be(1);
    }

    [Fact]
    public void TryEvictMessage_KeepSessionTag_CountsUntilReleased()
    {
        // Arrange - a settlement evicts the entry but is still inside the SDK call.
        var registry = new AzureServiceBusConsumerRegistry();
        registry.RegisterSession("c1");
        ServiceBusReceiver receiver = Substitute.For<ServiceBusReceiver>();
        registry.StoreMessage("c1", 1UL, AzureServiceBusShutdownTestSupport.Message("m1", "s1"), receiver, "s1");

        // Act
        var entry = registry.TryEvictMessage("c1", 1UL, releaseSessionTag: false);

        // Assert
        entry.Should().NotBeNull();
        entry!.Value.SessionId.Should().Be("s1");
        registry.SessionPendingSettlementCount("c1", "s1").Should().Be(1);
        registry.ReleaseSessionTag("c1", entry.Value.SessionId, 1UL);
        registry.SessionPendingSettlementCount("c1", "s1").Should().Be(0);
    }

    [Fact]
    public void ReleaseSessionTag_AfterEvictAllForSession_DoesNotRecreateSession()
    {
        // Arrange
        var registry = new AzureServiceBusConsumerRegistry();
        registry.RegisterSession("c1");
        ServiceBusReceiver receiver = Substitute.For<ServiceBusReceiver>();
        registry.StoreMessage("c1", 1UL, AzureServiceBusShutdownTestSupport.Message("m1", "s1"), receiver, "s1");
        var entry = registry.TryEvictMessage("c1", 1UL, releaseSessionTag: false);
        registry.EvictAllForSession("c1", "s1");

        // Act
        Action act = () => registry.ReleaseSessionTag("c1", entry!.Value.SessionId, 1UL);

        // Assert
        act.Should().NotThrow();
        registry.SessionPendingSettlementCount("c1", "s1").Should().Be(0);
    }

    [Fact]
    public void SessionPendingSettlementCount_UnknownConsumerOrSession_ReturnsZero()
    {
        // Arrange
        var registry = new AzureServiceBusConsumerRegistry();

        // Act + Assert
        registry.SessionPendingSettlementCount("nope", "s1").Should().Be(0);
        registry.RegisterSession("c1");
        registry.SessionPendingSettlementCount("c1", "s1").Should().Be(0);
    }
}
