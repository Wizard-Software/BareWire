// [modern-csharp loaded] — applied net10.0 / C# 14 conventions
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using BareWire.Transport.AzureServiceBus;
using BareWire.Transport.AzureServiceBus.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BareWire.IntegrationTests.Transport;

/// <summary>
/// Helper that gates Azure Service Bus integration tests behind the
/// <c>BAREWIRE_ASB_CONNECTION_STRING</c> environment variable. When the variable is absent
/// the test is skipped via <see cref="Assert.SkipUnless"/> (reports status "Skipped", never
/// silently green). Mirrors the <c>Category=TLS</c> gating pattern used for
/// <c>RabbitMqTlsTests</c>.
/// </summary>
/// <remarks>
/// SEC-1: this class never logs, interpolates, or writes the connection-string value into any
/// output. Skip messages echo only the environment-variable NAME, not its value.
/// </remarks>
internal static class AzureServiceBusTestEnvironment
{
    internal const string ConnectionStringEnvVar = "BAREWIRE_ASB_CONNECTION_STRING";

    /// <summary>
    /// Returns the raw connection string, or <see langword="null"/> when the variable is unset.
    /// </summary>
    internal static string? ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionStringEnvVar);

    /// <summary>
    /// <see langword="true"/> when a non-empty connection string is available in the environment.
    /// </summary>
    internal static bool IsAvailable => !string.IsNullOrWhiteSpace(ConnectionString);

    /// <summary>
    /// Skips the calling test when the broker connection string is not available.
    /// Must be the first statement of every broker-gated test.
    /// </summary>
    internal static void SkipIfUnavailable() =>
        Assert.SkipUnless(
            IsAvailable,
            $"Pominięto: brak zmiennej {ConnectionStringEnvVar} (brak dostępnego brokera Azure Service Bus).");

    /// <summary>
    /// Builds an <see cref="AzureServiceBusTransportAdapter"/> authenticated via SAS.
    /// An optional <paramref name="configure"/> callback can further customise the configurator
    /// (e.g. call <c>UseSessions()</c>) before <c>Build()</c> is called.
    /// </summary>
    /// <param name="configure">Optional additional configuration applied after <c>UseSasAuth</c>.</param>
    /// <returns>A configured, disposable <see cref="AzureServiceBusTransportAdapter"/>.</returns>
    internal static AzureServiceBusTransportAdapter CreateSasAdapter(
        Action<IAzureServiceBusConfigurator>? configure = null)
    {
        var cfg = new AzureServiceBusConfigurator();
        cfg.UseSasAuth(ConnectionString!);
        configure?.Invoke(cfg);
        AzureServiceBusTransportOptions options = cfg.Build();
        return new AzureServiceBusTransportAdapter(
            options,
            NullLogger<AzureServiceBusTransportAdapter>.Instance);
    }

    /// <summary>
    /// Creates a <see cref="ServiceBusAdministrationClient"/> authenticated with the SAS
    /// connection string. Used for topology teardown (queue deletion) in test cleanup.
    /// </summary>
    internal static ServiceBusAdministrationClient CreateAdminClient() =>
        new(ConnectionString!);

    /// <summary>
    /// Deletes <paramref name="queueName"/> from the broker, swallowing
    /// <see cref="ServiceBusFailureReason.MessagingEntityNotFound"/> so that teardown is safe
    /// even when the queue was never created (e.g. the test failed during setup).
    /// </summary>
    /// <param name="queueName">Name of the queue to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task TryDeleteQueueAsync(string queueName, CancellationToken ct)
    {
        try
        {
            await CreateAdminClient().DeleteQueueAsync(queueName, ct).ConfigureAwait(false);
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
        {
            // Queue did not exist — teardown is a no-op.
        }
    }
}
