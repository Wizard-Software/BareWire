using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions.Serialization;
using BareWire.Interop.MassTransit;
using BareWire.Serialization.Json;
using NSubstitute;

namespace BareWire.UnitTests.Serialization;

public sealed class ContentTypeDeserializerRouterTests
{
    private readonly SystemTextJsonRawDeserializer _rawDeserializer = new();
    private readonly BareWireEnvelopeSerializer _envelopeDeserializer = new();
    private readonly MassTransitEnvelopeDeserializer _mtDeserializer = new();

    [Fact]
    public void Resolve_ApplicationJson_ReturnsRawDeserializer()
    {
        var sut = new ContentTypeDeserializerRouter(
            _rawDeserializer,
            [_envelopeDeserializer]);

        var result = sut.Resolve("application/json");

        result.Should().BeSameAs(_rawDeserializer);
    }

    [Fact]
    public void Resolve_VndBarewireJson_ReturnsEnvelopeDeserializer()
    {
        var sut = new ContentTypeDeserializerRouter(
            _rawDeserializer,
            [_envelopeDeserializer]);

        var result = sut.Resolve("application/vnd.barewire+json");

        result.Should().BeSameAs(_envelopeDeserializer);
    }

    [Fact]
    public void Resolve_UnknownContentType_ReturnsDefaultDeserializer()
    {
        var sut = new ContentTypeDeserializerRouter(_rawDeserializer);

        var result = sut.Resolve("text/plain");

        result.Should().BeSameAs(_rawDeserializer);
    }

    [Fact]
    public void Resolve_NullContentType_ReturnsDefaultDeserializer()
    {
        var sut = new ContentTypeDeserializerRouter(_rawDeserializer);

        var result = sut.Resolve(null);

        result.Should().BeSameAs(_rawDeserializer);
    }

    [Fact]
    public void Resolve_CustomRegistered_ReturnsCustomDeserializer()
    {
        const string customContentType = "application/x-custom";
        var custom = Substitute.For<IMessageDeserializer>();
        custom.ContentType.Returns(customContentType);

        var sut = new ContentTypeDeserializerRouter(_rawDeserializer, [custom]);

        var result = sut.Resolve(customContentType);

        result.Should().BeSameAs(custom);
    }

    [Fact]
    public void Resolve_CaseInsensitive_ReturnsMatch()
    {
        var sut = new ContentTypeDeserializerRouter(
            _rawDeserializer,
            [_envelopeDeserializer]);

        var result = sut.Resolve("APPLICATION/JSON");

        result.Should().BeSameAs(_rawDeserializer);
    }

    [Fact]
    public void Resolve_VndMasstransitJson_ReturnsMassTransitDeserializer()
    {
        var sut = new ContentTypeDeserializerRouter(
            _rawDeserializer,
            [_envelopeDeserializer, _mtDeserializer]);

        var result = sut.Resolve("application/vnd.masstransit+json");

        result.Should().BeSameAs(_mtDeserializer);
    }
}
