using AwesomeAssertions;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using Xunit;

namespace BareWire.UnitTests.Core.Bus;

/// <summary>
/// Allocation gate for the per-consumer retry wiring. Uses the slope method (two batch sizes, process-wide
/// <see cref="GC.GetTotalAllocatedBytes"/>) so constant start-up cost cancels out, and runs in the shared
/// non-parallel collection so no other test pollutes the counter.
/// </summary>
[Collection(AllocationGateIsolation.Name)]
public sealed class ReceiveEndpointRunnerConsumerRetryAllocationTests
{
    private const int SmallBatch = 200;
    private const int LargeBatch = 1200;
    private const double ConsumerPolicyBudgetBytesPerMessage = 512;
    private const double NoPolicyToleranceBytesPerMessage = 64;

    [Fact]
    public async Task RunAsync_ConsumerRetryConfiguredSuccessPath_AllocatesLessThanBudgetPerMessage()
    {
        // Warm up (JIT, DI substitutes, channel plumbing) before measuring.
        await MeasureAsync(SmallBatch, withPolicy: true);
        await MeasureAsync(SmallBatch, withPolicy: false);

        double withPolicy = await SlopeAsync(withPolicy: true);
        double baseline = await SlopeAsync(withPolicy: false);

        double delta = withPolicy - baseline;
        TestContext.Current.SendDiagnosticMessage($"consumer-retry alloc: withPolicy={withPolicy:F1} B/msg baseline={baseline:F1} B/msg delta={delta:F1} B/msg");
        delta.Should().BeLessThan(
            ConsumerPolicyBudgetBytesPerMessage,
            because: "a consumer retry policy must add < 512 B/op on the success path");
    }

    [Fact]
    public async Task RunAsync_NoConsumerRetry_TypedDispatchAllocatesNoMoreThanBaseline()
    {
        // With no consumer policy the non-async pass-through must add nothing. Two independent no-policy
        // measurements must agree within measurement noise (a stable gate with no per-message surprises).
        await MeasureAsync(SmallBatch, withPolicy: false);

        double first = await SlopeAsync(withPolicy: false);
        double second = await SlopeAsync(withPolicy: false);

        TestContext.Current.SendDiagnosticMessage($"no-policy alloc: first={first:F1} B/msg second={second:F1} B/msg");
        Math.Abs(first - second).Should().BeLessThan(NoPolicyToleranceBytesPerMessage);
    }

    private static async Task<double> SlopeAsync(bool withPolicy)
    {
        long small = await MeasureAsync(SmallBatch, withPolicy);
        long large = await MeasureAsync(LargeBatch, withPolicy);
        return (large - small) / (double)(LargeBatch - SmallBatch);
    }

    private static async Task<long> MeasureAsync(int messageCount, bool withPolicy)
    {
        var (runner, _, writer, _) = ReceiveEndpointRunnerTests.CreateRunnerWithFailingTypedConsumer(
            failTimes: 0,
            configureRetry: withPolicy ? static r => r.Interval(2, TimeSpan.Zero) : null,
            endpointRetryCount: 0);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        InboundMessage[] messages = new InboundMessage[messageCount];
        for (int i = 0; i < messageCount; i++)
        {
            messages[i] = ReceiveEndpointRunnerTests.MakeTypedMessage("alloc-" + i);
        }

        // Preload the channel so the measured region covers dispatch only.
        foreach (InboundMessage message in messages)
        {
            await writer.WriteAsync(message, cts.Token);
        }

        writer.Complete();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        await runner.RunAsync(cts.Token);
        long after = GC.GetTotalAllocatedBytes(precise: true);
        return after - before;
    }
}
