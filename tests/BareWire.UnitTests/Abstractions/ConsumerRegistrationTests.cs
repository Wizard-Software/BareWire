using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;

namespace BareWire.UnitTests.Abstractions;

/// <summary>
/// Contract tests for the additive shape of <see cref="ConsumerRegistration"/> (task 17.3):
/// the record gains two trailing optional parameters — <c>RoutingKeys</c> (topic-pattern set) and
/// <c>AcceptUntyped</c> (secure-by-default type-less opt-in). The change must be purely additive:
/// existing two-argument constructions keep compiling and default to catch-all / typed-only.
/// </summary>
public sealed class ConsumerRegistrationTests
{
    private sealed record DummyMessage(string Value);

    private sealed class DummyConsumer : IConsumer<DummyMessage>
    {
        public Task ConsumeAsync(ConsumeContext<DummyMessage> context) => Task.CompletedTask;
    }

    [Fact]
    public void Constructor_WithTwoArgs_RoutingKeysNullAndAcceptUntypedFalse()
    {
        var registration = new ConsumerRegistration(typeof(DummyConsumer), typeof(DummyMessage));

        registration.RoutingKeys.Should().BeNull();
        registration.AcceptUntyped.Should().BeFalse();
    }

    [Fact]
    public void Constructor_WithTwoArgs_PreservesConsumerAndMessageTypes()
    {
        var registration = new ConsumerRegistration(typeof(DummyConsumer), typeof(DummyMessage));

        registration.ConsumerType.Should().Be<DummyConsumer>();
        registration.MessageType.Should().Be<DummyMessage>();
    }

    [Fact]
    public void Constructor_WithRoutingKeys_StoresPatternListInOrder()
    {
        string[] patterns = ["transfer.eu.*", "transfer.pl.#"];

        var registration = new ConsumerRegistration(
            typeof(DummyConsumer),
            typeof(DummyMessage),
            RoutingKeys: patterns);

        registration.RoutingKeys.Should().NotBeNull();
        registration.RoutingKeys.Should().Equal("transfer.eu.*", "transfer.pl.#");
        registration.AcceptUntyped.Should().BeFalse();
    }

    [Fact]
    public void Constructor_WithEmptyRoutingKeys_StoresEmptyList()
    {
        var registration = new ConsumerRegistration(
            typeof(DummyConsumer),
            typeof(DummyMessage),
            RoutingKeys: []);

        registration.RoutingKeys.Should().NotBeNull();
        registration.RoutingKeys.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_WithAcceptUntypedTrue_SetsFlag()
    {
        var registration = new ConsumerRegistration(
            typeof(DummyConsumer),
            typeof(DummyMessage),
            AcceptUntyped: true);

        registration.AcceptUntyped.Should().BeTrue();
        registration.RoutingKeys.Should().BeNull();
    }
}
