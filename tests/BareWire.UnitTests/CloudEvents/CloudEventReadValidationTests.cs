using AwesomeAssertions;

using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.CloudEvents;
using BareWire.Testing;

namespace BareWire.UnitTests.CloudEvents;

/// <summary>
/// Tests for the validated read path introduced in 13.6:
/// <see cref="CloudEventContextExtensions.GetCloudEventOrThrow"/> (throwing variant) and
/// regression tests for the existing <see cref="CloudEventContextExtensions.GetCloudEvent"/>
/// (SEC-1: return-null-never-throw contract).
/// </summary>
public sealed class CloudEventReadValidationTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ConsumeContext<object> BuildContext(Dictionary<string, string> headers)
        => MessageContextBuilder.Create()
            .WithHeaders(headers)
            .Build(new object());

    private static Dictionary<string, string> ValidMandatoryHeaders() => new()
    {
        ["ce-id"] = "1",
        ["ce-source"] = "https://example.com/svc",
        ["ce-type"] = "com.example.test",
        ["ce-specversion"] = "1.0",
    };

    // -------------------------------------------------------------------------
    // GetCloudEventOrThrow — throwing path (validation on read, closes 13.3 gap)
    // -------------------------------------------------------------------------

    [Fact]
    public void GetCloudEventOrThrow_WhenAllMandatoryValidAndSpecVersion10_ReturnsAttributes()
    {
        ConsumeContext<object> ctx = BuildContext(ValidMandatoryHeaders());

        ICloudEventAttributes result = ctx.GetCloudEventOrThrow();

        result.Should().NotBeNull();
        result.Id.Should().Be("1");
        result.SpecVersion.Should().Be("1.0");
    }

    [Fact]
    public void GetCloudEventOrThrow_WhenSpecVersionUnsupported_ThrowsBareWireSerializationException()
    {
        // FAIL-ABLE TDD test: mandatory headers all present but specversion != "1.0".
        // Before GetCloudEventOrThrow existed, TryFromHeaders accepted any non-empty specversion
        // string — this test closes the zero-call-site gap in CloudEventAttributeValidator (13.3).
        var headers = new Dictionary<string, string>
        {
            ["ce-id"] = "1",
            ["ce-source"] = "https://example.com/svc",
            ["ce-type"] = "com.example.test",
            ["ce-specversion"] = "99.0",
        };
        ConsumeContext<object> ctx = BuildContext(headers);

        Action act = () => ctx.GetCloudEventOrThrow();

        act.Should().Throw<BareWireSerializationException>();
    }

    [Fact]
    public void GetCloudEventOrThrow_WhenMandatoryHeaderMissing_ThrowsBareWireSerializationException()
    {
        // Omit ce-type — TryFromHeaders returns false → GetCloudEventOrThrow must throw.
        var headers = new Dictionary<string, string>
        {
            ["ce-id"] = "1",
            ["ce-source"] = "https://example.com/svc",
            ["ce-specversion"] = "1.0",
            // ce-type intentionally absent
        };
        ConsumeContext<object> ctx = BuildContext(headers);

        Action act = () => ctx.GetCloudEventOrThrow();

        act.Should().Throw<BareWireSerializationException>();
    }

    // -------------------------------------------------------------------------
    // GetCloudEventOrThrow — null-guard
    // -------------------------------------------------------------------------

    [Fact]
    public void GetCloudEventOrThrow_WhenContextNull_ThrowsArgumentNullException()
    {
        ConsumeContext context = null!;

        Action act = () => context.GetCloudEventOrThrow();

        act.Should().Throw<ArgumentNullException>();
    }

    // -------------------------------------------------------------------------
    // Regression (SEC-1): GetCloudEvent() must NEVER throw — contract preserved
    // -------------------------------------------------------------------------

    [Fact]
    public void GetCloudEvent_WhenSpecVersionUnsupported_ReturnsAttributesWithoutThrowing()
    {
        // REGRESSION / SEC-1 watch-item: GetCloudEvent() "return null, never throw" contract
        // must remain intact even after GetCloudEventOrThrow() was introduced. A message with
        // all mandatory headers present but ce-specversion = "99.0" must be returned as-is
        // (no validation, no throw) — specversion validation is opt-in via GetCloudEventOrThrow().
        var headers = new Dictionary<string, string>
        {
            ["ce-id"] = "1",
            ["ce-source"] = "https://example.com/svc",
            ["ce-type"] = "com.example.test",
            ["ce-specversion"] = "99.0",
        };
        ConsumeContext<object> ctx = BuildContext(headers);

        ICloudEventAttributes? result = null;
        Action act = () => result = ctx.GetCloudEvent();

        act.Should().NotThrow();
        result.Should().NotBeNull();
        result!.SpecVersion.Should().Be("99.0");
    }

    [Fact]
    public void GetCloudEvent_WhenMandatoryHeaderMissing_ReturnsNull()
    {
        // REGRESSION / SEC-1: missing mandatory header → null, not an exception.
        var headers = new Dictionary<string, string>
        {
            ["ce-id"] = "1",
            ["ce-source"] = "https://example.com/svc",
            // ce-type and ce-specversion intentionally absent
        };
        ConsumeContext<object> ctx = BuildContext(headers);

        ICloudEventAttributes? result = ctx.GetCloudEvent();

        result.Should().BeNull();
    }
}
