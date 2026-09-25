using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Configuration;
using BareWire.Transport.InMemory.Internal;
using BareWire.Transport.InMemory.Topology;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class InMemoryStartupDiagnosticsTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed record LogEntry(LogLevel Level, int EventId, string Message);

    /// <summary>Records every emitted log event as (level, event id, formatted message).</summary>
    private sealed class FakeLogger : ILogger<InMemoryStartupDiagnostics>
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
            => Entries.Add(new LogEntry(logLevel, eventId.Id, formatter(state, exception)));
    }

    /// <summary>Records the log entries produced through a real DI-resolved <see cref="ILogger{T}"/>.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<LogEntry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => entries.Add(new LogEntry(logLevel, eventId.Id, formatter(state, exception)));
        }
    }

    private sealed record OrderCreated(Guid Id);

    private sealed class TestConsumer : IConsumer<OrderCreated>
    {
        public Task ConsumeAsync(ConsumeContext<OrderCreated> context) => Task.CompletedTask;
    }

    private static InMemoryStartupDiagnostics Create(
        InMemoryTransportOptions? options = null,
        ExchangeRegistry? registry = null,
        bool adapterActive = true,
        OutboxRegistrationState outbox = default,
        IHostEnvironment? environment = null,
        FakeLogger? logger = null)
    {
        InMemoryTransportOptions o = options ?? new InMemoryConfigurator().Build();
        ExchangeRegistry? r = adapterActive ? registry ?? InMemoryTopologyInterpreter.BuildRegistry(o) : null;
        return new InMemoryStartupDiagnostics(o, r, outbox, environment, logger ?? new FakeLogger());
    }

    /// <summary>Fanout exchange "events" bound to queue "billing", which has a registered consumer.</summary>
    private static (InMemoryTransportOptions Options, ExchangeRegistry Registry) BuildFanOutConsumerTransport()
    {
        var c = new InMemoryConfigurator();
        c.ConfigureTopology(t =>
        {
            t.DeclareExchange("events", ExchangeType.Fanout);
            t.DeclareQueue("billing");
            t.BindExchangeToQueue("events", "billing", "");
        });
        c.ReceiveEndpoint("billing", e => e.Consumer<TestConsumer, OrderCreated>());
        InMemoryTransportOptions o = c.Build();
        ExchangeRegistry r = InMemoryTopologyInterpreter.BuildRegistry(o);
        return (o, r);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Production", LogLevel.Warning)]
    [InlineData("Development", LogLevel.Information)]
    [InlineData("Staging", LogLevel.Information)]
    public async Task StartAsync_ByEnvironment_LogsGuaranteeNoticeAtExpectedLevel(string env, LogLevel expected)
    {
        IHostEnvironment host = Substitute.For<IHostEnvironment>();
        host.EnvironmentName.Returns(env);
        var logger = new FakeLogger();
        InMemoryStartupDiagnostics sut = Create(environment: host, logger: logger);

        await sut.StartAsync(TestContext.Current.CancellationToken);

        logger.Entries.Should().ContainSingle(e => e.EventId == 1).Which.Level.Should().Be(expected);
    }

    [Fact]
    public async Task StartAsync_NoHostEnvironment_LogsGuaranteeNoticeAsInformation()
    {
        var logger = new FakeLogger();
        InMemoryStartupDiagnostics sut = Create(environment: null, logger: logger);

        await sut.StartAsync(TestContext.Current.CancellationToken);

        logger.Entries.Should().ContainSingle(e => e.EventId == 1).Which.Level.Should().Be(LogLevel.Information);
    }

    [Fact]
    public async Task StartAsync_CalledTwice_LogsEachDiagnosticExactlyOnce()
    {
        (InMemoryTransportOptions options, ExchangeRegistry registry) = BuildFanOutConsumerTransport();
        var logger = new FakeLogger();
        InMemoryStartupDiagnostics sut = Create(
            options: options, registry: registry, outbox: new OutboxRegistrationState(true, false), logger: logger);

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await sut.StartAsync(TestContext.Current.CancellationToken);

        logger.Entries.Count(e => e.EventId == 1).Should().Be(1);
        logger.Entries.Count(e => e.EventId == 2).Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_GuaranteeNotice_MentionsAtMostOnceRestartLossFullQueueLossAndBroker()
    {
        var logger = new FakeLogger();
        InMemoryStartupDiagnostics sut = Create(logger: logger);

        await sut.StartAsync(TestContext.Current.CancellationToken);

        string message = logger.Entries.Single(e => e.EventId == 1).Message;
        message.Should().Contain("at-most-once").And.Contain("restart").And.Contain("full queue")
            .And.Contain("outbox").And.Contain("broker").And.Contain("single process");
    }

    [Fact]
    public async Task StartAsync_OutboxWithoutInboxAndFanoutConsumerQueue_WarnsNamingTheQueue()
    {
        (InMemoryTransportOptions options, ExchangeRegistry registry) = BuildFanOutConsumerTransport();
        var logger = new FakeLogger();
        InMemoryStartupDiagnostics sut = Create(
            options: options, registry: registry, outbox: new OutboxRegistrationState(true, false), logger: logger);

        await sut.StartAsync(TestContext.Current.CancellationToken);

        LogEntry warning = logger.Entries.Single(e => e.EventId == 2);
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().Contain("billing");
    }

    [Fact]
    public async Task StartAsync_OutboxWithInbox_DoesNotWarnAboutFanOut()
    {
        (InMemoryTransportOptions options, ExchangeRegistry registry) = BuildFanOutConsumerTransport();
        var logger = new FakeLogger();
        InMemoryStartupDiagnostics sut = Create(
            options: options, registry: registry, outbox: new OutboxRegistrationState(true, true), logger: logger);

        await sut.StartAsync(TestContext.Current.CancellationToken);

        logger.Entries.Should().NotContain(e => e.EventId == 2);
    }

    [Fact]
    public async Task StartAsync_NoOutbox_DoesNotWarnAboutFanOut()
    {
        (InMemoryTransportOptions options, ExchangeRegistry registry) = BuildFanOutConsumerTransport();
        var logger = new FakeLogger();
        InMemoryStartupDiagnostics sut = Create(
            options: options, registry: registry, outbox: new OutboxRegistrationState(false, false), logger: logger);

        await sut.StartAsync(TestContext.Current.CancellationToken);

        logger.Entries.Should().NotContain(e => e.EventId == 2);
    }

    [Fact]
    public async Task StartAsync_OutboxWithoutInboxOnlyDirectBoundQueues_DoesNotWarnAboutFanOut()
    {
        var c = new InMemoryConfigurator();
        c.ConfigureTopology(t =>
        {
            t.DeclareExchange("commands", ExchangeType.Direct);
            t.DeclareQueue("billing");
            t.BindExchangeToQueue("commands", "billing", "key");
        });
        c.ReceiveEndpoint("billing", e => e.Consumer<TestConsumer, OrderCreated>());
        InMemoryTransportOptions options = c.Build();
        ExchangeRegistry registry = InMemoryTopologyInterpreter.BuildRegistry(options);

        var logger = new FakeLogger();
        InMemoryStartupDiagnostics sut = Create(
            options: options, registry: registry, outbox: new OutboxRegistrationState(true, false), logger: logger);

        await sut.StartAsync(TestContext.Current.CancellationToken);

        logger.Entries.Should().NotContain(e => e.EventId == 2);
    }

    [Fact]
    public async Task StartAsync_InMemoryAdapterNotActive_LogsNothing()
    {
        var logger = new FakeLogger();
        InMemoryStartupDiagnostics sut = Create(adapterActive: false, logger: logger);

        await sut.StartAsync(TestContext.Current.CancellationToken);

        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public void AddBareWireInMemory_CalledTwice_RegistersSingleStartupDiagnosticsHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBareWireInMemory(_ => { });
        services.AddBareWireInMemory(_ => { });

        using ServiceProvider sp = services.BuildServiceProvider();

        sp.GetServices<IHostedService>().OfType<InMemoryStartupDiagnostics>().Should().ContainSingle();
    }

    [Fact]
    public async Task AddBareWireInMemory_OutboxStoreRegisteredAfterTransportWithoutInbox_WarnsAtStartup()
    {
        var services = new ServiceCollection();
        var provider = new CapturingLoggerProvider();
        services.AddLogging(b => b.AddProvider(provider));
        services.AddBareWireInMemory(t =>
        {
            t.ConfigureTopology(topo =>
            {
                topo.DeclareExchange("events", ExchangeType.Fanout);
                topo.DeclareQueue("billing");
                topo.BindExchangeToQueue("events", "billing", "");
            });
            t.ReceiveEndpoint("billing", e => e.Consumer<TestConsumer, OrderCreated>());
        });

        // Registered AFTER AddBareWireInMemory, on purpose — the probe must read the collection at the
        // moment the hosted service is resolved, not at AddBareWireInMemory call time.
        services.AddSingleton(typeof(BareWire.Outbox.IOutboxStore), _ => Substitute.For<BareWire.Outbox.IOutboxStore>());

        using ServiceProvider sp = services.BuildServiceProvider();
        InMemoryStartupDiagnostics hosted = sp.GetServices<IHostedService>().OfType<InMemoryStartupDiagnostics>().Single();

        await hosted.StartAsync(TestContext.Current.CancellationToken);

        provider.Entries.Should().ContainSingle(e => e.EventId == 2 && e.Message.Contains("billing"));
    }

    [Fact]
    public async Task AddBareWireInMemory_NoHostEnvironmentRegistered_StartsAndLogsInformation()
    {
        var services = new ServiceCollection();
        var provider = new CapturingLoggerProvider();
        services.AddLogging(b => b.AddProvider(provider));
        services.AddBareWireInMemory(_ => { });

        using ServiceProvider sp = services.BuildServiceProvider();
        InMemoryStartupDiagnostics hosted = sp.GetServices<IHostedService>().OfType<InMemoryStartupDiagnostics>().Single();

        await hosted.StartAsync(TestContext.Current.CancellationToken);

        provider.Entries.Should().ContainSingle(e => e.EventId == 1 && e.Level == LogLevel.Information);
    }
}
