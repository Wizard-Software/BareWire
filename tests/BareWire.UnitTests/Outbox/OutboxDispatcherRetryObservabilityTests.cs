// NSubstitute's Returns() for ValueTask-returning mocks triggers CA2012 as a false positive.
// The ValueTask is consumed internally by NSubstitute and never double-consumed.
#pragma warning disable CA2012

using System.Collections.Frozen;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using BareWire.Abstractions.Outbox;
using BareWire.Abstractions.Transport;
using BareWire.Outbox;
using BareWire.Outbox.EntityFramework;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Outbox;

// OutboxDispatcher's retry-observability integration: the rate-limited retry Warning, the retried-rows
// counter, the lazily sampled oldest-due-retry gauge, and the DI wiring of the new optional ctor
// parameters (TimeProvider, IMeterFactory).
public sealed class OutboxDispatcherRetryObservabilityTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    // Minimal ILogger<T> that captures the log level, formatted message, and structured state of every
    // call — IsEnabled always true (a default NSubstitute ILogger<T> reports false and silently drops
    // [LoggerMessage] calls).
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            IReadOnlyList<KeyValuePair<string, object?>> kvps =
                state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            Events.Add((logLevel, formatter(state, exception), kvps));
        }
    }

    private static OutboxEntry CreateEntry(long id, int nackCount = 0, string? orderingKey = null)
    {
        ReadOnlySpan<byte> body = "test-body"u8;
        byte[] pooled = System.Buffers.ArrayPool<byte>.Shared.Rent(body.Length);
        body.CopyTo(pooled);
        return new OutboxEntry
        {
            Id = id,
            RoutingKey = "test.routing.key",
            Headers = new Dictionary<string, string>(),
            PooledBody = pooled,
            BodyLength = body.Length,
            ContentType = "application/json",
            CreatedAt = DateTimeOffset.UtcNow,
            Status = OutboxEntryStatus.Pending,
            OrderingKey = orderingKey,
            NackCount = nackCount,
        };
    }

    private static IServiceScopeFactory CreateScopeFactory(IOutboxStore store)
    {
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IOutboxStore)).Returns(store);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);
        return scopeFactory;
    }

    // A lifetime whose ApplicationStarted has already fired, so the dispatcher's loop starts immediately.
    private static IHostApplicationLifetime StartedLifetime()
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        var started = new CancellationTokenSource();
        started.Cancel();
        lifetime.ApplicationStarted.Returns(started.Token);
        return lifetime;
    }

    private static IOutboxStore CreateStoreReturningOnceThenEmpty(IReadOnlyList<OutboxEntry> entries)
    {
        var store = Substitute.For<IOutboxStore>();
        bool first = true;
        store.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (first)
                {
                    first = false;
                    return ValueTask.FromResult(entries);
                }

                return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>());
            });
        store.ReleaseLockAsync(
                Arg.Any<IReadOnlyList<long>>(), Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult<IReadOnlySet<long>>(FrozenSet<long>.Empty));
        store.MarkDeliveredAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.CompletedTask);
        return store;
    }

    private static ITransportAdapter CreateAdapterReturning(Func<int, bool> isConfirmed)
    {
        var adapter = Substitute.For<ITransportAdapter>();
        adapter.SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                IReadOnlyList<OutboundMessage> msgs = ci.Arg<IReadOnlyList<OutboundMessage>>();
                SendResult[] results = msgs
                    .Select((_, i) => new SendResult(IsConfirmed: isConfirmed(i), DeliveryTag: (ulong)i))
                    .ToArray();
                return Task.FromResult<IReadOnlyList<SendResult>>(results);
            });
        return adapter;
    }

    private static (IMeterFactory Factory, Meter Meter) CreateMeterFactory()
    {
        var meter = new Meter("BareWire.Test." + Guid.NewGuid());
        var factory = Substitute.For<IMeterFactory>();
        factory.Create(Arg.Any<MeterOptions>()).Returns(meter);
        return (factory, meter);
    }

    private static MeterListener CreateStartedListener(
        Meter meter,
        Action<string, long> onLong,
        Action<string, double, KeyValuePair<string, object?>[]> onDouble)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, meter))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            onLong(instrument.Name, measurement));
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            onDouble(instrument.Name, measurement, tags.ToArray()));
        listener.Start();
        return listener;
    }

    // ── Retry Warning + counter ──────────────────────────────────────────────

    [Fact]
    public async Task DispatchBatch_WhenRowNacked_LogsRetryWarningWithRowIdAndRetryCount()
    {
        IOutboxStore store = CreateStoreReturningOnceThenEmpty([CreateEntry(7, nackCount: 2)]);
        ITransportAdapter adapter = CreateAdapterReturning(_ => false);
        var logger = new CapturingLogger<OutboxDispatcher>();
        var options = new OutboxOptions { PollingInterval = TimeSpan.FromMilliseconds(10), DispatchBatchSize = 100 };

        await using var sut = new OutboxDispatcher(
            CreateScopeFactory(store), adapter, options, logger, StartedLifetime());

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await sut.StopAsync(CancellationToken.None);

        var warnings = logger.Events.Where(e => e.Level == LogLevel.Warning).ToList();
        warnings.Should().ContainSingle();
        warnings[0].State.Should().Contain(kv => kv.Key == "RowId" && Equals(kv.Value, 7L));
        warnings[0].State.Should().Contain(kv => kv.Key == "RetryCount" && Equals(kv.Value, 3));
        sut.RetryDiagnostics.RetriedRowCount.Should().Be(1);
    }

    [Fact]
    public async Task DispatchBatch_WhenRowNackedWithMeterFactory_IncrementsRetriedRowsCounter()
    {
        IOutboxStore store = CreateStoreReturningOnceThenEmpty([CreateEntry(1, nackCount: 0)]);
        ITransportAdapter adapter = CreateAdapterReturning(_ => false);
        var logger = new CapturingLogger<OutboxDispatcher>();
        var options = new OutboxOptions { PollingInterval = TimeSpan.FromMilliseconds(10), DispatchBatchSize = 100 };
        (IMeterFactory factory, Meter meter) = CreateMeterFactory();

        await using var sut = new OutboxDispatcher(
            CreateScopeFactory(store), adapter, options, logger, StartedLifetime(), meterFactory: factory);

        var longMeasurements = new List<(string Name, long Value)>();
        using MeterListener listener = CreateStartedListener(
            meter,
            (name, value) => longMeasurements.Add((name, value)),
            (_, _, _) => { });

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await sut.StopAsync(CancellationToken.None);

        longMeasurements
            .Where(m => m.Name == OutboxRetryDiagnostics.RetriedRowsCounterName)
            .Sum(m => m.Value)
            .Should().Be(1);

        meter.Dispose();
    }

    [Fact]
    public async Task DispatchBatch_PerKeyBarrierSiblings_AreNotCountedAsRetries()
    {
        var entries = new List<OutboxEntry> { CreateEntry(1, orderingKey: "k"), CreateEntry(2, orderingKey: "k") };
        IOutboxStore store = CreateStoreReturningOnceThenEmpty(entries);
        ITransportAdapter adapter = CreateAdapterReturning(i => i != 0); // Id=1 nacked, Id=2 confirmed (barrier sibling)
        var logger = new CapturingLogger<OutboxDispatcher>();
        var options = new OutboxOptions
        {
            PollingInterval = TimeSpan.FromMilliseconds(10),
            DispatchBatchSize = 100,
            OrderingMode = OrderingMode.PerKey,
            OrderingKeyHeaderName = "x-order-key",
        };

        await using var sut = new OutboxDispatcher(
            CreateScopeFactory(store), adapter, options, logger, StartedLifetime());

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await sut.StopAsync(CancellationToken.None);

        sut.RetryDiagnostics.RetriedRowCount.Should().Be(1);
        logger.Events.Where(e => e.Level == LogLevel.Warning)
            .Should().ContainSingle(e => e.State.Any(kv => kv.Key == "RowId" && Equals(kv.Value, 1L)));
    }

    [Fact]
    public async Task DispatchBatch_SeveralNackedRows_ReportsRowWithHighestRetryCount()
    {
        var entries = new List<OutboxEntry>
        {
            CreateEntry(3, nackCount: 0),
            CreateEntry(5, nackCount: 4),
            CreateEntry(9, nackCount: 4),
        };
        IOutboxStore store = CreateStoreReturningOnceThenEmpty(entries);
        ITransportAdapter adapter = CreateAdapterReturning(_ => false); // all nacked
        var logger = new CapturingLogger<OutboxDispatcher>();
        var options = new OutboxOptions { PollingInterval = TimeSpan.FromMilliseconds(10), DispatchBatchSize = 100 };

        await using var sut = new OutboxDispatcher(
            CreateScopeFactory(store), adapter, options, logger, StartedLifetime());

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await sut.StopAsync(CancellationToken.None);

        var warning = logger.Events.Single(e => e.Level == LogLevel.Warning);
        warning.State.Should().Contain(kv => kv.Key == "RowId" && Equals(kv.Value, 5L));
        warning.State.Should().Contain(kv => kv.Key == "RetryCount" && Equals(kv.Value, 5));
        warning.State.Should().Contain(kv => kv.Key == "RetriedCount" && Equals(kv.Value, 3));
    }

    [Fact]
    public async Task DispatchBatch_WhenRowNacked_DoesNotLogPartialSendFailureAsWarning()
    {
        IOutboxStore store = CreateStoreReturningOnceThenEmpty([CreateEntry(1)]);
        ITransportAdapter adapter = CreateAdapterReturning(_ => false);
        var logger = new CapturingLogger<OutboxDispatcher>();
        var options = new OutboxOptions { PollingInterval = TimeSpan.FromMilliseconds(10), DispatchBatchSize = 100 };

        await using var sut = new OutboxDispatcher(
            CreateScopeFactory(store), adapter, options, logger, StartedLifetime());

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await sut.StopAsync(CancellationToken.None);

        logger.Events.Count(e => e.Level == LogLevel.Warning).Should().Be(
            1, "the rate-limited retry entry must be the ONLY Warning — LogPartialSendFailure moved to Debug");
    }

    // ── Gauge sampling ────────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchBatch_AfterGaugeObserved_ProbesStoreOnceAndRecordsSample()
    {
        var store = Substitute.For<IOutboxStore, IOutboxRetryBacklogProbe>();
        var probe = (IOutboxRetryBacklogProbe)store;
        store.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>()));

        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        DateTimeOffset dueAt = clock.GetUtcNow() - TimeSpan.FromSeconds(30);
        probe.GetOldestDueRetryAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<DateTimeOffset?>(dueAt));

        ITransportAdapter adapter = CreateAdapterReturning(_ => true);
        var logger = new CapturingLogger<OutboxDispatcher>();
        var options = new OutboxOptions { PollingInterval = TimeSpan.FromMilliseconds(10), DispatchBatchSize = 100 };
        (IMeterFactory factory, Meter meter) = CreateMeterFactory();

        await using var sut = new OutboxDispatcher(
            CreateScopeFactory(store), adapter, options, logger, StartedLifetime(),
            timeProvider: clock, meterFactory: factory);

        var doubleMeasurements = new List<(string Name, double Value)>();
        using MeterListener listener = CreateStartedListener(
            meter,
            (_, _) => { },
            (name, value, _) => doubleMeasurements.Add((name, value)));

        listener.RecordObservableInstruments(); // requests a sample before any batch runs

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await sut.StopAsync(CancellationToken.None);

        await probe.Received(1).GetOldestDueRetryAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

        doubleMeasurements.Clear();
        listener.RecordObservableInstruments();
        doubleMeasurements
            .Where(m => m.Name == OutboxRetryDiagnostics.OldestDueRetryAgeGaugeName)
            .Select(m => m.Value)
            .Should().ContainSingle().Which.Should().Be(30.0);

        meter.Dispose();
    }

    [Fact]
    public async Task DispatchBatch_GaugeNeverObserved_DoesNotProbeStore()
    {
        var store = Substitute.For<IOutboxStore, IOutboxRetryBacklogProbe>();
        var probe = (IOutboxRetryBacklogProbe)store;
        store.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>()));

        ITransportAdapter adapter = CreateAdapterReturning(_ => true);
        var logger = new CapturingLogger<OutboxDispatcher>();
        var options = new OutboxOptions { PollingInterval = TimeSpan.FromMilliseconds(10), DispatchBatchSize = 100 };
        (IMeterFactory factory, Meter meter) = CreateMeterFactory();

        await using var sut = new OutboxDispatcher(
            CreateScopeFactory(store), adapter, options, logger, StartedLifetime(), meterFactory: factory);

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await sut.StopAsync(CancellationToken.None);

        await probe.DidNotReceive().GetOldestDueRetryAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

        meter.Dispose();
    }

    [Fact]
    public async Task DispatchBatch_ProbeThrows_BatchStillDispatched()
    {
        var store = Substitute.For<IOutboxStore, IOutboxRetryBacklogProbe>();
        var probe = (IOutboxRetryBacklogProbe)store;
        bool first = true;
        store.GetPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (first)
                {
                    first = false;
                    return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(new List<OutboxEntry> { CreateEntry(1) });
                }

                return ValueTask.FromResult<IReadOnlyList<OutboxEntry>>(Array.Empty<OutboxEntry>());
            });
        store.ReleaseLockAsync(
                Arg.Any<IReadOnlyList<long>>(), Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult<IReadOnlySet<long>>(FrozenSet<long>.Empty));
        store.MarkDeliveredAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.CompletedTask);
        probe.GetOldestDueRetryAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<DateTimeOffset?>>(_ => throw new InvalidOperationException("probe failure"));

        ITransportAdapter adapter = CreateAdapterReturning(_ => true);
        var logger = new CapturingLogger<OutboxDispatcher>();
        var options = new OutboxOptions { PollingInterval = TimeSpan.FromMilliseconds(10), DispatchBatchSize = 100 };
        (IMeterFactory factory, Meter meter) = CreateMeterFactory();

        await using var sut = new OutboxDispatcher(
            CreateScopeFactory(store), adapter, options, logger, StartedLifetime(), meterFactory: factory);

        using MeterListener listener = CreateStartedListener(meter, (_, _) => { }, (_, _, _) => { });
        listener.RecordObservableInstruments(); // requests a sample so the probe is actually invoked

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await sut.StopAsync(CancellationToken.None);

        await adapter.Received().SendBatchAsync(
            Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>());
        logger.Events.Should().NotContain(e => e.Level == LogLevel.Error);

        meter.Dispose();
    }

    // ── Pure helper ───────────────────────────────────────────────────────────

    [Fact]
    public void SelectReportedRetryRow_SaturatedNackCount_ReportsIntMaxValue()
    {
        var pending = new List<OutboxEntry> { CreateEntry(1, nackCount: int.MaxValue) };
        var results = new List<SendResult> { new(IsConfirmed: false, DeliveryTag: 0) };

        (long rowId, int retryCount) = OutboxDispatcher.SelectReportedRetryRow(pending, results);

        rowId.Should().Be(1);
        retryCount.Should().Be(int.MaxValue);
    }

    // ── Log safety (D-V3) ─────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchBatch_WhenRowNacked_NeverLogsInstanceIdentityShapedString()
    {
        // An instance-id-shaped string ("Machine:PID:Guid", the exact shape ServiceCollectionExtensions
        // generates in production) is wired into a REAL EfCoreOutboxStore's claim/nack cycle, so a
        // regression that started interpolating LockedBy (or any instance identity) into a log message
        // would be caught here — not merely asserted never to appear by construction.
        string instanceId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<OutboxDbContext>(o => o.UseSqlite(connection));
        var options = new OutboxOptions { PollingInterval = TimeSpan.FromMilliseconds(10), DispatchBatchSize = 100 };
        services.AddSingleton(options);
        services.AddSingleton(new OutboxInstanceId(instanceId));
        services.AddSingleton<IOutboxSqlDialect, PostgresOutboxSqlDialect>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOutboxJitterSource>(SharedRandomOutboxJitterSource.Instance);
        services.AddScoped<IOutboxStore>(sp => new EfCoreOutboxStore(
            sp.GetRequiredService<OutboxDbContext>(),
            sp.GetRequiredService<OutboxInstanceId>(),
            sp.GetRequiredService<IOutboxSqlDialect>(),
            sp.GetRequiredService<OutboxOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOutboxJitterSource>()));

        await using ServiceProvider provider = services.BuildServiceProvider();

        await using (AsyncServiceScope seedScope = provider.CreateAsyncScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<OutboxDbContext>();
            await dbContext.Database.EnsureCreatedAsync();
            IOutboxStore seedStore = seedScope.ServiceProvider.GetRequiredService<IOutboxStore>();
            await seedStore.SaveMessagesAsync([new OutboundMessage(
                routingKey: "orders.created",
                headers: new Dictionary<string, string>(),
                body: "{}"u8.ToArray(),
                contentType: "application/json")]);
            await dbContext.SaveChangesAsync();
        }

        var logger = new CapturingLogger<OutboxDispatcher>();
        ITransportAdapter adapter = CreateAdapterReturning(_ => false); // nack, so the row is released for retry

        await using var sut = new OutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            adapter,
            options,
            logger,
            StartedLifetime());

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await sut.StopAsync(CancellationToken.None);

        logger.Events.Should().NotBeEmpty("the batch must have produced at least one log entry to make this assertion meaningful");
        logger.Events.Should().OnlyContain(
            e => !e.Message.Contains(instanceId, StringComparison.Ordinal),
            "no dispatcher log message may ever reveal the claim owner / instance identity");
    }

    // ── DI resolution (D-V2) ──────────────────────────────────────────────────

    [Fact]
    public async Task AddBareWireOutbox_ResolvesOutboxDispatcherAsHostedService_WithoutMeterFactoryRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ITransportAdapter>());
        services.AddSingleton(Substitute.For<IHostApplicationLifetime>());

        services.AddBareWireOutbox(configureDbContext: o => o.UseSqlite("DataSource=:memory:"));

        await using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetServices<IHostedService>().OfType<OutboxDispatcher>().Should().ContainSingle();
    }

    [Fact]
    public async Task AddBareWireOutbox_ResolvesOutboxDispatcherAsHostedService_WithMeterFactoryRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<ITransportAdapter>());
        services.AddSingleton(Substitute.For<IHostApplicationLifetime>());
        (IMeterFactory factory, Meter meter) = CreateMeterFactory();
        services.AddSingleton(factory);

        services.AddBareWireOutbox(configureDbContext: o => o.UseSqlite("DataSource=:memory:"));

        await using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetServices<IHostedService>().OfType<OutboxDispatcher>().Should().ContainSingle();

        meter.Dispose();
    }
}
