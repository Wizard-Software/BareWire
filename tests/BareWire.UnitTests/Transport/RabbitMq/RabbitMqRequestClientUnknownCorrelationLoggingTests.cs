using System.Buffers;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// Unit tests for task 14.13 — demote the "unknown correlation" log to <see cref="LogLevel.Debug"/>
/// (no warn-spam under competing-responders) and the optional, dimensionless
/// <c>barewire.responses.dropped_as_late</c> counter. All tests are broker-free and exercise
/// <c>TryResolvePending</c> directly. Acceptance criteria (ADR-027 Enforcement):
/// (a) log level Debug/Trace, not Warning; (b) no correlation-id / exchange dimension on the metric.
/// </summary>
public sealed class RabbitMqRequestClientUnknownCorrelationLoggingTests
{
    // ── Test records ───────────────────────────────────────────────────────────

    private sealed record TestRequest(string Value);

    private static readonly Uri FakeConnectionUri = new("amqp://localhost");

    private const string MassTransitContentType = "application/vnd.masstransit+json";

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal logger that captures emitted log events for level assertions.
    /// Mirrors the pattern in <c>OutboxDialectMismatchWarningTests</c>.
    /// </summary>
    private sealed class FakeLogger : ILogger
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

    /// <summary>
    /// Captures all <see cref="long"/> measurements emitted on the "BareWire" meter so tests can
    /// assert both the value and the (expected empty) tag set.
    /// </summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly MeterListener _listener;

        public List<(string InstrumentName, long Value, KeyValuePair<string, object?>[] Tags)> Measurements { get; } = [];

        public MetricCapture()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == "BareWire")
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };

            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
                Measurements.Add((instrument.Name, measurement, tags.ToArray())));

            _listener.Start();
        }

        public long Sum(string instrumentName) =>
            Measurements.Where(m => m.InstrumentName == instrumentName).Sum(m => m.Value);

        public void Dispose() => _listener.Dispose();
    }

    private static RabbitMqRequestClient<TestRequest> CreateClient(
        ILogger? logger = null,
        IDeserializerResolver? deserializerResolver = null,
        Meter? meter = null)
        => new(
            connection: Substitute.For<IConnection>(),
            serializer: Substitute.For<IMessageSerializer>(),
            deserializerResolver: deserializerResolver ?? Substitute.For<IDeserializerResolver>(),
            logger: logger ?? NullLogger.Instance,
            targetExchange: string.Empty,
            routingKey: "test-queue",
            timeout: TimeSpan.FromSeconds(30),
            connectionUri: FakeConnectionUri,
            vhost: null,
            meter: meter);

    /// <summary>
    /// Builds a deserializer resolver whose resolved deserializer reads a MassTransit envelope
    /// request-id that is never registered in <c>_pending</c> — i.e. the Stage-2 "unknown correlation"
    /// discard path that calls <c>LogUnknownCorrelationId</c>.
    /// </summary>
    private static IDeserializerResolver BuildMtUnknownCorrelationResolver()
    {
        var deserializer = Substitute.For<IMessageDeserializer, IResponseEnvelopeReader>();
        ((IResponseEnvelopeReader)deserializer)
            .TryReadRequestId(Arg.Any<ReadOnlySequence<byte>>(), out Arg.Any<Guid>())
            .Returns(call =>
            {
                call[1] = Guid.NewGuid(); // an id that is NOT in _pending
                return true;
            });

        var resolver = Substitute.For<IDeserializerResolver>();
        resolver.Resolve(MassTransitContentType).Returns(deserializer);
        return resolver;
    }

    // ── (a) Log level demoted to Debug, not Warning ────────────────────────────

    [Fact]
    public void OnUnknownCorrelation_MtPath_LogsAtDebugLevel_NotWarning()
    {
        // Arrange — MT response carrying an unregistered requestId reaches the Stage-2 discard path.
        var logger = new FakeLogger();
        var client = CreateClient(logger: logger, deserializerResolver: BuildMtUnknownCorrelationResolver());

        // Act — non-empty body forces the envelope reader; contentType selects the MT branch.
        bool resolved = client.TryResolvePending(
            amqpCorrelationId: null,
            contentType: MassTransitContentType,
            body: new ReadOnlySequence<byte>(new byte[] { 0x7B, 0x7D }),
            out TaskCompletionSource<InboundMessage>? result);

        // Assert
        resolved.Should().BeFalse();
        ((object?)result).Should().BeNull();
        logger.Events.Should().ContainSingle(e => e.Level == LogLevel.Debug,
            "a late/duplicate response under competing-responders is expected, not a Warning (ADR-027 D7)");
        logger.Events.Should().NotContain(e => e.Level == LogLevel.Warning,
            "demoting the log removes warn-spam under competing-responders");
    }

    // ── (b) Metric has no correlation-id / exchange dimension ──────────────────

    [Fact]
    public void UnknownCorrelationMetric_HasNoCorrelationOrExchangeDimension()
    {
        // Arrange
        using var capture = new MetricCapture();
        using var meter = new Meter("BareWire");
        var client = CreateClient(deserializerResolver: BuildMtUnknownCorrelationResolver(), meter: meter);

        // Act — drive one unknown-correlation discard on the MT path.
        client.TryResolvePending(
            amqpCorrelationId: null,
            contentType: MassTransitContentType,
            body: new ReadOnlySequence<byte>(new byte[] { 0x7B, 0x7D }),
            out _);

        // Assert — the counter fired exactly once and with ZERO tags (SEC S1: no correlation/exchange label).
        capture.Sum("barewire.responses.dropped_as_late").Should().Be(1);
        capture.Measurements
            .Where(m => m.InstrumentName == "barewire.responses.dropped_as_late")
            .Should().OnlyContain(m => m.Tags.Length == 0,
                "the counter must be dimensionless — no correlation-id or exchange name as a tag (ADR-027 D7(b))");
    }

    [Fact]
    public void UnknownCorrelationLogMessage_DoesNotEmbedCorrelationId()
    {
        // Arrange
        var logger = new FakeLogger();
        var client = CreateClient(logger: logger, deserializerResolver: BuildMtUnknownCorrelationResolver());

        // Act
        client.TryResolvePending(
            amqpCorrelationId: null,
            contentType: MassTransitContentType,
            body: new ReadOnlySequence<byte>(new byte[] { 0x7B, 0x7D }),
            out _);

        // Assert — the demoted message must NOT contain a GUID/correlation-id (ADR-027 D7(a): "or in the log").
        string message = logger.Events.Single().Message;
        message.Should().NotMatchRegex(
            "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
            "the correlation-id GUID must not appear in the log (ADR-027 D7(a))");
    }

    // ── (D-2) Raw BareWire↔BareWire competing-responders miss is counted ───────

    [Fact]
    public void OnUnknownCorrelation_RawStage1Miss_IncrementsCounter_ByOne()
    {
        // Arrange — empty _pending; a Guid-shaped AMQP CorrelationId with no MT content-type
        // is the canonical raw competing-responders late/duplicate response (ADR-027 D8(a)).
        using var capture = new MetricCapture();
        using var meter = new Meter("BareWire");
        var client = CreateClient(meter: meter);

        // Act
        bool resolved = client.TryResolvePending(
            amqpCorrelationId: Guid.NewGuid().ToString(),
            contentType: null,
            body: ReadOnlySequence<byte>.Empty,
            out TaskCompletionSource<InboundMessage>? result);

        // Assert
        resolved.Should().BeFalse();
        ((object?)result).Should().BeNull();
        capture.Sum("barewire.responses.dropped_as_late").Should().Be(1,
            "the raw Stage-1 miss is the primary competing-responders scenario and must be observed (D-2)");
    }

    [Fact]
    public void OnNonCorrelatedMiss_WithoutGuidShapedCorrelationId_DoesNotIncrementCounter()
    {
        // Arrange — a response with neither a Guid-shaped AMQP CorrelationId nor an MT envelope is
        // not a correlated-but-late response; it must NOT inflate the late-drop counter.
        using var capture = new MetricCapture();
        using var meter = new Meter("BareWire");
        var client = CreateClient(meter: meter);

        // Act
        bool resolved = client.TryResolvePending(
            amqpCorrelationId: "not-a-guid",
            contentType: null,
            body: ReadOnlySequence<byte>.Empty,
            out _);

        // Assert
        resolved.Should().BeFalse();
        capture.Sum("barewire.responses.dropped_as_late").Should().Be(0,
            "only a Guid-shaped (correlated) late/duplicate response counts as dropped-as-late");
    }

    // ── (opt-in) Counter is optional — no Meter means no-op, no throw ──────────

    [Fact]
    public void Constructor_WithoutMeter_RawMissIsNoOp_AndDoesNotThrow()
    {
        // Arrange — no Meter supplied → counter is null (observability OFF).
        var client = CreateClient(meter: null);

        // Act
        Action act = () => client.TryResolvePending(
            amqpCorrelationId: Guid.NewGuid().ToString(),
            contentType: null,
            body: ReadOnlySequence<byte>.Empty,
            out _);

        // Assert — the null-conditional Add(1) must be a clean no-op.
        act.Should().NotThrow("the counter is opt-in; without a Meter the drop path must not allocate or throw");
    }
}
