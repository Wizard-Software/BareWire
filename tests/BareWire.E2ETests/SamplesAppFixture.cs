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

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
