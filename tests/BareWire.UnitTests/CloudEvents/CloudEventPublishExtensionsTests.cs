using AwesomeAssertions;

using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.CloudEvents;
using NSubstitute;

namespace BareWire.UnitTests.CloudEvents;

/// <summary>
/// Tests for <see cref="CloudEventPublishExtensions.PublishCloudEventAsync{T}"/>.
/// </summary>
public sealed class CloudEventPublishExtensionsTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private sealed record TestMessage(string Value);

    private static CloudEventContext ValidAttributes() => new(
        id: "evt-publish-1",
        source: new Uri("https://example.com/publisher"),
        type: "com.example.order.created",
        specVersion: "1.0");

    // -------------------------------------------------------------------------
    // Happy path
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PublishCloudEventAsync_WhenValidAttributes_CallsPublishAsyncWithCeHeaders()
    {
        IPublishEndpoint endpoint = Substitute.For<IPublishEndpoint>();
        endpoint
            .PublishAsync(Arg.Any<TestMessage>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var message = new TestMessage("hello");
        CloudEventContext attributes = ValidAttributes();

        await endpoint.PublishCloudEventAsync(message, attributes);

        await endpoint.Received(1).PublishAsync(
            message,
            Arg.Is<IReadOnlyDictionary<string, string>>(h =>
                h.ContainsKey("ce-id") &&
                h["ce-specversion"] == "1.0" &&
                h.ContainsKey("ce-source") &&
                h.ContainsKey("ce-type")),
            Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // Fail-fast: validation BEFORE publish
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PublishCloudEventAsync_WhenMandatoryAttributeInvalid_ThrowsBeforePublishing()
    {
        // CloudEventContext ctor null-guards mandatory fields and rejects empty/whitespace.
        // To reach the validator's specversion-check branch with a "valid" ctor but invalid
        // specversion, we use a NSubstitute stub that returns a bad specversion value.
        IPublishEndpoint endpoint = Substitute.For<IPublishEndpoint>();
        ICloudEventAttributes badAttributes = Substitute.For<ICloudEventAttributes>();
        badAttributes.Id.Returns("ok-id");
        badAttributes.Source.Returns(new Uri("https://example.com/svc"));
        badAttributes.Type.Returns("com.example.test");
        badAttributes.SpecVersion.Returns("2.0"); // unsupported — validator rejects

        var message = new TestMessage("hello");

        Func<Task> act = () => endpoint.PublishCloudEventAsync(message, badAttributes);

        await act.Should().ThrowAsync<BareWireSerializationException>();
        await endpoint.DidNotReceive().PublishAsync(
            Arg.Any<TestMessage>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // Null-guards
    // -------------------------------------------------------------------------

    [Fact]
    public async Task PublishCloudEventAsync_WhenEndpointNull_ThrowsArgumentNullException()
    {
        IPublishEndpoint endpoint = null!;
        var message = new TestMessage("x");
        CloudEventContext attributes = ValidAttributes();

        Func<Task> act = () => endpoint.PublishCloudEventAsync(message, attributes);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task PublishCloudEventAsync_WhenMessageNull_ThrowsArgumentNullException()
    {
        IPublishEndpoint endpoint = Substitute.For<IPublishEndpoint>();
        TestMessage message = null!;
        CloudEventContext attributes = ValidAttributes();

        Func<Task> act = () => endpoint.PublishCloudEventAsync(message, attributes);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task PublishCloudEventAsync_WhenAttributesNull_ThrowsArgumentNullException()
    {
        IPublishEndpoint endpoint = Substitute.For<IPublishEndpoint>();
        var message = new TestMessage("x");
        ICloudEventAttributes attributes = null!;

        Func<Task> act = () => endpoint.PublishCloudEventAsync(message, attributes);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
