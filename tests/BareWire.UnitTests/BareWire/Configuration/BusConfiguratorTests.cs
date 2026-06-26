using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Pipeline;
using BareWire.Abstractions.Serialization;
using BareWire.Configuration;

namespace BareWire.UnitTests.Core.Configuration;

public sealed class BusConfiguratorTests
{
    // ── UseRabbitMQ (deprecated no-op — Feature 15, ADR-028 D4) ────────────────
    // UseRabbitMQ is [Obsolete] (deprecated marker reduced to a no-op): real RabbitMQ settings flow
    // through AddBareWireRabbitMq / AddBareWireWithRabbitMq, never through this bus-level marker.
    // CS0618 is suppressed for these tests because they intentionally exercise the deprecated method.
#pragma warning disable CS0618 // Type or member is obsolete

    [Fact]
    public void UseRabbitMQ_NullConfigure_ThrowsArgumentNullException()
    {
        // Arrange
        BusConfigurator sut = new();

        // Act
        Action act = () => sut.UseRabbitMQ(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("configure");
    }

    [Fact]
    public void UseRabbitMQ_AfterDeprecation_IsNoOp_DoesNotSetMarker()
    {
        // Arrange
        BusConfigurator sut = new();
        Action<IRabbitMqConfigurator> configure = _ => { };

        // Act
        sut.UseRabbitMQ(configure);

        // Assert — the deprecated method no longer captures the delegate: the marker stays null and,
        // with no InMemory transport registered, HasTransport remains false. This proves the no-op
        // (before the change the delegate was stored and HasTransport would be true).
        sut.RabbitMqConfigurator.Should().BeNull();
        sut.HasTransport.Should().BeFalse();
    }

#pragma warning restore CS0618 // Type or member is obsolete

    // ── ConfigureObservability ────────────────────────────────────────────────

    [Fact]
    public void ConfigureObservability_NullConfigure_ThrowsArgumentNullException()
    {
        // Arrange
        BusConfigurator sut = new();

        // Act
        Action act = () => sut.ConfigureObservability(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("configure");
    }

    [Fact]
    public void ConfigureObservability_ValidConfigure_StoresConfigurator()
    {
        // Arrange
        BusConfigurator sut = new();
        Action<object> configure = _ => { };

        // Act
        sut.ConfigureObservability(configure);

        // Assert
        sut.ObservabilityConfigurator.Should().BeSameAs(configure);
    }

    // ── AddMiddleware ─────────────────────────────────────────────────────────

    [Fact]
    public void AddMiddleware_SingleCall_AddsTypeToMiddlewareTypes()
    {
        // Arrange
        BusConfigurator sut = new();

        // Act
        sut.AddMiddleware<StubMiddleware>();

        // Assert
        sut.MiddlewareTypes.Should().ContainSingle()
            .Which.Should().BeSameAs(typeof(StubMiddleware));
    }

    [Fact]
    public void AddMiddleware_MultipleCalls_AddsAllTypesInOrder()
    {
        // Arrange
        BusConfigurator sut = new();

        // Act
        sut.AddMiddleware<StubMiddleware>();
        sut.AddMiddleware<AnotherStubMiddleware>();

        // Assert
        sut.MiddlewareTypes.Should().HaveCount(2);
        sut.MiddlewareTypes[0].Should().BeSameAs(typeof(StubMiddleware));
        sut.MiddlewareTypes[1].Should().BeSameAs(typeof(AnotherStubMiddleware));
    }

    // ── UseSerializer ─────────────────────────────────────────────────────────

    [Fact]
    public void UseSerializer_Called_SetsSerializerType()
    {
        // Arrange
        BusConfigurator sut = new();

        // Act
        sut.UseSerializer<StubSerializer>();

        // Assert
        sut.SerializerType.Should().BeSameAs(typeof(StubSerializer));
    }

    [Fact]
    public void UseSerializer_CalledTwice_OverwritesWithLastType()
    {
        // Arrange
        BusConfigurator sut = new();

        // Act
        sut.UseSerializer<StubSerializer>();
        sut.UseSerializer<AnotherStubSerializer>();

        // Assert
        sut.SerializerType.Should().BeSameAs(typeof(AnotherStubSerializer));
    }

    // ── AddReceiveEndpoint ────────────────────────────────────────────────────

    [Fact]
    public void AddReceiveEndpoint_NullEndpointName_ThrowsArgumentNullException()
    {
        // Arrange
        BusConfigurator sut = new();
        Action<IReceiveEndpointConfigurator> configure = _ => { };

        // Act
        Action act = () => sut.AddReceiveEndpoint(null!, configure);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("endpointName");
    }

    [Fact]
    public void AddReceiveEndpoint_NullConfigure_ThrowsArgumentNullException()
    {
        // Arrange
        BusConfigurator sut = new();

        // Act
        Action act = () => sut.AddReceiveEndpoint("my-queue", null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("configure");
    }

    // ── Stub implementations ──────────────────────────────────────────────────

    private sealed class StubMiddleware : IMessageMiddleware
    {
        public Task InvokeAsync(MessageContext context, NextMiddleware nextMiddleware)
            => nextMiddleware(context);
    }

    private sealed class AnotherStubMiddleware : IMessageMiddleware
    {
        public Task InvokeAsync(MessageContext context, NextMiddleware nextMiddleware)
            => nextMiddleware(context);
    }

    private sealed class StubSerializer : IMessageSerializer
    {
        public string ContentType => "application/octet-stream";

        public void Serialize<T>(T message, IBufferWriter<byte> output) where T : class { }
    }

    private sealed class AnotherStubSerializer : IMessageSerializer
    {
        public string ContentType => "application/json";

        public void Serialize<T>(T message, IBufferWriter<byte> output) where T : class { }
    }
}
