using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Configuration;
using BareWire.Transport.InMemory.Internal;
using BareWire.Transport.InMemory.Topology;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class InMemoryRouterTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static InMemoryTransportOptions Options(Action<IInMemoryConfigurator> configure)
    {
        var c = new InMemoryConfigurator();
        configure(c);
        return c.Build();
    }

    private static InMemoryRouter Router(
        InMemoryTransportOptions options,
        ILogger<InMemoryRouter>? logger = null,
        Meter? meter = null,
        TimeProvider? timeProvider = null) =>
        new(InMemoryTopologyInterpreter.BuildRegistry(options), options, logger ?? NullLogger<InMemoryRouter>.Instance, meter, timeProvider);

    // ── Direct / Fanout / Topic ──────────────────────────────────────────────

    [Fact]
    public void Route_DirectExchange_ReturnsOnlyExactKeyBindings()
    {
        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t =>
        {
            t.DeclareExchange("d", ExchangeType.Direct);
            t.DeclareQueue("q1");
            t.DeclareQueue("q2");
            t.BindExchangeToQueue("d", "q1", "a");
            t.BindExchangeToQueue("d", "q2", "b");
        }));
        InMemoryRouter router = Router(options);

        InMemoryRouteResult result = router.Route("d", "a");

        result.Status.Should().Be(InMemoryRouteStatus.Routed);
        result.Queues.Should().Equal("q1");
    }

    [Fact]
    public void Route_FanoutExchange_ReturnsAllBoundQueuesIgnoringKey()
    {
        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t =>
        {
            t.DeclareExchange("f", ExchangeType.Fanout);
            t.DeclareQueue("q1");
            t.DeclareQueue("q2");
            t.BindExchangeToQueue("f", "q1", "x");
            t.BindExchangeToQueue("f", "q2", "");
        }));
        InMemoryRouter router = Router(options);

        InMemoryRouteResult result = router.Route("f", "anything");

        result.Status.Should().Be(InMemoryRouteStatus.Routed);
        result.Queues.Should().Equal("q1", "q2"); // Sorted Ordinal, not first-occurrence order.
    }

    [Theory]
    [InlineData("orders.created.eu", new[] { "all", "eu" })]
    [InlineData("orders.created", new[] { "all", "created" })]
    public void Route_TopicExchange_MatchesStarAndHash(string key, string[] expected)
    {
        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t =>
        {
            t.DeclareExchange("t", ExchangeType.Topic);
            t.DeclareQueue("all");
            t.DeclareQueue("created");
            t.DeclareQueue("eu");
            t.BindExchangeToQueue("t", "all", "orders.#");
            t.BindExchangeToQueue("t", "created", "orders.*");
            t.BindExchangeToQueue("t", "eu", "#.eu");
        }));
        InMemoryRouter router = Router(options);

        InMemoryRouteResult result = router.Route("t", key);

        result.Status.Should().Be(InMemoryRouteStatus.Routed);
        result.Queues.Should().Equal(expected.OrderBy(static q => q, StringComparer.Ordinal));
    }

    // ── Exchange-to-exchange, cycles, deduplication ──────────────────────────

    [Fact]
    public void Route_ExchangeToExchangeBinding_RoutesThroughDestinationWithOriginalKey()
    {
        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t =>
        {
            t.DeclareExchange("src", ExchangeType.Topic);
            t.DeclareExchange("dst", ExchangeType.Direct);
            t.DeclareQueue("q");
            t.BindExchangeToExchange("src", "dst", "orders.#");
            t.BindExchangeToQueue("dst", "q", "orders.created");
        }));
        InMemoryRouter router = Router(options);

        InMemoryRouteResult result = router.Route("src", "orders.created");

        result.Status.Should().Be(InMemoryRouteStatus.Routed);
        result.Queues.Should().Equal("q");
    }

    [Fact]
    public void Route_ExchangeBindingCycle_TerminatesAndReturnsQueues()
    {
        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t =>
        {
            t.DeclareExchange("a", ExchangeType.Fanout);
            t.DeclareExchange("b", ExchangeType.Fanout);
            t.DeclareQueue("q");
            t.BindExchangeToExchange("a", "b", "");
            t.BindExchangeToExchange("b", "a", "");
            t.BindExchangeToQueue("b", "q", "");
        }));
        InMemoryRouter router = Router(options);

        InMemoryRouteResult result = router.Route("a", "k");

        result.Status.Should().Be(InMemoryRouteStatus.Routed);
        result.Queues.Should().Equal("q");
    }

    [Fact]
    public void Route_QueueMatchedByTwoDifferentBindingsOnSameExchange_ReturnsSingleCopy()
    {
        // Identical bindings are already deduplicated by topology normalization — this test uses
        // two DIFFERENT topic patterns that both resolve to the same queue for the given routing key.
        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t =>
        {
            t.DeclareExchange("t", ExchangeType.Topic);
            t.DeclareQueue("q");
            t.BindExchangeToQueue("t", "q", "orders.#");
            t.BindExchangeToQueue("t", "q", "#.created");
        }));
        InMemoryRouter router = Router(options);

        InMemoryRouteResult result = router.Route("t", "orders.created");

        result.Status.Should().Be(InMemoryRouteStatus.Routed);
        result.Queues.Should().Equal("q");
    }

    [Fact]
    public void Route_QueueReachableDirectlyAndViaExchangeToExchange_ReturnsSingleCopy()
    {
        // The same queue is reachable both by a direct binding on the source exchange AND through
        // an exchange-to-exchange hop — the router still returns exactly one copy.
        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t =>
        {
            t.DeclareExchange("a", ExchangeType.Fanout);
            t.DeclareExchange("b", ExchangeType.Fanout);
            t.DeclareQueue("q");
            t.BindExchangeToQueue("a", "q", "");
            t.BindExchangeToExchange("a", "b", "");
            t.BindExchangeToQueue("b", "q", "");
        }));
        InMemoryRouter router = Router(options);

        InMemoryRouteResult result = router.Route("a", "k");

        result.Status.Should().Be(InMemoryRouteStatus.Routed);
        result.Queues.Should().Equal("q");
    }

    // ── Default exchange ─────────────────────────────────────────────────────

    [Fact]
    public void Route_DefaultExchangeWithDeclaredQueue_ReturnsThatQueue()
    {
        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t => t.DeclareQueue("q")));
        InMemoryRouter router = Router(options);

        InMemoryRouteResult result = router.Route("", "q");

        result.Status.Should().Be(InMemoryRouteStatus.Routed);
        result.Queues.Should().Equal("q");
    }

    [Fact]
    public void Route_DefaultExchangeWithUndeclaredQueue_ReturnsUnroutable()
    {
        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t => t.DeclareQueue("q")));
        InMemoryRouter router = Router(options);

        InMemoryRouteResult result = router.Route("", "nope");

        result.Status.Should().Be(InMemoryRouteStatus.Unroutable);
        result.Queues.Should().BeEmpty();
    }

    [Fact]
    public void Route_DefaultExchangeUndeclaredQueue_DoesNotPolluteCache()
    {
        // The default exchange is served from a frozen map built once in the constructor — an
        // unknown queue name is never written into the route cache.
        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t => t.DeclareQueue("q")));
        InMemoryRouter router = Router(options);

        router.Route("", "nope");
        router.Route("", "nope");

        router.CacheCount.Should().Be(0);
    }

    [Fact]
    public void Route_DefaultExchangeDeclaredQueue_AllocatesNothing()
    {
        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t => t.DeclareQueue("q")));
        InMemoryRouter router = Router(options);

        for (int i = 0; i < 100; i++)
        {
            router.Route("", "q");
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            router.Route("", "q");
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
        router.Route("", "q").Queues.Should().Equal("q");
    }

    // ── Undeclared exchange / no matching binding ────────────────────────────

    [Fact]
    public void Route_UndeclaredExchange_ReturnsExchangeNotFoundAndDoesNotCache()
    {
        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t => t.DeclareQueue("q")));
        InMemoryRouter router = Router(options);

        InMemoryRouteResult result = router.Route("missing", "k");

        result.Status.Should().Be(InMemoryRouteStatus.ExchangeNotFound);
        result.Queues.Should().BeEmpty();
        router.CacheCount.Should().Be(0);
    }

    [Fact]
    public void Route_NoMatchingBinding_ReturnsUnroutable()
    {
        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t =>
        {
            t.DeclareExchange("d", ExchangeType.Direct);
            t.DeclareQueue("q");
            t.BindExchangeToQueue("d", "q", "a");
        }));
        InMemoryRouter router = Router(options);

        InMemoryRouteResult result = router.Route("d", "b");

        result.Status.Should().Be(InMemoryRouteStatus.Unroutable);
        result.Queues.Should().BeEmpty();
    }

    // ── Routing key length ────────────────────────────────────────────────────

    [Fact]
    public void Route_RoutingKeyLongerThan255Bytes_ReturnsRoutingKeyTooLong()
    {
        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t => t.DeclareQueue("q")));
        InMemoryRouter router = Router(options);

        router.Route("", new string('a', 256)).Status.Should().Be(InMemoryRouteStatus.RoutingKeyTooLong);
        router.Route("", new string('a', 255)).Status.Should().NotBe(InMemoryRouteStatus.RoutingKeyTooLong);
        router.Route("", new string('ą', 128)).Status.Should().Be(InMemoryRouteStatus.RoutingKeyTooLong); // 128 * 2 B = 256 B
    }

    [Fact]
    public void Route_LoneSurrogateRoutingKey_DoesNotThrowAndIsNotTooLong()
    {
        // A lone (unpaired) surrogate must not throw out of Encoding.UTF8.GetByteCount and must
        // not be misclassified as too long.
        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t => t.DeclareQueue("q")));
        InMemoryRouter router = Router(options);

        string key = new string('a', 90) + '\uD800'; // > 85 chars, forces the UTF-8 byte-count path
        Action act = () => router.Route("", key);

        act.Should().NotThrow();
        router.Route("", key).Status.Should().NotBe(InMemoryRouteStatus.RoutingKeyTooLong);
    }

    // ── Cache behavior ────────────────────────────────────────────────────────

    [Fact]
    public void Route_SameKeyTwice_ReturnsSameCachedArrayInstance()
    {
        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t =>
        {
            t.DeclareExchange("d", ExchangeType.Direct);
            t.DeclareQueue("q");
            t.BindExchangeToQueue("d", "q", "a");
        }));
        InMemoryRouter router = Router(options);

        InMemoryRouteResult first = router.Route("d", "a");
        InMemoryRouteResult second = router.Route("d", "a");

        string[] firstArray = ImmutableCollectionsMarshal.AsArray(first.Queues)!;
        string[] secondArray = ImmutableCollectionsMarshal.AsArray(second.Queues)!;
        ReferenceEquals(firstArray, secondArray).Should().BeTrue();
        router.CacheCount.Should().Be(1);
    }

    [Fact]
    public void Route_WhenCacheIsFull_ClearsCacheAndStaysWithinCapacity()
    {
        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t =>
        {
            t.DeclareExchange("d", ExchangeType.Direct);
            t.DeclareQueue("q");
            for (int i = 0; i < 10; i++)
            {
                t.BindExchangeToQueue("d", "q", $"k{i}");
            }
        }));
        options.RouteCacheCapacity = 3;
        InMemoryRouter router = Router(options);

        for (int i = 0; i < 10; i++)
        {
            InMemoryRouteResult result = router.Route("d", $"k{i}");
            result.Status.Should().Be(InMemoryRouteStatus.Routed);
            result.Queues.Should().Equal("q");
            router.CacheCount.Should().BeLessThanOrEqualTo(3);
        }
    }

    [Fact]
    public void Route_CacheHit_AllocatesNothing()
    {
        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t =>
        {
            t.DeclareExchange("t", ExchangeType.Topic);
            t.DeclareQueue("q");
            t.BindExchangeToQueue("t", "q", "orders.#");
        }));
        InMemoryRouter router = Router(options);

        for (int i = 0; i < 100; i++)
        {
            router.Route("t", "orders.created"); // warm-up: populate the cache, let the JIT tier up.
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            router.Route("t", "orders.created");
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    // ── Argument validation ───────────────────────────────────────────────────

    [Fact]
    public void Constructor_WhenArgumentIsNull_ThrowsArgumentNullException()
    {
        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t => t.DeclareQueue("q")));

        Action registryNull = () => _ = new InMemoryRouter(null!, options, NullLogger<InMemoryRouter>.Instance);
        Action optionsNull = () => _ = new InMemoryRouter(InMemoryTopologyInterpreter.BuildRegistry(options), null!, NullLogger<InMemoryRouter>.Instance);
        Action loggerNull = () => _ = new InMemoryRouter(InMemoryTopologyInterpreter.BuildRegistry(options), options, null!);

        registryNull.Should().Throw<ArgumentNullException>();
        optionsNull.Should().Throw<ArgumentNullException>();
        loggerNull.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Route_WhenArgumentIsNull_ThrowsArgumentNullException()
    {
        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t => t.DeclareQueue("q")));
        InMemoryRouter router = Router(options);

        Action exchangeNull = () => router.Route(null!, "k");
        Action routingKeyNull = () => router.Route("", null!);

        exchangeNull.Should().Throw<ArgumentNullException>();
        routingKeyNull.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Validate_WhenRouteCacheCapacityIsNotPositive_ThrowsConfigurationException()
    {
        InMemoryTransportOptions options = new() { RouteCacheCapacity = 0 };

        Action act = () => options.Validate();

        act.Should().Throw<BareWireConfigurationException>()
            .Which.OptionName.Should().Be(nameof(InMemoryTransportOptions.RouteCacheCapacity));
    }

    // ── ReportUnroutable (Deliverable C) ─────────────────────────────────────

    private sealed record LogEntry(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> State);

    /// <summary>Records every emitted log event, including its structured state.</summary>
    private sealed class RecordingLogger : ILogger<InMemoryRouter>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Dictionary<string, object?> stateEntries = state is IReadOnlyList<KeyValuePair<string, object?>> structured
                ? structured.ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value)
                : [];
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), stateEntries));
        }
    }

    /// <summary>A hand-written <see cref="TimeProvider"/> subclass — the test project's fake-time-provider
    /// package is deliberately not used here per the router's throttle-clock contract.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    [Fact]
    public void ReportUnroutable_WithoutGuaranteedRouting_ReturnsConfirmedAndLogsWarning()
    {
        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t => t.DeclareExchange("orders", ExchangeType.Direct)));
        var logger = new RecordingLogger();
        InMemoryRouter router = Router(options, logger: logger);

        bool confirmed = router.ReportUnroutable("orders", "orders.created");

        confirmed.Should().BeTrue();
        router.UnroutableCount.Should().Be(1);
        LogEntry entry = logger.Entries.Should().ContainSingle().Which;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain("orders").And.Contain("orders.created");
    }

    [Fact]
    public void ReportUnroutable_WithGuaranteedRouting_ReturnsNotConfirmed()
    {
        InMemoryTransportOptions options = Options(c =>
        {
            c.GuaranteedRouting();
            c.ConfigureTopology(t => t.DeclareExchange("orders", ExchangeType.Direct));
        });
        InMemoryRouter router = Router(options);

        router.ReportUnroutable("orders", "orders.created").Should().BeFalse();
        router.UnroutableCount.Should().Be(1);
    }

    [Fact]
    public void ReportUnroutable_WithMeter_IncrementsUnroutableCounterWithExchangeTagOnly()
    {
        using var meter = new Meter("BareWire.Tests." + Guid.NewGuid());
        List<KeyValuePair<string, object?>[]> measurements = [];
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter == meter)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => measurements.Add(tags.ToArray()));
        listener.Start();

        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t => t.DeclareExchange("orders", ExchangeType.Direct)));
        InMemoryRouter router = Router(options, meter: meter);

        router.ReportUnroutable("orders", "k");
        listener.RecordObservableInstruments();

        KeyValuePair<string, object?>[] tags = measurements.Should().ContainSingle().Which;
        tags.Select(static t => t.Key).Should().BeEquivalentTo(["exchange"]);
        tags.Single().Value.Should().Be("orders");
    }

    [Fact]
    public void ReportUnroutable_CalledManyTimesWithinWindow_ThrottlesToOneWarningThenLogsAgainAfterWindow()
    {
        // 1000 calls -> exactly 1 Warning while counters/metric still increment every call; advancing
        // the clock by the throttle window produces a second Warning carrying the suppressed count.
        var timeProvider = new ManualTimeProvider();
        using var meter = new Meter("BareWire.Tests." + Guid.NewGuid());
        long measured = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter == meter)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref measured, value));
        listener.Start();

        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t => t.DeclareExchange("orders", ExchangeType.Direct)));
        var logger = new RecordingLogger();
        InMemoryRouter router = Router(options, logger: logger, meter: meter, timeProvider: timeProvider);

        for (int i = 0; i < 1_000; i++)
        {
            router.ReportUnroutable("orders", "orders.created");
        }

        logger.Entries.Should().ContainSingle();
        router.UnroutableCount.Should().Be(1_000);
        measured.Should().Be(1_000);

        timeProvider.Advance(TimeSpan.FromSeconds(60));
        router.ReportUnroutable("orders", "orders.created");

        logger.Entries.Should().HaveCount(2);
        logger.Entries[1].State["SuppressedCount"].Should().Be(999);
    }

    [Fact]
    public void ReportUnroutable_LogState_ContainsExactlyExpectedKeys()
    {
        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t => t.DeclareExchange("orders", ExchangeType.Direct)));
        var logger = new RecordingLogger();
        InMemoryRouter router = Router(options, logger: logger);

        router.ReportUnroutable("orders", "orders.created");

        logger.Entries.Should().ContainSingle().Which.State.Keys.Should().BeEquivalentTo(
            ["Exchange", "RoutingKey", "SuppressedCount", "{OriginalFormat}"]);
    }

    [Fact]
    public void ReportUnroutable_ForUndeclaredExchange_ThrowsArgumentExceptionAndDoesNotChangeCounters()
    {
        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t => t.DeclareQueue("q")));
        InMemoryRouter router = Router(options);

        Action act = () => router.ReportUnroutable("missing", "k");

        act.Should().Throw<ArgumentException>();
        router.UnroutableCount.Should().Be(0);
    }

    [Fact]
    public void ReportUnroutable_ForDefaultExchange_DoesNotThrow()
    {
        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t => t.DeclareQueue("q")));
        InMemoryRouter router = Router(options);

        Action act = () => router.ReportUnroutable("", "nope");

        act.Should().NotThrow();
        router.UnroutableCount.Should().Be(1);
    }

    [Fact]
    public void ReportUnroutable_WhenArgumentIsNull_ThrowsArgumentNullException()
    {
        InMemoryTransportOptions options = Options(c => c.ConfigureTopology(t => t.DeclareExchange("orders", ExchangeType.Direct)));
        InMemoryRouter router = Router(options);

        Action exchangeNull = () => router.ReportUnroutable(null!, "k");
        Action routingKeyNull = () => router.ReportUnroutable("orders", null!);

        exchangeNull.Should().Throw<ArgumentNullException>();
        routingKeyNull.Should().Throw<ArgumentNullException>();
    }
}
