using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Configuration;
using Microsoft.Extensions.Logging;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// 16.8 — Divergent-double-write diagnostic. When the SAME type <c>T</c> receives a DIFFERENT exchange
/// or routing key from two registration paths, the config-time <see cref="PublishRegistry"/> records a
/// divergence; <c>Build()</c> snapshots it onto the options; and the transport adapter emits a
/// DEFAULT-ON warning at startup. Last-call-wins still applies (no exception). Idempotent re-writes of
/// the SAME value stay silent. Cost is config-time only.
/// </summary>
public sealed class PublishRoutingDivergenceTests
{
    private sealed record Foo(string Value);
    private sealed record Bar(string Value);

    private static RabbitMqConfigurator CreateConfigurator()
    {
        var configurator = new RabbitMqConfigurator();
        configurator.Host("amqp://guest:guest@localhost:5672/");
        return configurator;
    }

    /// <summary>Minimal logger that captures emitted log events for assertion.</summary>
    private sealed class FakeLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Events.Add((logLevel, formatter(state, exception)));
    }

    // ── Registry: divergent exchange double-write records a divergence; last-call-wins value kept ─

    [Fact]
    public void MapExchange_DivergentDoubleWrite_RecordsDivergence_AndKeepsLastValue()
    {
        // Arrange
        var registry = new PublishRegistry();

        // Act — two DIFFERENT exchanges for the same type.
        registry.MapExchange(typeof(Foo), "a");
        registry.MapExchange(typeof(Foo), "b");

        // Assert — one divergence recorded; last-call-wins value unchanged; no exception thrown.
        registry.Divergences.Should().ContainSingle();
        PublishRoutingDivergence divergence = registry.Divergences[0];
        divergence.Dimension.Should().Be(PublishRoutingDimension.Exchange);
        divergence.MessageType.Should().Be<Foo>();
        divergence.PreviousValue.Should().Be("a");
        divergence.NewValue.Should().Be("b");
        registry.ExchangeMappings[typeof(Foo)].Should().Be("b");
    }

    // ── Registry: idempotent exchange re-write is SILENT ──────────────────────────────────────

    [Fact]
    public void MapExchange_IdempotentDoubleWrite_RecordsNoDivergence()
    {
        // Arrange
        var registry = new PublishRegistry();

        // Act — SAME exchange written twice.
        registry.MapExchange(typeof(Foo), "a");
        registry.MapExchange(typeof(Foo), "a");

        // Assert
        registry.Divergences.Should().BeEmpty();
        registry.ExchangeMappings[typeof(Foo)].Should().Be("a");
    }

    // ── Registry: divergent routing-key double-write records a divergence ─────────────────────

    [Fact]
    public void MapRoutingKey_DivergentDoubleWrite_RecordsDivergence_AndKeepsLastValue()
    {
        // Arrange
        var registry = new PublishRegistry();

        // Act
        registry.MapRoutingKey(typeof(Foo), "rk-a");
        registry.MapRoutingKey(typeof(Foo), "rk-b");

        // Assert
        registry.Divergences.Should().ContainSingle();
        PublishRoutingDivergence divergence = registry.Divergences[0];
        divergence.Dimension.Should().Be(PublishRoutingDimension.RoutingKey);
        divergence.PreviousValue.Should().Be("rk-a");
        divergence.NewValue.Should().Be("rk-b");
        registry.RoutingKeyMappings[typeof(Foo)].Should().Be("rk-b");
    }

    // ── Registry: idempotent routing-key re-write is SILENT ───────────────────────────────────

    [Fact]
    public void MapRoutingKey_IdempotentDoubleWrite_RecordsNoDivergence()
    {
        // Arrange
        var registry = new PublishRegistry();

        // Act
        registry.MapRoutingKey(typeof(Foo), "rk-a");
        registry.MapRoutingKey(typeof(Foo), "rk-a");

        // Assert
        registry.Divergences.Should().BeEmpty();
    }

    // ── Build(): divergence across registration paths is snapshotted onto the options ─────────

    [Fact]
    public void Build_DivergentExchangeAcrossPaths_SnapshotsDivergence_AndKeepsLastCallWins()
    {
        // Arrange — DeclareExchange<Foo>("a") then Publish<Foo>(Exchange("b")); both declared.
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t =>
        {
            t.DeclareExchange("b", ExchangeType.Topic);
            t.DeclareExchange<Foo>("a", ExchangeType.Topic);
        });
        configurator.Publish<Foo>(p => p.Exchange("b"));

        // Act — must NOT throw (non-breaking, last-call-wins).
        RabbitMqTransportOptions options = configurator.Build();

        // Assert
        options.PublishRoutingDivergences.Should().NotBeNull();
        options.PublishRoutingDivergences!.Should().ContainSingle(d =>
            d.MessageType == typeof(Foo) &&
            d.Dimension == PublishRoutingDimension.Exchange &&
            d.PreviousValue == "a" &&
            d.NewValue == "b");
        options.ExchangeMappings![typeof(Foo)].Should().Be("b");
    }

    // ── Build(): idempotent registration across paths produces NO divergence snapshot ─────────

    [Fact]
    public void Build_IdempotentExchangeAcrossPaths_ProducesNoDivergence()
    {
        // Arrange — DeclareExchange<Foo>("a") then Publish<Foo>(Exchange("a")) — same value.
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t => t.DeclareExchange<Foo>("a", ExchangeType.Topic));
        configurator.Publish<Foo>(p => p.Exchange("a"));

        // Act
        RabbitMqTransportOptions options = configurator.Build();

        // Assert — no divergence surfaced.
        options.PublishRoutingDivergences.Should().BeNull();
    }

    // ── Adapter: emits exactly one DEFAULT-ON warning per divergence at startup ────────────────

    [Fact]
    public void TransportAdapter_WithDivergence_EmitsWarningAtStartup()
    {
        // Arrange — produce options carrying one exchange divergence (Foo: "a" → "b").
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t =>
        {
            t.DeclareExchange("b", ExchangeType.Topic);
            t.DeclareExchange<Foo>("a", ExchangeType.Topic);
        });
        configurator.Publish<Foo>(p => p.Exchange("b"));
        RabbitMqTransportOptions options = configurator.Build();

        var logger = new FakeLogger<RabbitMqTransportAdapter>();

        // Act — constructing the adapter is the bus-startup moment; no broker connection needed.
        _ = new RabbitMqTransportAdapter(options, logger);

        // Assert — exactly one warning, naming the type and both values; DEFAULT-ON, no extra config.
        logger.Events.Should().ContainSingle(e => e.Level == LogLevel.Warning);
        string warning = logger.Events.Single(e => e.Level == LogLevel.Warning).Message;
        warning.Should().Contain(typeof(Foo).FullName!);
        warning.Should().Contain("'a'");
        warning.Should().Contain("'b'");
    }

    // ── Adapter: no divergence → no warning emitted ───────────────────────────────────────────

    [Fact]
    public void TransportAdapter_WithoutDivergence_EmitsNoWarning()
    {
        // Arrange — a single, consistent mapping → no divergence snapshot.
        RabbitMqConfigurator configurator = CreateConfigurator();
        configurator.ConfigureTopology(t => t.DeclareExchange<Bar>("orders", ExchangeType.Topic));
        RabbitMqTransportOptions options = configurator.Build();
        options.PublishRoutingDivergences.Should().BeNull(); // precondition

        var logger = new FakeLogger<RabbitMqTransportAdapter>();

        // Act
        _ = new RabbitMqTransportAdapter(options, logger);

        // Assert — no warning logged at construction.
        logger.Events.Should().NotContain(e => e.Level == LogLevel.Warning);
    }
}
