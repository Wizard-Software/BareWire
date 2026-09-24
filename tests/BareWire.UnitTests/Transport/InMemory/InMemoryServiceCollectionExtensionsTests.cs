using System.Diagnostics.Metrics;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Routing;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class InMemoryServiceCollectionExtensionsTests
{
    private static ServiceProvider Build(Action<IInMemoryConfigurator>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBareWireInMemory(configure ?? (_ => { }));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddBareWireInMemory_Registered_ResolvesAdapterWithNativeSchedulingCapability()
    {
        using ServiceProvider sp = Build();
        ITransportAdapter adapter = sp.GetRequiredService<ITransportAdapter>();
        adapter.Should().BeOfType<InMemoryTransportAdapter>();
        adapter.Capabilities.Should().Be(TransportCapabilities.NativeScheduling);
        adapter.Should().BeAssignableTo<INativeMessageScheduler>();
        adapter.TransportName.Should().Be("InMemory");
    }

    [Fact]
    public void AddBareWireInMemory_SameContainer_ResolvesSingleBrokerInstance()
    {
        using ServiceProvider sp = Build();
        sp.GetRequiredService<InMemoryBroker>().Should().BeSameAs(sp.GetRequiredService<InMemoryBroker>());
        ((InMemoryTransportAdapter)sp.GetRequiredService<ITransportAdapter>()).Broker
            .Should().BeSameAs(sp.GetRequiredService<InMemoryBroker>());
    }

    [Fact]
    public void AddBareWireInMemory_TwoContainers_DoNotShareQueues()
    {
        using ServiceProvider first = Build(t => t.ConfigureTopology(topo => topo.DeclareQueue("orders")));
        using ServiceProvider second = Build(t => t.ConfigureTopology(topo => topo.DeclareQueue("orders")));
        _ = first.GetRequiredService<ITransportAdapter>();
        _ = second.GetRequiredService<ITransportAdapter>();
        InMemoryBroker a = first.GetRequiredService<InMemoryBroker>();
        InMemoryBroker b = second.GetRequiredService<InMemoryBroker>();
        a.Should().NotBeSameAs(b);

        a.TryGetQueue("orders", out InMemoryQueue? qa).Should().BeTrue();
        b.TryGetQueue("orders", out InMemoryQueue? qb).Should().BeTrue();
        qa.Should().NotBeSameAs(qb);
        qa!.TryReserve().Should().Be(QueueReservationResult.Reserved);
        qb!.Occupancy.Should().Be(0);
    }

    [Fact]
    public void AddBareWireInMemory_WithInvalidOption_ThrowsConfigurationException() =>
        new ServiceCollection().Invoking(s => s.AddBareWireInMemory(t => t.MaxMessageSize(0)))
            .Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be("MaxMessageSize");

    [Fact]
    public void AddBareWireInMemory_WithReceiveEndpoint_RegistersEndpointBinding()
    {
        using ServiceProvider sp = Build(t =>
        {
            t.ConfigureTopology(topo => topo.DeclareQueue("q"));
            t.ReceiveEndpoint("q", e => e.Consumer<TestConsumer, OrderCreated>());
        });

        IReadOnlyList<EndpointBinding> bindings = sp.GetRequiredService<IReadOnlyList<EndpointBinding>>();
        bindings.Should().ContainSingle();
        bindings[0].EndpointName.Should().Be("q");
        bindings[0].Consumers.Should().ContainSingle();

        sp.GetRequiredService<TopologyDeclaration>().Queues.Should().ContainSingle(q => q.Name == "q");
    }

    [Fact]
    public void AddBareWireInMemory_WithDeadLetterQueueArgument_MapsDeadLetterExchangeOnBinding()
    {
        using ServiceProvider sp = Build(t =>
        {
            t.ConfigureTopology(topo =>
                topo.DeclareQueue("q", true, false, q => q.DeadLetterExchange("dlx")));
            t.ReceiveEndpoint("q", e => e.Consumer<TestConsumer, OrderCreated>());
        });

        IReadOnlyList<EndpointBinding> bindings = sp.GetRequiredService<IReadOnlyList<EndpointBinding>>();
        bindings.Should().ContainSingle();
        bindings[0].DeadLetterExchange.Should().Be("dlx");
    }

    [Fact]
    public void AddBareWireInMemory_WithNullServices_ThrowsArgumentNullException()
    {
        IServiceCollection? services = null;
        Action act = () => services!.AddBareWireInMemory(_ => { });
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddBareWireInMemory_WithNullConfigure_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection();
        services.Invoking(s => s.AddBareWireInMemory(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddBareWireInMemory_WithMapExchangeAndRoutingKey_ReplacesResolvers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var defaultExchangeResolver = Substitute.For<IExchangeResolver>();
        var defaultRoutingKeyResolver = Substitute.For<IRoutingKeyResolver>();
        services.TryAddSingleton(defaultExchangeResolver);
        services.TryAddSingleton(defaultRoutingKeyResolver);

        services.AddBareWireInMemory(t =>
        {
            t.MapExchange<OrderCreated>("orders");
            t.MapRoutingKey<OrderCreated>("order.created");
        });

        using ServiceProvider sp = services.BuildServiceProvider();
        IExchangeResolver exchangeResolver = sp.GetRequiredService<IExchangeResolver>();
        IRoutingKeyResolver routingKeyResolver = sp.GetRequiredService<IRoutingKeyResolver>();

        exchangeResolver.Should().NotBeSameAs(defaultExchangeResolver);
        routingKeyResolver.Should().NotBeSameAs(defaultRoutingKeyResolver);
        exchangeResolver.Resolve<OrderCreated>().Should().Be("orders");
        routingKeyResolver.Resolve<OrderCreated>().Should().Be("order.created");
    }

    [Fact]
    public void AddBareWireInMemory_CalledTwice_KeepsFirstRoutingMappings()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBareWireInMemory(t => t.MapExchange<OrderCreated>("first"));
        services.AddBareWireInMemory(t => t.MapExchange<OrderCreated>("second"));

        using ServiceProvider sp = services.BuildServiceProvider();

        sp.GetRequiredService<IExchangeResolver>().Resolve<OrderCreated>().Should().Be("first");
        sp.GetServices<InMemoryTransportOptions>().Should().ContainSingle();
    }

    [Fact]
    public void AddBareWireInMemory_SecondCallWithInvalidOption_StillThrowsConfigurationException()
    {
        var services = new ServiceCollection();
        services.AddBareWireInMemory(_ => { });

        services.Invoking(s => s.AddBareWireInMemory(t => t.QueueCapacity(0)))
            .Should().Throw<BareWireConfigurationException>();
    }

    [Fact]
    public async Task SendBatchAsync_WithEmptyBatch_ReturnsEmptyResults()
    {
        using ServiceProvider sp = Build();
        ITransportAdapter adapter = sp.GetRequiredService<ITransportAdapter>();

        IReadOnlyList<SendResult> result = await adapter.SendBatchAsync([]);

        result.Should().BeEmpty();
    }

    // ── Adapter observability wiring: logger + meter resolved from DI, TimeProvider never is ─────────

    [Fact]
    public async Task AddBareWireInMemory_WhenMeterFactoryAndLoggerRegistered_AdapterEmitsMetricsOnBareWireMeter()
    {
        using var meterFactory = new TestMeterFactory();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMeterFactory>(meterFactory);
        services.AddBareWireInMemory(t =>
        {
            t.MaxMessageSize(16);
            t.ConfigureTopology(topo =>
            {
                topo.DeclareExchange("ex", ExchangeType.Direct);
                topo.DeclareQueue("q");
                topo.BindExchangeToQueue("ex", "q", "k");
            });
        });

        using ServiceProvider sp = services.BuildServiceProvider();
        ITransportAdapter adapter = sp.GetRequiredService<ITransportAdapter>();

        // AddBareWireInMemory's adapter factory resolved the IMeterFactory and created exactly one
        // meter, named "BareWire" (the same meter BareWire.Observability registers its instruments on).
        Meter meter = meterFactory.CreatedMeters.Should().ContainSingle().Which;
        meter.Name.Should().Be(InMemoryTransportMetrics.MeterName);

        var measurements = new List<(string Reason, string? Exchange)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            // Filters on the exact Meter instance this test's factory created — not merely on the
            // shared "BareWire" name — so this test cannot pick up instruments from another test's
            // meter of the same name running concurrently.
            if (ReferenceEquals(instrument.Meter, meter)
                && instrument.Name == InMemoryTransportMetrics.RejectedCounterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? reason = null;
            string? exchange = null;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                switch (tag.Key)
                {
                    case "reason": reason = tag.Value as string; break;
                    case "exchange": exchange = tag.Value as string; break;
                }
            }

            for (long i = 0; i < value; i++)
            {
                measurements.Add((reason ?? string.Empty, exchange));
            }
        });
        listener.Start();

        var oversized = new OutboundMessage(
            "k", new Dictionary<string, string> { ["BW-Exchange"] = "ex" }, new byte[17], "");
        IReadOnlyList<SendResult> result =
            await adapter.SendBatchAsync([oversized], TestContext.Current.CancellationToken);

        result.Single().IsConfirmed.Should().BeFalse();
        measurements.Should().ContainSingle(x => x.Reason == "oversized" && x.Exchange == "ex");
    }

    [Fact]
    public async Task AddBareWireInMemory_WhenNoMeterFactoryRegistered_ResolvesAdapterWithoutInstruments()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBareWireInMemory(t => t.ConfigureTopology(topo => topo.DeclareQueue("q")));

        using ServiceProvider sp = services.BuildServiceProvider();

        // No IMeterFactory was registered, so sp.GetService<IMeterFactory>() returns null and the
        // adapter factory passes meter: null — today's opt-in, zero-cost-when-unused behaviour: the
        // adapter still resolves and functions normally, it simply creates no instrument.
        sp.GetService<IMeterFactory>().Should().BeNull();
        ITransportAdapter adapter = sp.GetRequiredService<ITransportAdapter>();

        IReadOnlyList<SendResult> result =
            await adapter.SendBatchAsync([ToQueue("q")], TestContext.Current.CancellationToken);

        result.Single().IsConfirmed.Should().BeTrue();
    }

    [Fact]
    public void AddBareWireInMemory_WhenTimeProviderRegistered_AdapterDoesNotUseIt()
    {
        var fakeTimeProvider = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(fakeTimeProvider);
        services.AddBareWireInMemory(_ => { });

        using ServiceProvider sp = services.BuildServiceProvider();

        // AddBareWireInMemory never resolves TimeProvider from the container: a FakeTimeProvider
        // registered by a test host (which never advances on its own) would otherwise freeze the
        // adapter's defer timers and drain polling. The adapter must still be driven by its own
        // default (TimeProvider.System), not by whatever TimeProvider happens to be registered.
        var adapter = (InMemoryTransportAdapter)sp.GetRequiredService<ITransportAdapter>();

        adapter.EffectiveTimeProvider.Should().NotBeSameAs(fakeTimeProvider);
        adapter.EffectiveTimeProvider.Should().BeSameAs(TimeProvider.System);
    }

    private static OutboundMessage ToQueue(string queue) =>
        new(queue, new Dictionary<string, string> { ["BW-Exchange"] = string.Empty }, new byte[8], "");

    /// <summary>
    /// A real <see cref="IMeterFactory"/> — not a substitute — so <c>AddBareWireInMemory</c>'s
    /// <c>sp.GetService&lt;IMeterFactory&gt;()?.Create(...)</c> call exercises the same code path a
    /// host's own metrics registration would. Tracks every <see cref="Meter"/> it creates so a test can
    /// scope a <see cref="MeterListener"/> to exactly the instance this factory produced, and disposes
    /// every created meter itself — mirroring the real <see cref="IMeterFactory"/> contract, under which
    /// the factory (not the caller of <c>Create</c>) owns the meters it hands out.
    /// </summary>
    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _created = [];

        internal IReadOnlyList<Meter> CreatedMeters => _created;

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            _created.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (Meter meter in _created)
            {
                meter.Dispose();
            }
        }
    }

    private sealed record OrderCreated(Guid Id);

    private sealed class TestConsumer : IConsumer<OrderCreated>
    {
        public Task ConsumeAsync(ConsumeContext<OrderCreated> context) => Task.CompletedTask;
    }
}
