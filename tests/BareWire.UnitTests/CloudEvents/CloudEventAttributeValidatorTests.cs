using AwesomeAssertions;

using BareWire.Abstractions.Exceptions;
using BareWire.CloudEvents;

namespace BareWire.UnitTests.CloudEvents;

public sealed class CloudEventAttributeValidatorTests
{
    private const string ContentType = "application/cloudevents+json";

    private static StubAttributes ValidAttributes() => new()
    {
        Id = "evt-1",
        Source = new Uri("https://example/source"),
        Type = "com.example.test",
        SpecVersion = "1.0",
    };

    [Fact]
    public void ValidateMandatory_WhenAllAttributesValid_DoesNotThrow()
    {
        ICloudEventAttributes attributes = ValidAttributes();

        Action act = () => CloudEventAttributeValidator.ValidateMandatory(attributes, ContentType);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateMandatory_WhenIdMissingOrEmpty_ThrowsSerializationException(string? id)
    {
        var attributes = ValidAttributes();
        attributes.Id = id!;

        Action act = () => CloudEventAttributeValidator.ValidateMandatory(attributes, ContentType);

        act.Should().Throw<BareWireSerializationException>()
            .Which.Message.Should().Contain("id");
    }

    [Fact]
    public void ValidateMandatory_WhenSourceMissing_ThrowsSerializationException()
    {
        var attributes = ValidAttributes();
        attributes.Source = null!;

        Action act = () => CloudEventAttributeValidator.ValidateMandatory(attributes, ContentType);

        act.Should().Throw<BareWireSerializationException>()
            .Which.Message.Should().Contain("source");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateMandatory_WhenTypeMissingOrEmpty_ThrowsSerializationException(string? type)
    {
        var attributes = ValidAttributes();
        attributes.Type = type!;

        Action act = () => CloudEventAttributeValidator.ValidateMandatory(attributes, ContentType);

        act.Should().Throw<BareWireSerializationException>()
            .Which.Message.Should().Contain("type");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateMandatory_WhenSpecVersionMissingOrEmpty_ThrowsSerializationException(string? specVersion)
    {
        var attributes = ValidAttributes();
        attributes.SpecVersion = specVersion!;

        Action act = () => CloudEventAttributeValidator.ValidateMandatory(attributes, ContentType);

        act.Should().Throw<BareWireSerializationException>()
            .Which.Message.Should().Contain("specversion");
    }

    [Fact]
    public void ValidateMandatory_WhenSpecVersionUnsupported_ThrowsSerializationException()
    {
        var attributes = ValidAttributes();
        attributes.SpecVersion = "0.3";

        Action act = () => CloudEventAttributeValidator.ValidateMandatory(attributes, ContentType);

        act.Should().Throw<BareWireSerializationException>()
            .Which.Message.Should().Contain("0.3");
    }

    // SEC-1 (task-verify): an untrusted, unbounded specversion echoed into the exception message
    // must be sanitized — capped length + control/CR/LF chars stripped — to neutralize
    // log-injection and DoS amplification. BareWireSerializationException truncates only RawPayload,
    // never the Message, so the validator must sanitize before interpolation.
    [Fact]
    public void ValidateMandatory_WhenSpecVersionUntrustedAndOversized_SanitizesEchoInMessage()
    {
        var attributes = ValidAttributes();
        attributes.SpecVersion = "1.0\r\nINJECTED" + new string('x', 100);

        Action act = () => CloudEventAttributeValidator.ValidateMandatory(attributes, ContentType);

        var message = act.Should().Throw<BareWireSerializationException>().Which.Message;

        // (a) bounded length — the raw 100+ char value must not appear verbatim
        message.Should().NotContain(new string('x', 100));
        // (b) no log-injection vectors
        message.Should().NotContain("\r");
        message.Should().NotContain("\n");
    }

    /// <summary>
    /// Mutable stub of <see cref="ICloudEventAttributes"/> that permits constructing the invalid
    /// states <see cref="CloudEventContext"/>'s constructor null-guards forbid (e.g. null Source, empty Id).
    /// </summary>
    private sealed class StubAttributes : ICloudEventAttributes
    {
        public string Id { get; set; } = string.Empty;

        public Uri Source { get; set; } = null!;

        public string SpecVersion { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public string? Subject { get; set; }

        public DateTimeOffset? Time { get; set; }

        public string? DataContentType { get; set; }

        public Uri? DataSchema { get; set; }

        public IReadOnlyDictionary<string, string> Extensions { get; set; } =
            new Dictionary<string, string>(0);
    }
}
