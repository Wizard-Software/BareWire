using AwesomeAssertions;

using BareWire.Abstractions.Serialization;
using BareWire.Serialization.Json;
using BareWire.Serialization.MsgPack;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

namespace BareWire.UnitTests.Serialization;

public sealed class MessagePackDeserializerRoutingTests
{
    // ── Router unit tests ───────────────────────────────────────────────────────

    [Fact]
    public void Resolve_ApplicationXMsgpack_ReturnsMessagePackDeserializer()
    {
        var inner = Substitute.For<IDeserializerResolver>();
        var msgpack = Substitute.For<IMessageDeserializer>();
        msgpack.ContentType.Returns("application/x-msgpack");

        var sut = new MessagePackDeserializerRouter(inner, msgpack);

        var result = sut.Resolve("application/x-msgpack");

        result.Should().BeSameAs(msgpack);
        inner.DidNotReceive().Resolve(Arg.Any<string?>());
    }

    [Fact]
    public void Resolve_ApplicationJson_DelegatesToInner()
    {
        var inner = Substitute.For<IDeserializerResolver>();
        var jsonDeserializer = Substitute.For<IMessageDeserializer>();
        inner.Resolve("application/json").Returns(jsonDeserializer);

        var msgpack = Substitute.For<IMessageDeserializer>();
        msgpack.ContentType.Returns("application/x-msgpack");

        var sut = new MessagePackDeserializerRouter(inner, msgpack);

        var result = sut.Resolve("application/json");

        result.Should().BeSameAs(jsonDeserializer);
        inner.Received(1).Resolve("application/json");
    }

    [Fact]
    public void Resolve_Null_DelegatesToInner()
    {
        var inner = Substitute.For<IDeserializerResolver>();
        var fallback = Substitute.For<IMessageDeserializer>();
        inner.Resolve(null).Returns(fallback);

        var msgpack = Substitute.For<IMessageDeserializer>();
        msgpack.ContentType.Returns("application/x-msgpack");

        var sut = new MessagePackDeserializerRouter(inner, msgpack);

        var result = sut.Resolve(null);

        result.Should().BeSameAs(fallback);
        inner.Received(1).Resolve(null);
    }

    [Fact]
    public void Resolve_CaseInsensitive_APPLICATION_X_MSGPACK_ReturnsMessagePackDeserializer()
    {
        var inner = Substitute.For<IDeserializerResolver>();
        var msgpack = Substitute.For<IMessageDeserializer>();
        msgpack.ContentType.Returns("application/x-msgpack");

        var sut = new MessagePackDeserializerRouter(inner, msgpack);

        var result = sut.Resolve("APPLICATION/X-MSGPACK");

        result.Should().BeSameAs(msgpack);
        inner.DidNotReceive().Resolve(Arg.Any<string?>());
    }

    [Fact]
    public void Resolve_MsgpackWithParameters_DelegatesToInner_FailClosed()
    {
        // application/x-msgpack; charset=utf-8 is NOT an exact match — delegate to inner (fail-closed).
        var inner = Substitute.For<IDeserializerResolver>();
        var fallback = Substitute.For<IMessageDeserializer>();
        inner.Resolve("application/x-msgpack; charset=utf-8").Returns(fallback);

        var msgpack = Substitute.For<IMessageDeserializer>();
        msgpack.ContentType.Returns("application/x-msgpack");

        var sut = new MessagePackDeserializerRouter(inner, msgpack);

        var result = sut.Resolve("application/x-msgpack; charset=utf-8");

        result.Should().BeSameAs(fallback);
        inner.Received(1).Resolve("application/x-msgpack; charset=utf-8");
    }

    [Fact]
    public void Ctor_NullInner_ThrowsArgumentNullException()
    {
        var msgpack = Substitute.For<IMessageDeserializer>();
        msgpack.ContentType.Returns("application/x-msgpack");

        Action act = () => _ = new MessagePackDeserializerRouter(null!, msgpack);

        act.Should().Throw<ArgumentNullException>().WithParameterName("inner");
    }

    [Fact]
    public void Ctor_NullDeserializer_ThrowsArgumentNullException()
    {
        var inner = Substitute.For<IDeserializerResolver>();

        Action act = () => _ = new MessagePackDeserializerRouter(inner, null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("messagePackDeserializer");
    }

    // ── Registration integration tests ─────────────────────────────────────────

    [Fact]
    public void AddBareWireMessagePackDeserializerRouting_ResolvesRouterThatRoutesXMsgpackToMsgPack()
    {
        var provider = new ServiceCollection()
            .AddBareWireJsonSerializer()
            .AddBareWireMessagePackDeserializerRouting()
            .BuildServiceProvider();

        var resolver = provider.GetRequiredService<IDeserializerResolver>();

        var msgpackDeserializer = provider.GetRequiredService<MessagePackDeserializer>();
        var resolved = resolver.Resolve("application/x-msgpack");

        resolved.Should().BeSameAs(msgpackDeserializer);
    }

    [Fact]
    public void AddBareWireMessagePackDeserializerRouting_NonMsgpackContentType_FallsBackToJson()
    {
        var provider = new ServiceCollection()
            .AddBareWireJsonSerializer()
            .AddBareWireMessagePackDeserializerRouting()
            .BuildServiceProvider();

        var resolver = provider.GetRequiredService<IDeserializerResolver>();

        // null content-type should fall back to raw-JSON (ADR-001 raw-first preserved)
        var resolved = resolver.Resolve(null);

        resolved.Should().NotBeOfType<MessagePackDeserializer>();
    }

    [Fact]
    public void AddBareWireMessagePackDeserializerRouting_CalledTwice_IsIdempotent()
    {
        var services = new ServiceCollection()
            .AddBareWireJsonSerializer()
            .AddBareWireMessagePackDeserializerRouting()
            .AddBareWireMessagePackDeserializerRouting(); // second call — must be no-op

        // Exactly one IDeserializerResolver descriptor after double-registration
        services.Count(d => d.ServiceType == typeof(IDeserializerResolver)).Should().Be(1);

        // Exactly one routing marker
        services.Count(d => d.ServiceType == typeof(MessagePackDeserializerRoutingMarker)).Should().Be(1);

        var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<IDeserializerResolver>();

        // Routing still works correctly after double-registration
        var msgpackDeserializer = provider.GetRequiredService<MessagePackDeserializer>();
        resolver.Resolve("application/x-msgpack").Should().BeSameAs(msgpackDeserializer);
    }

    [Fact]
    public void AddBareWireMessagePackDeserializerRouting_WithoutPriorResolver_ThrowsInvalidOperationException()
    {
        // No AddBareWireJsonSerializer() — no IDeserializerResolver registered
        Action act = () => new ServiceCollection()
            .AddBareWireMessagePackDeserializerRouting();

        act.Should().Throw<InvalidOperationException>();
    }
}
