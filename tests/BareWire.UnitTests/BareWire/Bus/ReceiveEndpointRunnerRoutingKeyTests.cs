using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AwesomeAssertions;
using BareWire;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Pipeline;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.FlowControl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Core.Bus;

// Single message type shared by several consumers — exercises consume-time routing-key selection
// among multiple consumers of the SAME type on one queue (ADR-030 D4 layers 1-2).
// Must be public so ConsumerInvokerFactory and GetService<T>() can resolve them.
public sealed record TransferInitiated(string CorrelationId);

public sealed class RegionEuConsumer(List<string> log) : IConsumer<TransferInitiated>
{
    public Task ConsumeAsync(ConsumeContext<TransferInitiated> context)
    {
        log.Add("RegionEu");
        return Task.CompletedTask;
    }
}

public sealed class RegionAllConsumer(List<string> log) : IConsumer<TransferInitiated>
{
    public Task ConsumeAsync(ConsumeContext<TransferInitiated> context)
    {
        log.Add("RegionAll");
        return Task.CompletedTask;
    }
}

public sealed class CatchAllTransferConsumer(List<string> log) : IConsumer<TransferInitiated>
{
    public Task ConsumeAsync(ConsumeContext<TransferInitiated> context)
    {
        log.Add("CatchAll");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Minimal <see cref="ILogger"/> that records every emitted entry so tests can assert that a
/// specific warning fired (and that the raw routing key never appears in any message).
/// </summary>
internal sealed class CapturingLogger : ILogger
{
    public List<(LogLevel Level, EventId EventId, string Message)> Entries { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, eventId, formatter(state, exception)));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
/// Tests for the consume-time routing-key dispatch rewrite (ADR-030 D4 layers 1-2 + ADR-FIX-2 guard)
/// in <see cref="ReceiveEndpointRunner.DispatchMessageAsync"/>.
/// </summary>
public sealed class ReceiveEndpointRunnerRoutingKeyTests
{
    private const string EndpointName = "routing-key-test-endpoint";

    // ── Layer 1: most-specific-wins ──────────────────────────────────────────────

    [Fact]
    public async Task DispatchMessageAsync_TypeResolvedMultiplePatternsMatch_SelectsMostSpecificConsumer()
    {
        // Arrange — two consumers of the SAME type; "transfer.eu.*" is more specific than "transfer.#".
        List<string> log = [];
        var (runner, writer) = CreateRunner(
            [
                new ConsumerRegistration(typeof(RegionAllConsumer), typeof(TransferInitiated), ["transfer.#"]),
                new ConsumerRegistration(typeof(RegionEuConsumer), typeof(TransferInitiated), ["transfer.eu.*"]),
            ],
            new Dictionary<Type, object>
            {
                [typeof(RegionAllConsumer)] = new RegionAllConsumer(log),
                [typeof(RegionEuConsumer)] = new RegionEuConsumer(log),
            },
            NullLogger<ReceiveEndpointRunner>.Instance);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(
            MakeMessage(new()
            {
                ["BW-MessageType"] = nameof(TransferInitiated),
                ["BW-RoutingKey"] = "transfer.eu.payment",
            }),
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — only the most specific consumer fires.
        log.Should().ContainSingle().Which.Should().Be("RegionEu");
    }

    // ── Layer 2: catch-all takes over when no pattern matches ────────────────────

    [Fact]
    public async Task DispatchMessageAsync_TypeResolvedNoPatternMatch_CatchAllConsumerWins()
    {
        // Arrange — a pattern consumer ("orders.*") plus a catch-all (no patterns) of the same type.
        List<string> log = [];
        var (runner, writer) = CreateRunner(
            [
                new ConsumerRegistration(typeof(RegionEuConsumer), typeof(TransferInitiated), ["orders.*"]),
                new ConsumerRegistration(typeof(CatchAllTransferConsumer), typeof(TransferInitiated)),
            ],
            new Dictionary<Type, object>
            {
                [typeof(RegionEuConsumer)] = new RegionEuConsumer(log),
                [typeof(CatchAllTransferConsumer)] = new CatchAllTransferConsumer(log),
            },
            NullLogger<ReceiveEndpointRunner>.Instance);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(
            MakeMessage(new()
            {
                ["BW-MessageType"] = nameof(TransferInitiated),
                ["BW-RoutingKey"] = "transfer.eu", // does NOT match "orders.*"
            }),
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — the catch-all handles it; the pattern consumer does not.
        log.Should().ContainSingle().Which.Should().Be("CatchAll");
    }

    [Fact]
    public async Task DispatchMessageAsync_RoutingKeyNoMatchNoCatchAll_WarnsAndDoesNotLeakRawKey()
    {
        // Arrange — only a pattern consumer, no catch-all; routing key matches nothing.
        const string sensitiveKey = "sensitive.unmatched.key";
        List<string> log = [];
        CapturingLogger logger = new();
        var (runner, writer) = CreateRunner(
            [new ConsumerRegistration(typeof(RegionEuConsumer), typeof(TransferInitiated), ["orders.*"])],
            new Dictionary<Type, object> { [typeof(RegionEuConsumer)] = new RegionEuConsumer(log) },
            logger);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(
            MakeMessage(new()
            {
                ["BW-MessageType"] = nameof(TransferInitiated),
                ["BW-RoutingKey"] = sensitiveKey,
            }),
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — nothing dispatched, a "no routing-key pattern matched" warning fired …
        log.Should().BeEmpty();
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("No routing-key pattern matched"));

        // … and the raw, producer-controlled routing key never appears in any log message (ADR-030 §Security).
        logger.Entries.Should().NotContain(e => e.Message.Contains(sensitiveKey));
    }

    [Fact]
    public async Task DispatchMessageAsync_AmbiguousSpecificityTie_SelectsFirstRegisteredAndWarns()
    {
        // Arrange — two consumers with the SAME pattern → unresolvable specificity tie.
        List<string> log = [];
        CapturingLogger logger = new();
        var (runner, writer) = CreateRunner(
            [
                new ConsumerRegistration(typeof(RegionEuConsumer), typeof(TransferInitiated), ["region.*"]),
                new ConsumerRegistration(typeof(RegionAllConsumer), typeof(TransferInitiated), ["region.*"]),
            ],
            new Dictionary<Type, object>
            {
                [typeof(RegionEuConsumer)] = new RegionEuConsumer(log),
                [typeof(RegionAllConsumer)] = new RegionAllConsumer(log),
            },
            logger);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(
            MakeMessage(new()
            {
                ["BW-MessageType"] = nameof(TransferInitiated),
                ["BW-RoutingKey"] = "region.eu",
            }),
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — first-registered wins, and an ambiguity warning fired.
        log.Should().ContainSingle().Which.Should().Be("RegionEu");
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("Ambiguous routing-key match"));
    }

    // ── Guard (ADR-FIX-2): bit-identical legacy path when no patterns / no AcceptUntyped ──

    [Fact]
    public async Task DispatchMessageAsync_NoPatternsNoAcceptUntyped_BwMessageTypePresent_BitIdenticalFastPath()
    {
        // Arrange — no patterns, no AcceptUntyped → legacy fast path; the routing key is ignored.
        List<string> log = [];
        var (runner, writer) = CreateRunner(
            [new ConsumerRegistration(typeof(RegionEuConsumer), typeof(TransferInitiated))],
            new Dictionary<Type, object> { [typeof(RegionEuConsumer)] = new RegionEuConsumer(log) },
            NullLogger<ReceiveEndpointRunner>.Instance);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(
            MakeMessage(new()
            {
                ["BW-MessageType"] = nameof(TransferInitiated),
                ["BW-RoutingKey"] = "anything.at.all", // ignored on the legacy path
            }),
            cts.Token);
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — type-name fast path dispatches regardless of routing key.
        log.Should().ContainSingle().Which.Should().Be("RegionEu");
    }

    [Fact]
    public async Task DispatchMessageAsync_NoPatternsNoAcceptUntyped_NoBwMessageType_BitIdenticalBlindFallback()
    {
        // Arrange — no patterns, no AcceptUntyped, no BW-MessageType → legacy blind sequential-deserialize.
        List<string> log = [];
        var (runner, writer) = CreateRunner(
            [new ConsumerRegistration(typeof(RegionEuConsumer), typeof(TransferInitiated))],
            new Dictionary<Type, object> { [typeof(RegionEuConsumer)] = new RegionEuConsumer(log) },
            NullLogger<ReceiveEndpointRunner>.Instance);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(MakeMessage([]), cts.Token); // no headers at all
        writer.Complete();

        // Act
        await runner.RunAsync(cts.Token);

        // Assert — blind fallback deserializes and dispatches the (only) consumer.
        log.Should().ContainSingle().Which.Should().Be("RegionEu");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static (ReceiveEndpointRunner Runner, ChannelWriter<InboundMessage> Writer) CreateRunner(
        IReadOnlyList<ConsumerRegistration> consumers,
        IReadOnlyDictionary<Type, object> instances,
        ILogger logger)
    {
        Channel<InboundMessage> channel = Channel.CreateBounded<InboundMessage>(
            new BoundedChannelOptions(64) { SingleWriter = false, SingleReader = true });

        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("test");
        adapter.ConsumeAsync(
                Arg.Any<string>(),
                Arg.Any<FlowControlOptions>(),
                Arg.Any<CancellationToken>())
               .Returns(callInfo => ReadChannelAsync(channel.Reader, callInfo.ArgAt<CancellationToken>(2)));
        adapter.SettleAsync(
                Arg.Any<SettlementAction>(),
                Arg.Any<InboundMessage>(),
                Arg.Any<CancellationToken>())
               .Returns(Task.CompletedTask);

        // The deserializer returns a valid TransferInitiated so the selected invoker succeeds (and the
        // blind-fallback path also resolves on the legacy guard test).
        IMessageDeserializer deserializer = Substitute.For<IMessageDeserializer>();
        deserializer.ContentType.Returns("application/json");
        deserializer.Deserialize<TransferInitiated>(Arg.Any<ReadOnlySequence<byte>>())
                    .Returns(new TransferInitiated("corr-1"));
        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();
        deserializerResolver.Resolve(Arg.Any<string?>()).Returns(deserializer);

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        IServiceScope scope = Substitute.For<IServiceScope>();
        IServiceProvider provider = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(provider);
        foreach (KeyValuePair<Type, object> kv in instances)
        {
            provider.GetService(kv.Key).Returns(kv.Value);
        }

        provider.GetService(typeof(IEnumerable<IMessageMiddleware>))
            .Returns(Array.Empty<IMessageMiddleware>());

        FlowController flowController = new(NullLogger<FlowController>.Instance);

        EndpointBinding binding = new()
        {
            EndpointName = EndpointName,
            PrefetchCount = 4,
            Consumers = consumers,
            RawConsumers = [],
        };

        ReceiveEndpointRunner runner = new(
            binding,
            adapter,
            deserializerResolver,
            Substitute.For<IPublishEndpoint>(),
            Substitute.For<ISendEndpointProvider>(),
            scopeFactory,
            flowController,
            new NullInstrumentation(),
            logger,
            loggerFactory: NullLoggerFactory.Instance);

        return (runner, channel.Writer);
    }

    private static InboundMessage MakeMessage(Dictionary<string, string> headers, string id = "msg-1")
    {
        byte[] body = """{"CorrelationId":"corr-1"}"""u8.ToArray();
        return new InboundMessage(
            messageId: id,
            headers: headers,
            body: new ReadOnlySequence<byte>(body),
            deliveryTag: 1UL);
    }

    private static async IAsyncEnumerable<InboundMessage> ReadChannelAsync(
        ChannelReader<InboundMessage> reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (InboundMessage msg in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return msg;
        }
    }
}
