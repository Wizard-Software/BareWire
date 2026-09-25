using System.Reflection;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Transport;
using BareWire.Testing;
using Xunit;

namespace BareWire.UnitTests.Testing;

public sealed class BareWireTestHarnessCompositionTests
{
    private sealed record Unmapped(string Id);

    [Fact]
    public async Task DisposeAsync_DrainsTransportThroughDecorator()
    {
        BareWireTestHarness harness = await BareWireTestHarness.CreateAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        ObservingTransportAdapter adapter = harness.Adapter;
        adapter.DrainCallCount.Should().Be(0);

        await harness.DisposeAsync();

        // BareWireBusControl.StopAsync calls DrainAsync at least twice during its quiescence check
        // (an initial wait, then a re-check once the publish loop is observed idle) — the exact count
        // is an implementation detail of the core, not of this decorator.
        adapter.DrainCallCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task CreateAsync_TwoHarnesses_DoNotShareQueues()
    {
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await using BareWireTestHarness first = await BareWireTestHarness.CreateAsync(
            null, null, null, t => t.ConfigureTopology(topo => topo.DeclareQueue("isolation-q")), cts.Token);
        await using BareWireTestHarness second = await BareWireTestHarness.CreateAsync(
            null, null, null, t => t.ConfigureTopology(topo => topo.DeclareQueue("isolation-q")), cts.Token);

        IReadOnlyList<SendResult> sent = await first.Adapter.SendBatchAsync(
            [new OutboundMessage("isolation-q", new Dictionary<string, string>(), "{}"u8.ToArray(), "application/json")],
            cts.Token);
        sent[0].IsConfirmed.Should().BeTrue();

        // Positive control: the message is visible in the first harness' own queue.
        InboundMessage? received = await FirstOrDefaultAsync(
            first.Adapter.ConsumeAsync("isolation-q", new FlowControlOptions(), cts.Token), cts.Token);
        received.Should().NotBeNull();
        await first.Adapter.SettleAsync(SettlementAction.Ack, received!, cts.Token);

        // The same queue name in the second, independent harness stays empty.
        using CancellationTokenSource shortCts = new(TimeSpan.FromMilliseconds(300));
        InboundMessage? leaked = await FirstOrDefaultAsync(
            second.Adapter.ConsumeAsync("isolation-q", new FlowControlOptions(), shortCts.Token), shortCts.Token);
        leaked.Should().BeNull();
    }

    [Fact]
    public async Task PublishAsync_WithoutQueueForRoutingKey_IsObservedByDecorator()
    {
        await using BareWireTestHarness harness = await BareWireTestHarness.CreateAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        // PublishAsync only enqueues into the bounded outgoing channel; the transport send happens on the
        // bus's publish loop, so the observation is awaited instead of asserted right after PublishAsync.
        TaskCompletionSource<OutboundMessage> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Adapter.MessageSent += message => observed.TrySetResult(message);

        await harness.Bus.PublishAsync(new Unmapped("u-1"), TestContext.Current.CancellationToken);

        OutboundMessage sent = await observed.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        sent.RoutingKey.Should().Be(typeof(Unmapped).FullName);
    }

    [Fact]
    public void TestingAssembly_DoesNotContainLegacyStub()
        => typeof(BareWireTestHarness).Assembly.GetType("BareWire.Testing.InMemoryTransportAdapter").Should().BeNull();

    [Fact]
    public void ObservingTransportAdapter_AndInMemoryConfiguratorOverload_AreNotPublic()
    {
        // BareWire.Testing has no approved.txt snapshot guarding its public API surface — these two
        // assertions are the only guard against accidentally widening it.
        typeof(ObservingTransportAdapter).IsPublic.Should().BeFalse();

        MethodInfo[] createAsyncMethods = typeof(BareWireTestHarness)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == nameof(BareWireTestHarness.CreateAsync))
            .ToArray();

        createAsyncMethods.Should().NotContain(
            m => m.GetParameters().Any(p => p.ParameterType == typeof(Action<IInMemoryConfigurator>)));
    }

    [Fact]
    public void HasInMemoryTransport_IsNoOp()
    {
        BareWire.Configuration.BusConfigurator configurator = new() { HasInMemoryTransport = true };

        configurator.HasInMemoryTransport.Should().BeFalse();
        configurator.HasTransport.Should().BeFalse();
    }

    private static async Task<InboundMessage?> FirstOrDefaultAsync(
        IAsyncEnumerable<InboundMessage> source, CancellationToken shortToken)
    {
        try
        {
            await foreach (InboundMessage message in source)
                return message;

            return null;
        }
        catch (OperationCanceledException) when (shortToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
