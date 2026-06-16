using AwesomeAssertions;

using BareWire.Abstractions.Serialization;
using BareWire.CloudEvents;

using NSubstitute;

namespace BareWire.UnitTests.CloudEvents;

/// <summary>
/// Tests for <see cref="ContentTypeDeserializerRouter"/> — the package-local
/// <see cref="IDeserializerResolver"/> decorator that routes
/// <c>application/cloudevents+json</c> to the CloudEvents envelope deserializer and
/// delegates every other content type to the wrapped inner resolver (raw-JSON fallback, ADR-001).
/// </summary>
public sealed class ContentTypeDeserializerRouterTests
{
    private readonly IDeserializerResolver _inner = Substitute.For<IDeserializerResolver>();
    private readonly IMessageDeserializer _cloudEventsDeserializer = Substitute.For<IMessageDeserializer>();
    private readonly IMessageDeserializer _fallbackDeserializer = Substitute.For<IMessageDeserializer>();

    // -------------------------------------------------------------------------
    // Required test (13.12): CE content type routes to the CloudEvents deserializer
    // -------------------------------------------------------------------------

    [Fact]
    public void Resolve_ApplicationCloudEventsJson_ReturnsCloudEventsDeserializer()
    {
        var sut = new ContentTypeDeserializerRouter(_inner, _cloudEventsDeserializer);

        IMessageDeserializer result = sut.Resolve("application/cloudevents+json");

        result.Should().BeSameAs(_cloudEventsDeserializer);
    }

    // -------------------------------------------------------------------------
    // Guard: case-insensitive match still routes to the CloudEvents deserializer
    // -------------------------------------------------------------------------

    [Fact]
    public void Resolve_CloudEventsJsonUppercase_ReturnsCloudEventsDeserializer()
    {
        var sut = new ContentTypeDeserializerRouter(_inner, _cloudEventsDeserializer);

        IMessageDeserializer result = sut.Resolve("APPLICATION/CLOUDEVENTS+JSON");

        result.Should().BeSameAs(_cloudEventsDeserializer);
    }

    // -------------------------------------------------------------------------
    // Guard (13.12): an unrelated/raw content type delegates to the inner resolver (ADR-001 fallback)
    // -------------------------------------------------------------------------

    [Fact]
    public void Resolve_ApplicationJson_DelegatesToInnerResolver()
    {
        _inner.Resolve("application/json").Returns(_fallbackDeserializer);
        var sut = new ContentTypeDeserializerRouter(_inner, _cloudEventsDeserializer);

        IMessageDeserializer result = sut.Resolve("application/json");

        result.Should().BeSameAs(_fallbackDeserializer);
        _inner.Received(1).Resolve("application/json");
    }

    // -------------------------------------------------------------------------
    // Guard: null content type delegates to the inner resolver (never the CE deserializer)
    // -------------------------------------------------------------------------

    [Fact]
    public void Resolve_NullContentType_DelegatesToInnerResolver()
    {
        _inner.Resolve(null).Returns(_fallbackDeserializer);
        var sut = new ContentTypeDeserializerRouter(_inner, _cloudEventsDeserializer);

        IMessageDeserializer result = sut.Resolve(null);

        result.Should().BeSameAs(_fallbackDeserializer);
    }

    // -------------------------------------------------------------------------
    // Null-guards on the constructor arguments
    // -------------------------------------------------------------------------

    [Fact]
    public void Ctor_WhenInnerNull_ThrowsArgumentNullException()
    {
        Action act = () => _ = new ContentTypeDeserializerRouter(null!, _cloudEventsDeserializer);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_WhenCloudEventsDeserializerNull_ThrowsArgumentNullException()
    {
        Action act = () => _ = new ContentTypeDeserializerRouter(_inner, null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
