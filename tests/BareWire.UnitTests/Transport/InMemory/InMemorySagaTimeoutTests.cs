using System.Globalization;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Saga.Scheduling;
using BareWire.Serialization.Json;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Configuration;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BareWire.UnitTests.Transport.InMemory;

/// <summary>
/// Covers subtask 20.25's variant (a) decision end to end: <c>BareWire.Saga</c>'s
/// <see cref="SchedulingStrategy.Auto"/> selects <c>TransportNativeScheduleProvider</c> for the in-memory
/// transport — never <c>DelayRequeueScheduleProvider</c>, which would call
/// <see cref="InMemoryTransportAdapter.DeployTopologyAsync"/> at runtime and throw against the sealed
/// in-memory topology — and a message scheduled through that provider is delivered by
/// <see cref="InMemoryMessageScheduler"/> without ever deploying topology.
/// </summary>
/// <remarks>
/// Timeout CANCELLATION through the saga ENGINE is a separate, unresolved limitation, out of this
/// subtask's write scope: <c>SagaMessageDispatcher</c> builds a fresh <c>IScheduleProvider</c> per
/// dispatched event, so the <c>correlationId → token</c> map a single <c>TransportNativeScheduleProvider</c>
/// instance keeps is empty by the time a later event tries to cancel — the cancel call is then a no-op and
/// a timeout the saga engine believes it cancelled is still delivered. <see cref="NativeProvider_CancelBeforeDue_IsNotDelivered"/>
/// below exercises cancellation at the PROVIDER level only (one provider instance handling both calls) and
/// is named accordingly — it is not evidence that saga-engine cancellation works end to end.
/// </remarks>
public sealed class InMemorySagaTimeoutTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);

    private sealed record PaymentTimedOut(Guid CorrelationId);

    // ── Fixtures ───────────────────────────────────────────────────────────────────────────────────

    private static InMemoryTransportAdapter CreateAdapter(TimeProvider timeProvider, string queue)
    {
        var c = new InMemoryConfigurator();
        c.ConfigureTopology(t => t.DeclareQueue(queue));
        InMemoryTransportOptions o = c.Build();
        return new InMemoryTransportAdapter(o, new InMemoryBroker(o), timeProvider: timeProvider);
    }

    private static InMemoryQueue Queue(InMemoryTransportAdapter adapter, string name)
    {
        adapter.Broker.TryGetQueue(name, out InMemoryQueue? queue).Should().BeTrue();
        return queue!;
    }

    private static async Task<InboundMessage> ConsumeOneAsync(
        InMemoryTransportAdapter adapter, string queueName, TimeSpan timeout)
    {
        IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(queueName, new FlowControlOptions(), TestContext.Current.CancellationToken)
                .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        (await enumerator.MoveNextAsync().AsTask().WaitAsync(timeout, TestContext.Current.CancellationToken))
            .Should().BeTrue();
        return enumerator.Current;
    }

    // ── Tests ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ScheduleProviderFactory_AutoWithInMemoryTransport_SelectsNativeProviderNotDelayRequeue()
    {
        using InMemoryTransportAdapter adapter = CreateAdapter(TimeProvider.System, queue: "saga-endpoint");

        IScheduleProvider provider = ScheduleProviderFactory.Create(
            SchedulingStrategy.Auto, adapter, NullLoggerFactory.Instance, new SystemTextJsonSerializer());

        provider.Should().BeOfType<TransportNativeScheduleProvider>();
    }

    [Fact]
    public async Task SagaTimeout_ScheduledThroughNativeProvider_IsDeliveredByInMemorySchedulerWithoutTopologyDeploy()
    {
        var fake = new FakeTimeProvider(Epoch);
        using InMemoryTransportAdapter adapter = CreateAdapter(fake, queue: "saga-endpoint");
        var provider = new TransportNativeScheduleProvider(
            adapter, new SystemTextJsonSerializer(), NullLogger<TransportNativeScheduleProvider>.Instance, fake);
        Guid correlationId = Guid.NewGuid();

        await provider.ScheduleAsync(
            new PaymentTimedOut(correlationId), TimeSpan.FromMinutes(30), "saga-endpoint", correlationId,
            TestContext.Current.CancellationToken);
        adapter.PendingScheduledCount.Should().Be(1);

        fake.Advance(TimeSpan.FromMinutes(30));

        InboundMessage delivered = await ConsumeOneAsync(adapter, "saga-endpoint", TimeSpan.FromSeconds(5));
        delivered.Headers["BW-MessageType"].Should().Be(nameof(PaymentTimedOut));
        delivered.Headers["correlation-id"].Should().Be(correlationId.ToString());
        adapter.PendingScheduledCount.Should().Be(0);
    }

    /// <summary>
    /// PROVIDER-level cancellation test — see this class's own remarks. One
    /// <see cref="TransportNativeScheduleProvider"/> instance drives both calls, so its token map is
    /// populated when <c>CancelAsync</c> runs; the saga engine itself does not guarantee this.
    /// </summary>
    [Fact]
    public async Task NativeProvider_CancelBeforeDue_IsNotDelivered()
    {
        var fake = new FakeTimeProvider(Epoch);
        using InMemoryTransportAdapter adapter = CreateAdapter(fake, queue: "saga-endpoint");
        var provider = new TransportNativeScheduleProvider(
            adapter, new SystemTextJsonSerializer(), NullLogger<TransportNativeScheduleProvider>.Instance, fake);
        Guid correlationId = Guid.NewGuid();

        await provider.ScheduleAsync(
            new PaymentTimedOut(correlationId), TimeSpan.FromMinutes(30), "saga-endpoint", correlationId,
            TestContext.Current.CancellationToken);
        await provider.CancelAsync<PaymentTimedOut>(correlationId, TestContext.Current.CancellationToken);

        fake.Advance(TimeSpan.FromMinutes(30));

        adapter.PendingScheduledCount.Should().Be(0);
        Queue(adapter, "saga-endpoint").Occupancy.Should().Be(0);
    }
}
