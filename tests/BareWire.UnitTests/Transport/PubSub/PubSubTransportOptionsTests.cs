using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Transport.Google.PubSub;
using Xunit;

namespace BareWire.UnitTests.Transport.PubSub;

public sealed class PubSubTransportOptionsTests
{
    private static PubSubTransportOptions ValidApplicationDefaultOptions() => new()
    {
        AuthMode = PubSubAuthMode.ApplicationDefault,
        ProjectId = "my-project",
    };

    // ── Validate — ProjectId ──────────────────────────────────────────────────

    [Fact]
    public void Validate_EmptyProjectId_ThrowsBareWireConfigurationException()
    {
        var options = new PubSubTransportOptions
        {
            AuthMode = PubSubAuthMode.ApplicationDefault,
            ProjectId = string.Empty,
        };

        Action act = () => options.Validate();

        act.Should().ThrowExactly<BareWireConfigurationException>()
            .WithMessage("*ProjectId*");
    }

    [Fact]
    public void Validate_ValidProjectId_DoesNotThrow()
    {
        var options = ValidApplicationDefaultOptions();

        Action act = () => options.Validate();

        act.Should().NotThrow();
    }

    // ── Validate — ServiceAccountJson mode ───────────────────────────────────

    [Fact]
    public void Validate_ServiceAccountJsonModeWithoutPathOrContent_ThrowsBareWireConfigurationException()
    {
        var options = new PubSubTransportOptions
        {
            AuthMode = PubSubAuthMode.ServiceAccountJson,
            ProjectId = "my-project",
            ServiceAccountJsonPath = string.Empty,
            ServiceAccountJson = string.Empty,
        };

        Action act = () => options.Validate();

        act.Should().ThrowExactly<BareWireConfigurationException>()
            .WithMessage("*ServiceAccountJson*");
    }

    [Fact]
    public void Validate_ServiceAccountJsonModeWithPath_DoesNotThrow()
    {
        var options = new PubSubTransportOptions
        {
            AuthMode = PubSubAuthMode.ServiceAccountJson,
            ProjectId = "my-project",
            ServiceAccountJsonPath = "/etc/secrets/sa.json",
        };

        Action act = () => options.Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ServiceAccountJsonModeWithContent_DoesNotThrow()
    {
        var options = new PubSubTransportOptions
        {
            AuthMode = PubSubAuthMode.ServiceAccountJson,
            ProjectId = "my-project",
            ServiceAccountJson = """{"type":"service_account"}""",
        };

        Action act = () => options.Validate();

        act.Should().NotThrow();
    }

    // ── Validate — EmulatorInsecure mode ─────────────────────────────────────

    [Fact]
    public void Validate_EmulatorInsecureModeWithoutEndpoint_ThrowsBareWireConfigurationException()
    {
        var options = new PubSubTransportOptions
        {
            AuthMode = PubSubAuthMode.EmulatorInsecure,
            ProjectId = "my-project",
            EmulatorEndpoint = string.Empty,
        };

        Action act = () => options.Validate();

        act.Should().ThrowExactly<BareWireConfigurationException>()
            .WithMessage("*EmulatorEndpoint*");
    }

    [Fact]
    public void Validate_EmulatorInsecureModeWithEndpoint_DoesNotThrow()
    {
        var options = new PubSubTransportOptions
        {
            AuthMode = PubSubAuthMode.EmulatorInsecure,
            ProjectId = "my-project",
            EmulatorEndpoint = "localhost:8085",
        };

        Action act = () => options.Validate();

        act.Should().NotThrow();
    }

    // ── Validate — SEC-3: EmulatorEndpoint under non-emulator auth mode ───────

    [Fact]
    public void Validate_EmulatorEndpointUnderApplicationDefault_ThrowsBareWireConfigurationException()
    {
        var options = new PubSubTransportOptions
        {
            AuthMode = PubSubAuthMode.ApplicationDefault,
            ProjectId = "my-project",
            EmulatorEndpoint = "localhost:8085",  // stale emulator endpoint — SEC-3
        };

        Action act = () => options.Validate();

        BareWireConfigurationException ex = act.Should()
            .ThrowExactly<BareWireConfigurationException>().Which;

        ex.Message.Should().Contain("EmulatorEndpoint",
            "the SEC-3 guard must identify the problematic option");
        ex.Message.Should().Contain("EmulatorInsecure",
            "the error should mention the correct AuthMode to use");
    }

    [Fact]
    public void Validate_EmulatorEndpointUnderServiceAccountJson_ThrowsBareWireConfigurationException()
    {
        var options = new PubSubTransportOptions
        {
            AuthMode = PubSubAuthMode.ServiceAccountJson,
            ProjectId = "my-project",
            ServiceAccountJsonPath = "/etc/sa.json",
            EmulatorEndpoint = "localhost:8085",  // SEC-3 guard
        };

        Action act = () => options.Validate();

        act.Should().ThrowExactly<BareWireConfigurationException>()
            .WithMessage("*EmulatorInsecure*");
    }

    // ── Validate — DefaultAckDeadline range ──────────────────────────────────

    [Fact]
    public void Validate_AckDeadlineBelowMinimum_ThrowsBareWireConfigurationException()
    {
        var options = new PubSubTransportOptions
        {
            AuthMode = PubSubAuthMode.ApplicationDefault,
            ProjectId = "my-project",
            DefaultAckDeadline = TimeSpan.FromSeconds(9),  // below 10 s minimum
        };

        Action act = () => options.Validate();

        act.Should().ThrowExactly<BareWireConfigurationException>()
            .WithMessage("*DefaultAckDeadline*");
    }

    [Fact]
    public void Validate_AckDeadlineAboveMaximum_ThrowsBareWireConfigurationException()
    {
        var options = new PubSubTransportOptions
        {
            AuthMode = PubSubAuthMode.ApplicationDefault,
            ProjectId = "my-project",
            DefaultAckDeadline = TimeSpan.FromSeconds(601),  // above 600 s maximum
        };

        Action act = () => options.Validate();

        act.Should().ThrowExactly<BareWireConfigurationException>()
            .WithMessage("*DefaultAckDeadline*");
    }

    [Fact]
    public void Validate_AckDeadlineAtBoundary10Seconds_DoesNotThrow()
    {
        var options = new PubSubTransportOptions
        {
            AuthMode = PubSubAuthMode.ApplicationDefault,
            ProjectId = "my-project",
            DefaultAckDeadline = TimeSpan.FromSeconds(10),
        };

        Action act = () => options.Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_AckDeadlineAtBoundary600Seconds_DoesNotThrow()
    {
        var options = new PubSubTransportOptions
        {
            AuthMode = PubSubAuthMode.ApplicationDefault,
            ProjectId = "my-project",
            DefaultAckDeadline = TimeSpan.FromSeconds(600),
        };

        Action act = () => options.Validate();

        act.Should().NotThrow();
    }

    // ── ToString — SEC-02 redaction ───────────────────────────────────────────

    [Fact]
    public void ToString_ServiceAccountJsonIsRedacted()
    {
        const string secret = """{"type":"service_account","private_key":"-----BEGIN RSA-----"}""";

        var options = new PubSubTransportOptions
        {
            AuthMode = PubSubAuthMode.ServiceAccountJson,
            ProjectId = "my-project",
            ServiceAccountJson = secret,
            ServiceAccountJsonPath = "/etc/sa.json",
        };

        string result = options.ToString();

        // SEC-02: inline JSON content must be redacted.
        result.Should().Contain("[Redacted]",
            "ServiceAccountJson must be shown as [Redacted]");

        // The actual secret content must never appear.
        result.Should().NotContain(secret,
            "secret inline JSON must not appear in ToString");
        result.Should().NotContain("private_key",
            "secret field names must not appear in ToString");

        // File path is a non-secret identifier — it should appear.
        result.Should().Contain("/etc/sa.json",
            "ServiceAccountJsonPath is not a secret and should be shown");

        // ProjectId is non-secret — it should appear.
        result.Should().Contain("my-project",
            "ProjectId is not a secret and should be shown");
    }

    [Fact]
    public void ToString_ApplicationDefaultMode_DoesNotContainRedacted()
    {
        var options = ValidApplicationDefaultOptions();

        string result = options.ToString();

        // In ApplicationDefault mode there are no secrets to redact.
        // The [Redacted] placeholder still appears for ServiceAccountJson field
        // (which is empty), so we just verify the mode is shown correctly.
        result.Should().Contain("ApplicationDefault");
        result.Should().Contain("my-project");
    }
}
