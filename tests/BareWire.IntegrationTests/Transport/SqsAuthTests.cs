// [modern-csharp loaded] — applied net10.0 / C# 14 conventions
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.AWS.SQS;
using BareWire.Transport.AWS.SQS.Configuration;
using Xunit;

namespace BareWire.IntegrationTests.Transport;

/// <summary>
/// Tests covering Amazon SQS authentication configuration. Contains:
/// <list type="bullet">
///   <item>
///     A broker-free structural test for explicit-credentials configuration
///     (<em>always runs</em> — no skip, no <c>[Trait("Category", "AwsSqs")]</c>).
///   </item>
///   <item>
///     A broker-free gating test verifying the environment helper fires correctly
///     (<em>always runs</em> — no skip, no <c>[Trait("Category", "AwsSqs")]</c>).
///   </item>
///   <item>
///     A broker-gated connectivity test (skipped when
///     <c>BAREWIRE_SQS_SERVICE_URL</c> is absent).
///   </item>
/// </list>
/// </summary>
public sealed class SqsAuthTests
{
    // ── Explicit credentials — broker-free structural test ────────────────────

    /// <summary>
    /// Verifies that <see cref="ISqsConfigurator.UseExplicitCredentials"/> produces options
    /// with <see cref="SqsAuthMode.Explicit"/> and a non-empty
    /// <see cref="SqsTransportOptions.AccessKeyId"/>, and that <c>Build()</c> does not throw.
    /// </summary>
    /// <remarks>
    /// This test is intentionally broker-free and is NOT tagged with
    /// <c>Category=AwsSqs</c>. It must run in every CI environment, including those
    /// without a live SQS endpoint or LocalStack.
    /// </remarks>
    [Fact]
    public void ExplicitCredentialsOptions_BuiltViaConfigurator_Validates()
    {
        // Arrange — use a well-formed HTTPS endpoint so Validate() does not require
        // AllowInsecureEndpoint (the default TLS guard applies in production; this test
        // exercises the production-safe path, not the LocalStack opt-out).
        var cfg = new SqsConfigurator();
        cfg.UseExplicitCredentials("AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY");
        cfg.Region("us-east-1");

        // Act — Build() must not throw for a valid explicit-credentials configuration.
        SqsTransportOptions options = cfg.Build();

        // Assert
        options.AuthMode.Should().Be(
            SqsAuthMode.Explicit,
            because: "UseExplicitCredentials must select the Explicit auth mode");

        options.AccessKeyId.Should().NotBeNullOrEmpty(
            because: "the Access Key ID supplied to UseExplicitCredentials must be propagated to options");
    }

    // ── Environment helper — broker-free gating test ──────────────────────────

    /// <summary>
    /// Verifies that <see cref="SqsTestEnvironment.IsAvailable"/> reports
    /// <see langword="false"/> when <c>BAREWIRE_SQS_SERVICE_URL</c> is unset, proving the
    /// graceful-skip gate fires correctly in a CI environment without an SQS endpoint.
    /// </summary>
    /// <remarks>
    /// Broker-free and NOT tagged <c>Category=AwsSqs</c> — must run everywhere. The
    /// process-scoped env var is temporarily cleared and restored in a <c>finally</c> so the test
    /// never leaks state into sibling tests. Asserts only the boolean gate; never reads or echoes
    /// the endpoint value (SEC-1).
    /// </remarks>
    [Fact]
    public void EnvironmentHelper_WhenServiceUrlUnset_IsAvailableFalse()
    {
        // Arrange — capture and clear the process-scoped variable.
        string? original = Environment.GetEnvironmentVariable(
            SqsTestEnvironment.ServiceUrlEnvVar);

        try
        {
            Environment.SetEnvironmentVariable(
                SqsTestEnvironment.ServiceUrlEnvVar, null);

            // Act + Assert — the gate must report the broker as unavailable.
            SqsTestEnvironment.IsAvailable.Should().BeFalse(
                because: "with the service-URL env var unset, the SQS endpoint is unavailable and "
                         + "broker-gated tests must skip rather than run");
        }
        finally
        {
            // Restore the original value so sibling tests observe the real environment.
            Environment.SetEnvironmentVariable(
                SqsTestEnvironment.ServiceUrlEnvVar, original);
        }
    }

    // ── Explicit credentials — broker-gated connectivity test ─────────────────

    /// <summary>
    /// Verifies that an adapter built with explicit credentials can publish a single message
    /// without throwing. Skipped when <c>BAREWIRE_SQS_SERVICE_URL</c> is absent.
    /// </summary>
    [Fact]
    [Trait("Category", "AwsSqs")]
    public async Task ExplicitCredentials_BuiltViaConfigurator_ValidatesAndConnects()
    {
        SqsTestEnvironment.SkipIfUnavailable();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string suffix = Guid.NewGuid().ToString("N");
        string queueName = $"e2e-sqs-auth-{suffix}";

        await using SqsTransportAdapter adapter = SqsTestEnvironment.CreateAdapter();

        try
        {
            var declaration = new TopologyDeclaration
            {
                Queues = [new QueueDeclaration(Name: queueName, Durable: true)],
            };
            await adapter.DeployTopologyAsync(declaration, cts.Token);

            OutboundMessage outbound = new(
                routingKey: queueName,
                headers: new Dictionary<string, string>(),
                body: System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { ping = true }),
                contentType: "application/json");

            // Assert — publishing must not throw; the broker accepted the message.
            IReadOnlyList<SendResult> results =
                await adapter.SendBatchAsync([outbound], cts.Token);

            results.Should().HaveCount(1);
            results[0].IsConfirmed.Should().BeTrue(
                because: "an explicit-credentials adapter must successfully publish to the broker");
        }
        finally
        {
            await SqsTestEnvironment.TryDeleteQueueAsync(queueName, CancellationToken.None);
        }
    }
}
