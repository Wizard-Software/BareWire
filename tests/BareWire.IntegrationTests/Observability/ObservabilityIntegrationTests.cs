using System.Diagnostics;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Observability;
using BareWire.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace BareWire.IntegrationTests.Observability;

/// <summary>
/// Integration tests that verify BareWire observability works end-to-end with the
/// real DI container and the OpenTelemetry InMemory exporters.
/// No external broker is required — all tests exercise the instrumentation layer directly.
/// </summary>
public sealed class ObservabilityIntegrationTests : IDisposable
{
    private ServiceProvider? _provider;

    public void Dispose() => _provider?.Dispose();

    // ── Test 1: Metrics via InMemory exporter ─────────────────────────────────

    [Fact]
    public void BareWireInstrumentation_RecordPublish_EmitsOTelMetric()
    {
        // Arrange
        var exportedMetrics = new List<Metric>();
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddMetrics();

        // Register BareWireMetrics via factory so DI can wire the internal constructor.
        // InternalsVisibleTo gives this assembly access to the internal ctor.
        services.AddSingleton<BareWireMetrics>(sp =>
            new BareWireMetrics(sp.GetRequiredService<IMeterFactory>()));

        // Wire up OTel with InMemory exporter to capture measurements.
        services.AddOpenTelemetry()
            .WithMetrics(builder => builder
                .AddMeter("BareWire")
                .AddInMemoryExporter(exportedMetrics));

        _provider = services.BuildServiceProvider();

        // Warm up the OTel pipeline by resolving MeterProvider
        var meterProvider = _provider.GetRequiredService<MeterProvider>();

        var metrics = _provider.GetRequiredService<BareWireMetrics>();

        // Act
        metrics.RecordPublish("orders-queue", "OrderCreated", messageSize: 256);

        // ForceFlush pushes all pending measurements to the exporter
        meterProvider.ForceFlush();

        // Assert — InMemory exporter must have received the "barewire.messages.published" metric
        exportedMetrics.Should().NotBeEmpty();

        var published = exportedMetrics
            .FirstOrDefault(m => m.Name == "barewire.messages.published");

        published.Should().NotBeNull("barewire.messages.published counter must be exported");
    }

    // ── Test 2: Tracing via InMemory exporter ─────────────────────────────────

    [Fact]
    public void BareWireInstrumentation_StartPublish_EmitsOTelSpan()
    {
        // Arrange
        var exportedActivities = new List<Activity>();
        var services = new ServiceCollection();

        services.AddLogging();

        services.AddOpenTelemetry()
            .WithTracing(builder => builder
                .AddSource("BareWire")
                .AddInMemoryExporter(exportedActivities));

        _provider = services.BuildServiceProvider();

        // Warm up the tracer provider — required for sampling to be active
        var tracerProvider = _provider.GetRequiredService<TracerProvider>();

        // Act — start a publish span and complete it (Dispose ends the activity)
        using (var activity = BareWireActivitySource.StartPublish(
            "OrderCreated",
            "orders-exchange",
            Guid.NewGuid()))
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
        }

        // ForceFlush pushes completed spans to the InMemory exporter
        tracerProvider.ForceFlush();

        // Assert
        exportedActivities.Should().NotBeEmpty(
            "at least one span must be exported after ForceFlush");

        var publishSpan = exportedActivities
            .FirstOrDefault(a => a.DisplayName.Contains("publish"));

        publishSpan.Should().NotBeNull(
            "a publish span named '<messageType> publish' must be exported");

        publishSpan!.Kind.Should().Be(ActivityKind.Producer);
    }

    // ── Test 3: Health check — Healthy ────────────────────────────────────────

    [Fact]
    public async Task HealthCheck_WhenBusHealthy_ReportsHealthy()
    {
        // Arrange
        var busControl = Substitute.For<IBusControl>();
        busControl.CheckHealth().Returns(new BusHealthStatus(
            BusStatus.Healthy,
            "All systems operational.",
            []));

        var services = new ServiceCollection();

        services.AddLogging();
        services.AddMetrics();

        // Register IBusControl mock so BareWireHealthCheck can resolve it
        services.AddSingleton(busControl);

        // AddBareWireObservability registers BareWireHealthCheck via AddHealthChecks()
        // It also tries to register BareWireMetrics (needs IMeterFactory from AddMetrics())
        // and IBareWireInstrumentation (Replace — registers BareWireInstrumentation as singleton)
        // BareWireInstrumentation depends on BareWireMetrics, which is fine.
        services.AddBareWireObservability(cfg => cfg.EnableOpenTelemetry = false);

        _provider = services.BuildServiceProvider();

        var healthService = _provider.GetRequiredService<HealthCheckService>();

        // Act
        var report = await healthService.CheckHealthAsync(CancellationToken.None);

        // Assert
        report.Status.Should().Be(HealthStatus.Healthy);
    }

    // ── Test 4: Health check — Degraded ───────────────────────────────────────

    [Fact]
    public async Task HealthCheck_WhenBusDegraded_ReportsDegraded()
    {
        // Arrange
        var busControl = Substitute.For<IBusControl>();
        busControl.CheckHealth().Returns(new BusHealthStatus(
            BusStatus.Degraded,
            "One endpoint is slow.",
            [new EndpointHealthStatus("orders-queue", BusStatus.Degraded, "High latency")]));

        var services = new ServiceCollection();

        services.AddLogging();
        services.AddMetrics();
        services.AddSingleton(busControl);
        services.AddBareWireObservability(cfg => cfg.EnableOpenTelemetry = false);

        _provider = services.BuildServiceProvider();

        var healthService = _provider.GetRequiredService<HealthCheckService>();

        // Act
        var report = await healthService.CheckHealthAsync(CancellationToken.None);

        // Assert
        report.Status.Should().Be(HealthStatus.Degraded);
    }

    // ── Test 5: OTel disabled — no TracerProvider registered ─────────────────

    [Fact]
    public void AddBareWireObservability_WithOTelDisabled_DoesNotRegisterOTelProviders()
    {
        // Arrange
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddMetrics();

        // Disable OTel — no AddOpenTelemetry() / AddSource / AddMeter calls should happen
        services.AddBareWireObservability(cfg => cfg.EnableOpenTelemetry = false);

        _provider = services.BuildServiceProvider();

        // Act
        var tracerProvider = _provider.GetService<TracerProvider>();

        // Assert — TracerProvider must NOT be registered when OTel is disabled
        tracerProvider.Should().BeNull(
            "TracerProvider must not be registered when EnableOpenTelemetry = false");
    }
}
