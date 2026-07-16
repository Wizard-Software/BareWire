using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;

namespace BareWire.UnitTests.Abstractions;

/// <summary>
/// Contract tests for the additive shape of <see cref="ConsumerRegistration"/> (tasks 17.3 + 18.2):
/// the record gains three trailing optional parameters — <c>RoutingKeys</c> (topic-pattern set),
/// <c>AcceptUntyped</c> (secure-by-default type-less opt-in), and <c>UseMassTransitEnvelope</c>
/// (per-consumer MassTransit envelope opt-in). The change must be purely additive: existing
/// two-argument constructions keep compiling and default to catch-all / typed-only / no-envelope.
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

    [Fact]
    public void Constructor_WithUseMassTransitEnvelopeTrue_SetsFlag()
    {
        var registration = new ConsumerRegistration(
            typeof(DummyConsumer),
            typeof(DummyMessage),
            UseMassTransitEnvelope: true);

        registration.UseMassTransitEnvelope.Should().BeTrue();
    }

    [Fact]
    public void Constructor_WithTwoArgs_UseMassTransitEnvelopeFalse()
    {
        var registration = new ConsumerRegistration(typeof(DummyConsumer), typeof(DummyMessage));

        registration.UseMassTransitEnvelope.Should().BeFalse();
    }

    [Fact]
    public void Constructor_WithUseMassTransitEnvelope_DoesNotAffectRoutingKeysOrAcceptUntyped()
    {
        var registration = new ConsumerRegistration(
            typeof(DummyConsumer),
            typeof(DummyMessage),
            UseMassTransitEnvelope: true);

        registration.UseMassTransitEnvelope.Should().BeTrue();
        registration.RoutingKeys.Should().BeNull();
        registration.AcceptUntyped.Should().BeFalse();
    }

    // --- Task 19.4: additive retry carrier (I-1) + endpoint-level knobs, all default-off ---

    [Fact]
    public void Constructor_WithTwoArgs_RetryAndKnobsDefaultToNull()
    {
        var registration = new ConsumerRegistration(typeof(DummyConsumer), typeof(DummyMessage));

        registration.ConfigureRetry.Should().BeNull();
        registration.PrefetchCount.Should().BeNull();
        registration.ConcurrentMessageLimit.Should().BeNull();
    }

    [Fact]
    public void ConfigureRetry_IsTypedOnPublicContract_NotUntypedHandle()
    {
        var property = typeof(ConsumerRegistration).GetProperty(nameof(ConsumerRegistration.ConfigureRetry))!;

        property.PropertyType.Should().Be<Action<IRetryConfigurator>>();
        typeof(IRetryConfigurator).Assembly
            .Should().BeSameAs(typeof(ConsumerRegistration).Assembly);
    }

    [Fact]
    public void ConfigureRetry_InvokesFluentContract_WhenSet()
    {
        var invoked = false;
        Action<IRetryConfigurator> configure = c =>
        {
            invoked = true;
            c.Interval(3, TimeSpan.FromSeconds(1));
        };

        var registration = new ConsumerRegistration(
            typeof(DummyConsumer),
            typeof(DummyMessage),
            ConfigureRetry: configure);

        registration.ConfigureRetry.Should().BeSameAs(configure);

        registration.ConfigureRetry!(new FakeRetryConfigurator());

        invoked.Should().BeTrue();
    }

    [Fact]
    public void Constructor_WithPrefetchCount_StoresValue()
    {
        var registration = new ConsumerRegistration(
            typeof(DummyConsumer),
            typeof(DummyMessage),
            PrefetchCount: 32);

        registration.PrefetchCount.Should().Be(32);
        registration.ConcurrentMessageLimit.Should().BeNull();
    }

    [Fact]
    public void Constructor_WithConcurrentMessageLimit_StoresValue()
    {
        var registration = new ConsumerRegistration(
            typeof(DummyConsumer),
            typeof(DummyMessage),
            ConcurrentMessageLimit: 8);

        registration.ConcurrentMessageLimit.Should().Be(8);
        registration.PrefetchCount.Should().BeNull();
    }

    /// <summary>
    /// No-op fluent stand-in for <see cref="IRetryConfigurator"/> proving the retry carrier is
    /// callable through the public contract alone, without any core type.
    /// </summary>
    private sealed class FakeRetryConfigurator : IRetryConfigurator
    {
        public IRetryConfigurator Interval(int retryCount, TimeSpan interval) => this;

        public IRetryConfigurator Incremental(int retryCount, TimeSpan initial, TimeSpan increment) => this;

        public IRetryConfigurator Exponential(int retryCount, TimeSpan minInterval, TimeSpan maxInterval) => this;

        public IRetryConfigurator Handle<TException>() where TException : Exception => this;

        public IRetryConfigurator Ignore<TException>() where TException : Exception => this;
    }
}
