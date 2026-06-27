using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BareWire.E2ETests;

public sealed class SamplesAppFixture : IAsyncLifetime
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);
    private DistributedApplication? _app;
    private string? _rabbitMqConnectionString;

    public async ValueTask InitializeAsync()
    {
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.BareWire_Samples_AppHost>();
        _app = await builder.BuildAsync();

        var notifier = _app.Services.GetRequiredService<ResourceNotificationService>();
        await _app.StartAsync();

        using var cts = new CancellationTokenSource(StartupTimeout);

        // Wait for infrastructure to be healthy.
        await notifier.WaitForResourceHealthyAsync("rabbitmq", cts.Token);
        await notifier.WaitForResourceHealthyAsync("barewiredb", cts.Token);

        _rabbitMqConnectionString = await _app.GetConnectionStringAsync("rabbitmq", cts.Token);

        // Wait for each sample project to be running.
        string[] sampleResources =
        [
            "basic-publish-consume",
            "request-response",
            "raw-message-interop",
            "saga-order-flow",
            "transactional-outbox",
            "retry-and-dlq",
            "backpressure-demo",
            "observability-showcase",
            "multi-consumer-partitioning",
            "inbox-deduplication",
            "masstransit-interop",
            "cloudevents-interop",
            // GAP-1: "ordered-consumers" is the first WithReplicas(2) resource in this repo.
            // Aspire names replicas "ordered-consumers-0" / "ordered-consumers-1" internally,
            // but WaitForResourceAsync and CreateHttpClient resolve the logical resource name
            // "ordered-consumers" to the replica set — verified at runtime against Aspire 13.1.3
            // by the R8.17 smoke-run (no fallback to a per-replica name was needed).
            "ordered-consumers",
            // "competing-responders" is a WithReplicas(2) resource (same Aspire logical-name
            // resolution as "ordered-consumers" above).
            "competing-responders",
            // Consumer routing keys sample: one shared queue, three consumers, most-specific-wins
            // dispatch, and type-less interop via AcceptUntyped().
            "consumer-routing-keys",
        ];

        foreach (string resource in sampleResources)
        {
            await notifier.WaitForResourceAsync(resource, KnownResourceStates.Running, cts.Token);
        }
    }

    public HttpClient CreateHttpClient(string resourceName)
    {
        return _app?.CreateHttpClient(resourceName)
            ?? throw new InvalidOperationException($"App not started, cannot create client for '{resourceName}'");
    }

    public string GetRabbitMqConnectionString()
    {
        return _rabbitMqConnectionString
            ?? throw new InvalidOperationException("RabbitMQ connection string not available — fixture not initialized.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
