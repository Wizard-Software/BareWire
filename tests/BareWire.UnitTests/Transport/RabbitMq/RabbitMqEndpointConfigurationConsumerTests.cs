using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Transport.RabbitMQ.Configuration;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// Tests for the transport <see cref="RabbitMqEndpointConfiguration.Consumer{TConsumer, TMessage}(Action{IConsumerConfigurator{TConsumer, TMessage}})"/>
/// overload and the <c>internal sealed ConsumerConfigurator&lt;,&gt;</c> it drives — asserts the accumulation
/// semantics (set of routing-key patterns with order-preserving dedup), the idempotent secure-by-default
/// <c>AcceptUntyped</c> flag, catch-all materialization (empty set → <see langword="null"/>), and the
/// published "must not be null or empty" pattern contract. Mirrors the ratified core reference
/// (<c>ReceiveEndpointConfiguration</c> / 17.6): the configurator is per-project, the impl is internal, and
/// both overloads materialize a <see cref="ConsumerRegistration"/> with identical semantics.
/// </summary>
public sealed class RabbitMqEndpointConfigurationConsumerTests
{
    [Fact]
    public void Consumer_WithConfigure_AccumulatesRoutingKeysDistinctPreservingOrder()
    {
        RabbitMqEndpointConfiguration sut = new("test-queue");

        sut.Consumer<FakeConsumer, FakeMessage>(c =>
        {
            c.RoutingKey("a.*");
            c.RoutingKeys("b.#", "a.*"); // "a.*" duplicate — idempotent
        });

        ConsumerRegistration registration = sut.ConsumerRegistrations.Should().ContainSingle().Subject;
        registration.ConsumerType.Should().Be<FakeConsumer>();
        registration.MessageType.Should().Be<FakeMessage>();
        registration.RoutingKeys.Should().Equal("a.*", "b.#"); // order preserved, second "a.*" deduped
        registration.AcceptUntyped.Should().BeFalse();
    }

    [Fact]
    public void Consumer_WithAcceptUntypedCalledMultipleTimes_SetsFlagTrueIdempotently()
    {
        RabbitMqEndpointConfiguration sut = new("test-queue");

        sut.Consumer<FakeConsumer, FakeMessage>(c =>
        {
            c.AcceptUntyped();
            c.AcceptUntyped();
        });

        ConsumerRegistration registration = sut.ConsumerRegistrations.Should().ContainSingle().Subject;
        registration.AcceptUntyped.Should().BeTrue();
    }

    [Fact]
    public void Consumer_WithRoutingKeyAndAcceptUntyped_MaterializesBoth()
    {
        RabbitMqEndpointConfiguration sut = new("test-queue");

        sut.Consumer<FakeConsumer, FakeMessage>(c =>
        {
            c.RoutingKey("x.y");
            c.AcceptUntyped();
        });

        ConsumerRegistration registration = sut.ConsumerRegistrations.Should().ContainSingle().Subject;
        registration.RoutingKeys.Should().Equal("x.y");
        registration.AcceptUntyped.Should().BeTrue();
    }

    [Fact]
    public void Consumer_WithEmptyConfigure_IsCatchAll()
    {
        RabbitMqEndpointConfiguration sut = new("test-queue");

        sut.Consumer<FakeConsumer, FakeMessage>(_ => { });

        ConsumerRegistration registration = sut.ConsumerRegistrations.Should().ContainSingle().Subject;
        registration.RoutingKeys.Should().BeNull();
        registration.AcceptUntyped.Should().BeFalse();
    }

    [Fact]
    public void Consumer_Parameterless_RegistersCatchAll()
    {
        RabbitMqEndpointConfiguration sut = new("test-queue");

        sut.Consumer<FakeConsumer, FakeMessage>();

        ConsumerRegistration registration = sut.ConsumerRegistrations.Should().ContainSingle().Subject;
        registration.ConsumerType.Should().Be<FakeConsumer>();
        registration.MessageType.Should().Be<FakeMessage>();
        registration.RoutingKeys.Should().BeNull();
        registration.AcceptUntyped.Should().BeFalse();
    }

    [Fact]
    public void Consumer_CalledForMultipleConsumers_ProducesOneRegistrationEach()
    {
        RabbitMqEndpointConfiguration sut = new("test-queue");

        sut.Consumer<FakeConsumer, FakeMessage>(c => c.RoutingKey("a.*"));
        sut.Consumer<OtherConsumer, OtherMessage>();

        sut.ConsumerRegistrations.Should().HaveCount(2);
        sut.ConsumerRegistrations[0].ConsumerType.Should().Be<FakeConsumer>();
        sut.ConsumerRegistrations[0].RoutingKeys.Should().Equal("a.*");
        sut.ConsumerRegistrations[1].ConsumerType.Should().Be<OtherConsumer>();
        sut.ConsumerRegistrations[1].RoutingKeys.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Consumer_RoutingKey_WithNullOrEmpty_Throws(string? routingKey)
    {
        RabbitMqEndpointConfiguration sut = new("test-queue");

        Action act = () => sut.Consumer<FakeConsumer, FakeMessage>(c => c.RoutingKey(routingKey!));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Consumer_RoutingKeys_WithEmptyElement_Throws()
    {
        RabbitMqEndpointConfiguration sut = new("test-queue");

        Action act = () => sut.Consumer<FakeConsumer, FakeMessage>(c => c.RoutingKeys("ok", ""));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Consumer_RoutingKeys_WithNullArray_Throws()
    {
        RabbitMqEndpointConfiguration sut = new("test-queue");

        Action act = () => sut.Consumer<FakeConsumer, FakeMessage>(c => c.RoutingKeys(null!));

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Consumer_WithNullConfigure_Throws()
    {
        RabbitMqEndpointConfiguration sut = new("test-queue");

        Action act = () => sut.Consumer<FakeConsumer, FakeMessage>(
            (Action<IConsumerConfigurator<FakeConsumer, FakeMessage>>)null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── Fake stub types ────────────────────────────────────────────────────────

    private sealed record FakeMessage;

    private sealed class FakeConsumer : IConsumer<FakeMessage>
    {
        public Task ConsumeAsync(ConsumeContext<FakeMessage> context) => Task.CompletedTask;
    }

    private sealed record OtherMessage;

    private sealed class OtherConsumer : IConsumer<OtherMessage>
    {
        public Task ConsumeAsync(ConsumeContext<OtherMessage> context) => Task.CompletedTask;
    }
}
