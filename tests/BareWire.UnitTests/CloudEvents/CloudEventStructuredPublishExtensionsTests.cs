using System.Text.Json;

using AwesomeAssertions;

using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.CloudEvents;
using NSubstitute;

namespace BareWire.UnitTests.CloudEvents;

/// <summary>
/// Tests for <see cref="CloudEventStructuredPublishExtensions.PublishCloudEventStructuredAsync{T}"/>.
/// </summary>
public sealed class CloudEventStructuredPublishExtensionsTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private sealed record TestMessage(string Value);

    private static CloudEventContext ValidAttributes() => new(
        id: "evt-structured-1",
        source: new Uri("https://example.com/publisher"),
        type: "com.example.order.created",
        specVersion: "1.0");

    // -------------------------------------------------------------------------
    // Happy path — publishes with envelope content type and valid payload
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PublishCloudEventStructuredAsync_WhenValidAttributes_CallsPublishRawWithEnvelopeContentTypeAndPayload()
    {
        IPublishEndpoint endpoint = Substitute.For<IPublishEndpoint>();
        byte[]? capturedPayload = null;
        string? capturedContentType = null;

        endpoint
            .PublishRawAsync(
                Arg.Do<ReadOnlyMemory<byte>>(p => capturedPayload = p.ToArray()),
                Arg.Do<string>(ct => capturedContentType = ct),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var message = new TestMessage("hello-structured");
        CloudEventContext attributes = ValidAttributes();

        await endpoint.PublishCloudEventStructuredAsync(message, attributes);

        // Verify PublishRawAsync was called exactly once with the correct content type.
        await endpoint.Received(1).PublishRawAsync(
            Arg.Any<ReadOnlyMemory<byte>>(),
            "application/cloudevents+json",
            Arg.Any<CancellationToken>());

        capturedContentType.Should().Be("application/cloudevents+json");
        capturedPayload.Should().NotBeNull().And.NotBeEmpty();

        // Parse the captured envelope and validate CloudEvents 1.0 mandatory fields + data.
        using JsonDocument doc = JsonDocument.Parse(capturedPayload!);
        JsonElement root = doc.RootElement;

        root.GetProperty("specversion").GetString().Should().Be("1.0");
        root.TryGetProperty("id", out _).Should().BeTrue();
        root.TryGetProperty("source", out _).Should().BeTrue();
        root.TryGetProperty("type", out _).Should().BeTrue();
        root.TryGetProperty("data", out _).Should().BeTrue();

        // The data property must contain the serialized TestMessage.
        root.GetProperty("data").GetProperty("value").GetString().Should().Be("hello-structured");
    }

    // -------------------------------------------------------------------------
    // Fail-fast: validation BEFORE publish
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PublishCloudEventStructuredAsync_WhenMandatoryAttributeInvalid_ThrowsBeforePublishing()
    {
        IPublishEndpoint endpoint = Substitute.For<IPublishEndpoint>();
        ICloudEventAttributes badAttributes = Substitute.For<ICloudEventAttributes>();
        badAttributes.Id.Returns("ok-id");
        badAttributes.Source.Returns(new Uri("https://example.com/svc"));
        badAttributes.Type.Returns("com.example.test");
        badAttributes.SpecVersion.Returns("2.0"); // unsupported — validator rejects
        badAttributes.Extensions.Returns(new Dictionary<string, string>(capacity: 0));

        var message = new TestMessage("hello");

        Func<Task> act = () => endpoint.PublishCloudEventStructuredAsync(message, badAttributes);

        await act.Should().ThrowAsync<BareWireSerializationException>();
        await endpoint.DidNotReceive().PublishRawAsync(
            Arg.Any<ReadOnlyMemory<byte>>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // Null-guards
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PublishCloudEventStructuredAsync_WhenEndpointNull_ThrowsArgumentNullException()
    {
        IPublishEndpoint endpoint = null!;
        var message = new TestMessage("x");
        CloudEventContext attributes = ValidAttributes();

        Func<Task> act = () => endpoint.PublishCloudEventStructuredAsync(message, attributes);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task PublishCloudEventStructuredAsync_WhenMessageNull_ThrowsArgumentNullException()
    {
        IPublishEndpoint endpoint = Substitute.For<IPublishEndpoint>();
        TestMessage message = null!;
        CloudEventContext attributes = ValidAttributes();

        Func<Task> act = () => endpoint.PublishCloudEventStructuredAsync(message, attributes);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task PublishCloudEventStructuredAsync_WhenAttributesNull_ThrowsArgumentNullException()
    {
        IPublishEndpoint endpoint = Substitute.For<IPublishEndpoint>();
        var message = new TestMessage("x");
        ICloudEventAttributes attributes = null!;

        Func<Task> act = () => endpoint.PublishCloudEventStructuredAsync(message, attributes);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
