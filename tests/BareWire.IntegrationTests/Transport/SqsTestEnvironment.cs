// [modern-csharp loaded] — applied net10.0 / C# 14 conventions
using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using BareWire.Transport.AWS.SQS;
using BareWire.Transport.AWS.SQS.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BareWire.IntegrationTests.Transport;

/// <summary>
/// Helper that gates Amazon SQS integration tests behind the
/// <c>BAREWIRE_SQS_SERVICE_URL</c> environment variable. When the variable is absent
/// the test is skipped via <see cref="Assert.SkipUnless"/> (reports status "Skipped", never
/// silently green). Mirrors the gating pattern used for <c>AzureServiceBusTestEnvironment</c>.
/// </summary>
/// <remarks>
/// SEC-1: this class never logs, interpolates, or writes the <see cref="ServiceUrl"/> value
/// into any skip message or output. Skip messages echo only the environment-variable NAME.
/// <para>
/// Note: <see cref="ServiceUrl"/> is a non-secret endpoint (not a credential), so the
/// name-only skip is stricter than strictly necessary — it is applied for consistency with
/// the Azure Service Bus gate (SEC-14 recommendation).
/// </para>
/// </remarks>
internal static class SqsTestEnvironment
{
    internal const string ServiceUrlEnvVar = "BAREWIRE_SQS_SERVICE_URL";

    /// <summary>
    /// Returns the SQS service URL, or <see langword="null"/> when the variable is unset.
    /// </summary>
    internal static string? ServiceUrl =>
        Environment.GetEnvironmentVariable(ServiceUrlEnvVar);

    /// <summary>
    /// <see langword="true"/> when a non-empty service URL is available in the environment.
    /// </summary>
    internal static bool IsAvailable => !string.IsNullOrWhiteSpace(ServiceUrl);

    /// <summary>
    /// Skips the calling test when the SQS endpoint is not configured.
    /// Must be the first statement of every broker-gated test.
    /// </summary>
    internal static void SkipIfUnavailable() =>
        Assert.SkipUnless(
            IsAvailable,
            $"Pominięto: brak zmiennej {ServiceUrlEnvVar} (brak dostępnego endpointu AWS SQS / LocalStack).");

    /// <summary>
    /// Builds an <see cref="SqsTransportAdapter"/> pointed at the LocalStack / SQS endpoint
    /// indicated by <see cref="ServiceUrl"/>. An optional <paramref name="configure"/> callback
    /// can further customise the configurator before <c>Build()</c> is called.
    /// </summary>
    /// <param name="configure">Optional additional configuration applied after the defaults.</param>
    /// <returns>A configured, disposable <see cref="SqsTransportAdapter"/>.</returns>
    /// <remarks>
    /// PERF-1: <c>WaitTimeSeconds(1)</c> is mandatory here. The consumer reads
    /// <c>_options.WaitTimeSeconds</c> at the adapter level, not per-queue. The SDK default
    /// of 20 s long-poll would cause tests to hang for up to 20 s per empty poll.
    /// </remarks>
    internal static SqsTransportAdapter CreateAdapter(Action<ISqsConfigurator>? configure = null)
    {
        var cfg = new SqsConfigurator();
        cfg.ServiceUrl(ServiceUrl!);
        cfg.AllowInsecureEndpoint();
        cfg.UseExplicitCredentials("test", "test");
        cfg.Region("us-east-1");
        cfg.WaitTimeSeconds(1); // PERF-1: short poll avoids 20-second hangs in tests.
        configure?.Invoke(cfg);
        SqsTransportOptions options = cfg.Build();
        return new SqsTransportAdapter(options, NullLogger<SqsTransportAdapter>.Instance);
    }

    /// <summary>
    /// Deletes <paramref name="queueName"/> from the broker, swallowing queue-not-found errors
    /// so that teardown is safe even when the queue was never created (e.g. the test failed
    /// during setup). SEC: never logs credential or endpoint values.
    /// </summary>
    /// <param name="queueName">Name of the queue to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task TryDeleteQueueAsync(string queueName, CancellationToken ct)
    {
        using var client = new AmazonSQSClient(
            new BasicAWSCredentials("test", "test"),
            new AmazonSQSConfig
            {
                ServiceURL = ServiceUrl,
                AuthenticationRegion = "us-east-1",
                RegionEndpoint = RegionEndpoint.USEast1,
            });

        try
        {
            GetQueueUrlResponse urlResponse = await client
                .GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = queueName }, ct)
                .ConfigureAwait(false);

            await client
                .DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = urlResponse.QueueUrl }, ct)
                .ConfigureAwait(false);
        }
        catch (QueueDoesNotExistException)
        {
            // Queue did not exist — teardown is a no-op.
        }
        catch (AmazonSQSException ex) when (
            ex.ErrorCode is "AWS.SimpleQueueService.NonExistentQueue" or "QueueDoesNotExist")
        {
            // Queue did not exist (alternate error code paths) — teardown is a no-op.
        }
    }
}
