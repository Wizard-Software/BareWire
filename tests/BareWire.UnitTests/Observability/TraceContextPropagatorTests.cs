using System.Diagnostics;
using AwesomeAssertions;
using BareWire.Abstractions.Observability;
using BareWire.Observability;
using Microsoft.Extensions.DependencyInjection;

namespace BareWire.UnitTests.Observability;

/// <summary>
/// Verifies inbound trace-context extraction, including the MassTransit <c>MT-Activity-Id</c> fallback.
/// </summary>
[Collection(ActivityListenerFixtureGroup.Name)]
public sealed class TraceContextPropagatorTests : IDisposable
{
    private const string MtTraceId = "0af7651916cd43dd8448eb211c80319c";
    private const string MtSpanId = "b7ad6b7169203331";
    private const string MtValue = "00-" + MtTraceId + "-" + MtSpanId + "-01";

    private const string W3cTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private const string W3cSpanId = "00f067aa0ba902b7";
    private const string W3cValue = "00-" + W3cTraceId + "-" + W3cSpanId + "-01";

    private readonly ActivityListener _listener;
    private readonly ServiceProvider _provider;
    private readonly IBareWireInstrumentation _instrumentation;

    public TraceContextPropagatorTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "BareWire",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(_listener);

        ServiceCollection services = new();
        services.AddSingleton<System.Diagnostics.Metrics.IMeterFactory, TestMeterFactory>();
        services.AddBareWireObservability(static o => o.EnableOpenTelemetry = false);
        _provider = services.BuildServiceProvider();
        _instrumentation = _provider.GetRequiredService<IBareWireInstrumentation>();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _provider.Dispose();
    }

    [Fact]
    public void StartConsumeActivity_WithOnlyMtActivityId_UsesItAsParent()
    {
        using Activity? activity = _instrumentation.StartConsumeActivity(
            "T", "ep", Guid.NewGuid(), new Dictionary<string, string> { ["MT-Activity-Id"] = MtValue });

        activity.Should().NotBeNull();
        activity!.TraceId.ToString().Should().Be(MtTraceId);
        activity.ParentSpanId.ToString().Should().Be(MtSpanId);
    }

    [Fact]
    public void StartConsumeActivity_WithBothHeaders_PrefersTraceparent()
    {
        using Activity? activity = _instrumentation.StartConsumeActivity(
            "T",
            "ep",
            Guid.NewGuid(),
            new Dictionary<string, string> { ["traceparent"] = W3cValue, ["MT-Activity-Id"] = MtValue });

        activity.Should().NotBeNull();
        activity!.TraceId.ToString().Should().Be(W3cTraceId);
        activity.ParentSpanId.ToString().Should().Be(W3cSpanId);
    }

    [Fact]
    public void StartConsumeActivity_WithInvalidMtActivityId_StartsNewRootWithoutThrowing()
    {
        using Activity? activity = _instrumentation.StartConsumeActivity(
            "T", "ep", Guid.NewGuid(), new Dictionary<string, string> { ["MT-Activity-Id"] = "garbage" });

        activity.Should().NotBeNull();
        activity!.ParentSpanId.Should().Be(default(ActivitySpanId));
    }

    [Fact]
    public void StartConsumeActivity_WithInvalidTraceparentAndValidMtActivityId_StartsNewRootWithoutFallback()
    {
        using Activity? activity = _instrumentation.StartConsumeActivity(
            "T",
            "ep",
            Guid.NewGuid(),
            new Dictionary<string, string> { ["traceparent"] = "garbage", ["MT-Activity-Id"] = MtValue });

        activity.Should().NotBeNull();
        activity!.ParentSpanId.Should().Be(default(ActivitySpanId));
        activity.TraceId.ToString().Should().NotBe(MtTraceId);
    }

    [Fact]
    public void ExtractTraceContext_WithOnlyMtActivityId_UsesItAsParent()
    {
        using Activity? activity = _instrumentation.ExtractTraceContext(
            new Dictionary<string, string> { ["MT-Activity-Id"] = MtValue }, "op");

        activity.Should().NotBeNull();
        activity!.TraceId.ToString().Should().Be(MtTraceId);
        activity.ParentSpanId.ToString().Should().Be(MtSpanId);
    }
}
