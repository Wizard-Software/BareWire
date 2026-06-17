using Azure.Identity;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.AzureServiceBus;
using BareWire.Transport.AzureServiceBus.Configuration;
using Xunit;

namespace BareWire.IntegrationTests.Transport;

/// <summary>
/// Tests covering Azure Service Bus authentication configuration. Contains:
/// <list type="bullet">
///   <item>
///     A broker-free structural test for Entra ID configuration (<em>always runs</em> — no
///     skip, no <c>[Trait("Category", "AzureServiceBus")]</c>).
///   </item>
///   <item>
///     A broker-gated SAS connectivity test (skipped when
///     <c>BAREWIRE_ASB_CONNECTION_STRING</c> is absent).
///   </item>
/// </list>
/// </summary>
public sealed class AzureServiceBusAuthTests
{
    // ── Entra ID — broker-free structural test ────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="IAzureServiceBusConfigurator.UseEntraIdAuth"/> produces
    /// options with <see cref="AzureServiceBusAuthMode.EntraId"/> and a non-null
    /// <see cref="AzureServiceBusTransportOptions.Credential"/>, and that <c>Build()</c>
    /// does not throw.
    /// </summary>
    /// <remarks>
    /// This test is intentionally broker-free and is NOT tagged with
    /// <c>Category=AzureServiceBus</c>. It must run in every CI environment, including those
    /// without a live Azure Service Bus namespace.
    /// </remarks>
    [Fact]
    public void EntraIdOptions_BuiltViaConfigurator_ValidatesAndSelectsEntraIdMode()
    {
        // Arrange — DefaultAzureCredential is a concrete TokenCredential; constructing it does
        // not attempt any network call (credential resolution is deferred to the first auth call).
        var cfg = new AzureServiceBusConfigurator();
        cfg.UseEntraIdAuth("myns.servicebus.windows.net", new DefaultAzureCredential());

        // Act — Build() must not throw for a valid Entra ID configuration.
        AzureServiceBusTransportOptions options = cfg.Build();

        // Assert
        options.AuthMode.Should().Be(
            AzureServiceBusAuthMode.EntraId,
            because: "UseEntraIdAuth must select the EntraId auth mode");

        options.Credential.Should().NotBeNull(
            because: "the TokenCredential supplied to UseEntraIdAuth must be propagated to options");
    }

    // ── Environment helper — broker-free gating test ──────────────────────────

    /// <summary>
    /// Verifies that <see cref="AzureServiceBusTestEnvironment.IsAvailable"/> reports
    /// <see langword="false"/> when <c>BAREWIRE_ASB_CONNECTION_STRING</c> is unset, proving the
    /// graceful-skip gate fires correctly in a CI environment without a broker secret.
    /// </summary>
    /// <remarks>
    /// Broker-free and NOT tagged <c>Category=AzureServiceBus</c> — must run everywhere. The
    /// process-scoped env var is temporarily cleared and restored in a <c>finally</c> so the test
    /// never leaks state into sibling tests. Asserts only the boolean gate; never reads or echoes
    /// the secret value (SEC-1).
    /// </remarks>
    [Fact]
    public void EnvironmentHelper_WhenConnStringUnset_IsAvailableFalse()
    {
        // Arrange — capture and clear the process-scoped variable.
        string? original = Environment.GetEnvironmentVariable(
            AzureServiceBusTestEnvironment.ConnectionStringEnvVar);

        try
        {
            Environment.SetEnvironmentVariable(
                AzureServiceBusTestEnvironment.ConnectionStringEnvVar, null);

            // Act + Assert — the gate must report the broker as unavailable.
            AzureServiceBusTestEnvironment.IsAvailable.Should().BeFalse(
                because: "with the connection-string env var unset, the broker is unavailable and "
                         + "broker-gated tests must skip rather than run");
        }
        finally
        {
            // Restore the original value so sibling tests observe the real environment.
            Environment.SetEnvironmentVariable(
                AzureServiceBusTestEnvironment.ConnectionStringEnvVar, original);
        }
    }

    // ── SAS — broker-gated connectivity test ─────────────────────────────────

    /// <summary>
    /// Verifies that a SAS-authenticated adapter can publish a single message without
    /// throwing. Skipped when <c>BAREWIRE_ASB_CONNECTION_STRING</c> is absent.
    /// </summary>
    [Fact]
    [Trait("Category", "AzureServiceBus")]
    public async Task SasOptions_BuiltViaConfigurator_ValidatesAndConnects()
    {
        AzureServiceBusTestEnvironment.SkipIfUnavailable();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string suffix = Guid.NewGuid().ToString("N");
        string queueName = $"e2e-asb-sas-auth-{suffix}";

        await using AzureServiceBusTransportAdapter adapter =
            AzureServiceBusTestEnvironment.CreateSasAdapter();

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
                because: "a SAS-authenticated adapter must successfully publish to the broker");
        }
        finally
        {
            await AzureServiceBusTestEnvironment.TryDeleteQueueAsync(
                queueName, CancellationToken.None);
        }
    }
}
