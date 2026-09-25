using System.Buffers;
using System.Text;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;

namespace BareWire.IntegrationTests.Transport.Parity;

/// <summary>
/// Abstract semantic-parity suite: one set of <c>ITransportAdapter</c>-level scenarios (routing,
/// settlement, unroutable handling, a full queue, header stamping) run against both the in-memory
/// transport and RabbitMQ. A derived class supplies <see cref="CreateAdapterAsync"/> (build a real
/// adapter from a transport-neutral <see cref="ParitySetup"/>) and <see cref="SkipReason"/> (the fixed
/// reason, or <see langword="null"/>, this transport skips one row of the "differences vs RabbitMQ"
/// table).
/// </summary>
/// <remarks>
/// <b>The rule this suite exists to enforce:</b> every new RabbitMQ behavior that has no in-memory
/// mirror must be added here — either as a parity scenario both transports run, or as a difference test
/// that asserts the RabbitMQ (reference) behavior and is skipped, with an explicit reason, on any
/// transport that cannot reproduce it. A behavior change that skips this suite entirely is a parity gap
/// no test will ever catch.
/// </remarks>
public abstract class TransportSemanticParityTests
{
    private const string ExchangeHeaderName = "BW-Exchange";
    private const string RoutingKeyHeaderName = "BW-RoutingKey";
    private const string PayloadContentType = "text/plain";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private readonly string _prefix = Guid.NewGuid().ToString("N");

    /// <summary>Builds a real adapter from a transport-neutral scenario description, with its topology already deployed.</summary>
    /// <param name="setup">The scenario's topology and transport options.</param>
    /// <param name="cancellationToken">A token bounding the whole scenario.</param>
    protected abstract Task<ITransportAdapter> CreateAdapterAsync(ParitySetup setup, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the fixed reason this transport skips <paramref name="difference"/>, or
    /// <see langword="null"/> when this transport is the one that runs (and asserts) that row of the
    /// "differences vs RabbitMQ" table.
    /// </summary>
    protected abstract string? SkipReason(ParityDifference difference);

    /// <summary>Publishes the D4 scenario's message, carrying a publisher-supplied <c>BW-Forged</c> header.</summary>
    /// <remarks>
    /// The default implementation goes through <see cref="ITransportAdapter.SendBatchAsync"/> like every
    /// other scenario. A transport whose header mapper strips or renames <c>BW-*</c> headers at publish
    /// time (RabbitMQ's does) overrides this hook to make the forged header actually reach the broker —
    /// for example by configuring the adapter with a header mapper that maps <c>BW-Forged</c> to a
    /// broker-native name, or by publishing with the raw client and letting the adapter consume it — so
    /// the difference test proves the real trust gap instead of a testing artifact.
    /// </remarks>
    protected virtual Task<IReadOnlyList<SendResult>> SendD4MessageAsync(
        ITransportAdapter adapter, string exchange, string routingKey, string payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        OutboundMessage message = new(
            routingKey,
            new Dictionary<string, string>
            {
                [ExchangeHeaderName] = exchange,
                ["BW-Forged"] = "forged-value",
            },
            Encoding.UTF8.GetBytes(payload),
            PayloadContentType);

        return adapter.SendBatchAsync([message], cancellationToken);
    }

    /// <summary>Builds a scenario-unique name: <c>parity-{guid}.{suffix}</c>.</summary>
    protected string Name(string suffix) => $"parity-{_prefix}.{suffix}";

    /// <summary>Builds an outbound message routed via <paramref name="exchange"/> (<c>""</c> = the default exchange).</summary>
    protected static OutboundMessage Message(string exchange, string routingKey, string payload) =>
        new(
            routingKey,
            new Dictionary<string, string> { [ExchangeHeaderName] = exchange },
            Encoding.UTF8.GetBytes(payload),
            PayloadContentType);

    /// <summary>Reads the UTF-8 body of a delivered message.</summary>
    protected static string Payload(InboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        ReadOnlySequence<byte> body = message.Body;
        if (body.IsSingleSegment)
        {
            return Encoding.UTF8.GetString(body.FirstSpan);
        }

        byte[] buffer = new byte[body.Length];
        int offset = 0;
        foreach (ReadOnlyMemory<byte> segment in body)
        {
            segment.Span.CopyTo(buffer.AsSpan(offset));
            offset += segment.Length;
        }

        return Encoding.UTF8.GetString(buffer);
    }

    /// <summary>Skips the current test with this transport's reason when it cannot reproduce <paramref name="difference"/>.</summary>
    protected void SkipIfDifferent(ParityDifference difference)
    {
        string? reason = SkipReason(difference);
        if (reason is not null)
        {
            Assert.Skip(reason);
        }
    }

    // ── Parity scenarios (P1-P16) — run, and asserted identically, on every transport ─────────────

    /// <summary>P1 — a direct exchange routes a message only to the queue bound on its exact routing key.</summary>
    [Fact]
    public async Task SendBatchAsync_DirectExchange_RoutesOnlyToQueueBoundWithExactKey()
    {
        using CancellationTokenSource cts = new(TestTimeout);
        string ex = Name("direct");
        string queueA = Name("a");
        string queueB = Name("b");

        ParitySetup setup = new(new TopologyDeclaration
        {
            Exchanges = [new ExchangeDeclaration(ex, ExchangeType.Direct, Durable: false)],
            Queues = [new QueueDeclaration(queueA, Durable: false), new QueueDeclaration(queueB, Durable: false)],
            ExchangeQueueBindings =
            [
                new ExchangeQueueBinding(ex, queueA, "a"),
                new ExchangeQueueBinding(ex, queueB, "b"),
            ],
        });

        ITransportAdapter adapter = await CreateAdapterAsync(setup, cts.Token);
        await using var _ = (IAsyncDisposable)adapter;
        await using ParityQueueReader readerA = ParityQueueReader.Open(adapter, queueA, cts.Token);
        await using ParityQueueReader readerB = ParityQueueReader.Open(adapter, queueB, cts.Token);

        IReadOnlyList<SendResult> results = await adapter.SendBatchAsync(
            [Message(ex, "a", "to-a"), Message(ex, "b", "marker")],
            cts.Token);
        results.Should().AllSatisfy(r => r.IsConfirmed.Should().BeTrue());

        using InboundMessage receivedA = await readerA.NextAsync();
        Payload(receivedA).Should().Be("to-a");
        await adapter.SettleAsync(SettlementAction.Ack, receivedA, cts.Token);

        using InboundMessage receivedB = await readerB.NextAsync();
        Payload(receivedB).Should().Be("marker", "queue b must never receive the message routed to queue a");
        await adapter.SettleAsync(SettlementAction.Ack, receivedB, cts.Token);
    }

    /// <summary>P2 — a fanout exchange delivers a copy of the message to every bound queue.</summary>
    [Fact]
    public async Task SendBatchAsync_FanoutExchange_DeliversCopyToEveryBoundQueue()
    {
        using CancellationTokenSource cts = new(TestTimeout);
        string ex = Name("fanout");
        string q1 = Name("q1");
        string q2 = Name("q2");

        ParitySetup setup = new(new TopologyDeclaration
        {
            Exchanges = [new ExchangeDeclaration(ex, ExchangeType.Fanout, Durable: false)],
            Queues = [new QueueDeclaration(q1, Durable: false), new QueueDeclaration(q2, Durable: false)],
            ExchangeQueueBindings =
            [
                new ExchangeQueueBinding(ex, q1, string.Empty),
                new ExchangeQueueBinding(ex, q2, string.Empty),
            ],
        });

        ITransportAdapter adapter = await CreateAdapterAsync(setup, cts.Token);
        await using var _ = (IAsyncDisposable)adapter;
        await using ParityQueueReader reader1 = ParityQueueReader.Open(adapter, q1, cts.Token);
        await using ParityQueueReader reader2 = ParityQueueReader.Open(adapter, q2, cts.Token);

        IReadOnlyList<SendResult> results =
            await adapter.SendBatchAsync([Message(ex, "any-key", "fanout-payload")], cts.Token);
        results.Should().AllSatisfy(r => r.IsConfirmed.Should().BeTrue());

        using InboundMessage m1 = await reader1.NextAsync();
        Payload(m1).Should().Be("fanout-payload");
        await adapter.SettleAsync(SettlementAction.Ack, m1, cts.Token);

        using InboundMessage m2 = await reader2.NextAsync();
        Payload(m2).Should().Be("fanout-payload");
        await adapter.SettleAsync(SettlementAction.Ack, m2, cts.Token);
    }

    /// <summary>P3 — a topic binding's <c>*</c> wildcard matches exactly one word.</summary>
    [Fact]
    public async Task SendBatchAsync_TopicStarWildcard_MatchesExactlyOneWord()
    {
        using CancellationTokenSource cts = new(TestTimeout);
        string ex = Name("topic-star");
        string q = Name("q");

        ParitySetup setup = new(new TopologyDeclaration
        {
            Exchanges = [new ExchangeDeclaration(ex, ExchangeType.Topic, Durable: false)],
            Queues = [new QueueDeclaration(q, Durable: false)],
            ExchangeQueueBindings = [new ExchangeQueueBinding(ex, q, "order.*")],
        });

        ITransportAdapter adapter = await CreateAdapterAsync(setup, cts.Token);
        await using var _ = (IAsyncDisposable)adapter;
        await using ParityQueueReader reader = ParityQueueReader.Open(adapter, q, cts.Token);

        IReadOnlyList<SendResult> results = await adapter.SendBatchAsync(
            [
                Message(ex, "order.created", "match"),
                Message(ex, "order.created.eu", "should-not-match"),
                Message(ex, "order.marker", "marker"),
            ],
            cts.Token);
        results.Should().AllSatisfy(r => r.IsConfirmed.Should().BeTrue());

        using InboundMessage first = await reader.NextAsync();
        Payload(first).Should().Be("match");
        await adapter.SettleAsync(SettlementAction.Ack, first, cts.Token);

        using InboundMessage second = await reader.NextAsync();
        Payload(second).Should().Be(
            "marker", "'order.*' matches exactly one word, so 'order.created.eu' must not arrive here");
        await adapter.SettleAsync(SettlementAction.Ack, second, cts.Token);
    }

    /// <summary>P4 — a topic binding's <c>#</c> wildcard matches zero or more words.</summary>
    [Fact]
    public async Task SendBatchAsync_TopicHashWildcard_MatchesZeroOrMoreWords()
    {
        using CancellationTokenSource cts = new(TestTimeout);
        string ex = Name("topic-hash");
        string q = Name("q");

        ParitySetup setup = new(new TopologyDeclaration
        {
            Exchanges = [new ExchangeDeclaration(ex, ExchangeType.Topic, Durable: false)],
            Queues = [new QueueDeclaration(q, Durable: false)],
            ExchangeQueueBindings = [new ExchangeQueueBinding(ex, q, "order.#")],
        });

        ITransportAdapter adapter = await CreateAdapterAsync(setup, cts.Token);
        await using var _ = (IAsyncDisposable)adapter;
        await using ParityQueueReader reader = ParityQueueReader.Open(adapter, q, cts.Token);

        IReadOnlyList<SendResult> results = await adapter.SendBatchAsync(
            [Message(ex, "order", "zero"), Message(ex, "order.created", "one"), Message(ex, "order.created.eu", "two")],
            cts.Token);
        results.Should().AllSatisfy(r => r.IsConfirmed.Should().BeTrue());

        using InboundMessage zero = await reader.NextAsync();
        Payload(zero).Should().Be("zero");
        await adapter.SettleAsync(SettlementAction.Ack, zero, cts.Token);

        using InboundMessage one = await reader.NextAsync();
        Payload(one).Should().Be("one");
        await adapter.SettleAsync(SettlementAction.Ack, one, cts.Token);

        using InboundMessage two = await reader.NextAsync();
        Payload(two).Should().Be("two");
        await adapter.SettleAsync(SettlementAction.Ack, two, cts.Token);
    }

    /// <summary>P5 — an exchange-to-exchange binding forwards a message end to end into a bound queue.</summary>
    [Fact]
    public async Task SendBatchAsync_ExchangeToExchangeBinding_DeliversEndToEnd()
    {
        using CancellationTokenSource cts = new(TestTimeout);
        string source = Name("source");
        string destination = Name("destination");
        string q = Name("q");
        const string routingKey = "route";

        ParitySetup setup = new(new TopologyDeclaration
        {
            Exchanges =
            [
                new ExchangeDeclaration(source, ExchangeType.Direct, Durable: false),
                new ExchangeDeclaration(destination, ExchangeType.Fanout, Durable: false),
            ],
            Queues = [new QueueDeclaration(q, Durable: false)],
            ExchangeExchangeBindings = [new ExchangeExchangeBinding(source, destination, routingKey)],
            ExchangeQueueBindings = [new ExchangeQueueBinding(destination, q, string.Empty)],
        });

        ITransportAdapter adapter = await CreateAdapterAsync(setup, cts.Token);
        await using var _ = (IAsyncDisposable)adapter;
        await using ParityQueueReader reader = ParityQueueReader.Open(adapter, q, cts.Token);

        IReadOnlyList<SendResult> results =
            await adapter.SendBatchAsync([Message(source, routingKey, "e2e")], cts.Token);
        results.Should().AllSatisfy(r => r.IsConfirmed.Should().BeTrue());

        using InboundMessage received = await reader.NextAsync();
        Payload(received).Should().Be("e2e");
        await adapter.SettleAsync(SettlementAction.Ack, received, cts.Token);
    }

    /// <summary>P6 — the default exchange (<c>""</c>) routes a message straight to the queue named by the routing key.</summary>
    [Fact]
    public async Task SendBatchAsync_DefaultExchange_RoutesByQueueName()
    {
        using CancellationTokenSource cts = new(TestTimeout);
        string q = Name("q");

        ParitySetup setup = new(new TopologyDeclaration { Queues = [new QueueDeclaration(q, Durable: false)] });

        ITransportAdapter adapter = await CreateAdapterAsync(setup, cts.Token);
        await using var _ = (IAsyncDisposable)adapter;
        await using ParityQueueReader reader = ParityQueueReader.Open(adapter, q, cts.Token);

        IReadOnlyList<SendResult> results =
            await adapter.SendBatchAsync([Message(string.Empty, q, "default-exchange")], cts.Token);
        results.Should().AllSatisfy(r => r.IsConfirmed.Should().BeTrue());

        using InboundMessage received = await reader.NextAsync();
        Payload(received).Should().Be("default-exchange");
        await adapter.SettleAsync(SettlementAction.Ack, received, cts.Token);
    }

    /// <summary>P7 — acknowledging a delivery permanently removes it from the queue.</summary>
    [Fact]
    public async Task SettleAsync_Ack_RemovesMessageFromQueue()
    {
        using CancellationTokenSource cts = new(TestTimeout);
        string q = Name("q");

        ParitySetup setup = new(new TopologyDeclaration { Queues = [new QueueDeclaration(q, Durable: false)] });

        ITransportAdapter adapter = await CreateAdapterAsync(setup, cts.Token);
        await using var _ = (IAsyncDisposable)adapter;
        await using ParityQueueReader reader = ParityQueueReader.Open(adapter, q, cts.Token);

        await adapter.SendBatchAsync(
            [Message(string.Empty, q, "first"), Message(string.Empty, q, "marker")], cts.Token);

        using InboundMessage first = await reader.NextAsync();
        Payload(first).Should().Be("first");
        await adapter.SettleAsync(SettlementAction.Ack, first, cts.Token);

        using InboundMessage second = await reader.NextAsync();
        Payload(second).Should().Be("marker", "Ack must permanently remove the first delivery, never redeliver it");
        await adapter.SettleAsync(SettlementAction.Ack, second, cts.Token);
    }

    /// <summary>P8 — a negatively acknowledged delivery is dead-lettered to the queue's dead-letter exchange.</summary>
    [Fact]
    public async Task SettleAsync_Nack_DeadLettersToDeadLetterExchange()
    {
        using CancellationTokenSource cts = new(TestTimeout);
        await RunDeadLetterScenarioAsync(SettlementAction.Nack, cts.Token);
    }

    /// <summary>P9 — a rejected delivery is dead-lettered to the queue's dead-letter exchange, same as <c>Nack</c>.</summary>
    [Fact]
    public async Task SettleAsync_Reject_DeadLettersToDeadLetterExchange()
    {
        using CancellationTokenSource cts = new(TestTimeout);
        await RunDeadLetterScenarioAsync(SettlementAction.Reject, cts.Token);
    }

    /// <summary>Shared body for P8/P9: dead-letters one delivery via <paramref name="action"/> and proves it left the source queue.</summary>
    private async Task RunDeadLetterScenarioAsync(SettlementAction action, CancellationToken cancellationToken)
    {
        string src = Name($"src-{action}");
        string dlx = Name($"dlx-{action}");
        string dlq = Name($"dlq-{action}");

        ParitySetup setup = new(new TopologyDeclaration
        {
            Exchanges = [new ExchangeDeclaration(dlx, ExchangeType.Fanout, Durable: false)],
            Queues =
            [
                new QueueDeclaration(src, Durable: false, Arguments: new Dictionary<string, object>
                {
                    ["x-dead-letter-exchange"] = dlx,
                }),
                new QueueDeclaration(dlq, Durable: false),
            ],
            ExchangeQueueBindings = [new ExchangeQueueBinding(dlx, dlq, string.Empty)],
        });

        ITransportAdapter adapter = await CreateAdapterAsync(setup, cancellationToken);
        await using var _ = (IAsyncDisposable)adapter;
        await using ParityQueueReader srcReader = ParityQueueReader.Open(adapter, src, cancellationToken);
        await using ParityQueueReader dlqReader = ParityQueueReader.Open(adapter, dlq, cancellationToken);

        await adapter.SendBatchAsync([Message(string.Empty, src, "victim")], cancellationToken);

        using InboundMessage victim = await srcReader.NextAsync();
        Payload(victim).Should().Be("victim");
        await adapter.SettleAsync(action, victim, cancellationToken);

        using InboundMessage deadLettered = await dlqReader.NextAsync();
        Payload(deadLettered).Should().Be("victim");
        await adapter.SettleAsync(SettlementAction.Ack, deadLettered, cancellationToken);

        await adapter.SendBatchAsync([Message(string.Empty, src, "marker")], cancellationToken);
        using InboundMessage marker = await srcReader.NextAsync();
        Payload(marker).Should().Be("marker", $"a {action} delivery must never redeliver on its source queue");
        await adapter.SettleAsync(SettlementAction.Ack, marker, cancellationToken);
    }

    /// <summary>P10 — requeuing a delivery redelivers the same content at the head of the source queue.</summary>
    [Fact]
    public async Task SettleAsync_Requeue_RedeliversSameMessageOnSourceQueue()
    {
        using CancellationTokenSource cts = new(TestTimeout);
        string q = Name("q");

        ParitySetup setup = new(new TopologyDeclaration { Queues = [new QueueDeclaration(q, Durable: false)] });

        ITransportAdapter adapter = await CreateAdapterAsync(setup, cts.Token);
        await using var _ = (IAsyncDisposable)adapter;
        await using ParityQueueReader reader = ParityQueueReader.Open(adapter, q, cts.Token);

        await adapter.SendBatchAsync([Message(string.Empty, q, "requeue-me")], cts.Token);

        using InboundMessage first = await reader.NextAsync();
        Payload(first).Should().Be("requeue-me");
        await adapter.SettleAsync(SettlementAction.Requeue, first, cts.Token);

        using InboundMessage redelivered = await reader.NextAsync();
        Payload(redelivered).Should().Be("requeue-me");
        await adapter.SettleAsync(SettlementAction.Ack, redelivered, cts.Token);
    }

    /// <summary>P11 — <c>Defer</c> without the opt-in throws, and the delivery stays settleable afterward.</summary>
    [Fact]
    public async Task SettleAsync_DeferWithoutOptIn_ThrowsNotSupportedException()
    {
        using CancellationTokenSource cts = new(TestTimeout);
        string q = Name("q");

        ParitySetup setup = new(new TopologyDeclaration { Queues = [new QueueDeclaration(q, Durable: false)] });

        ITransportAdapter adapter = await CreateAdapterAsync(setup, cts.Token);
        await using var _ = (IAsyncDisposable)adapter;
        await using ParityQueueReader reader = ParityQueueReader.Open(adapter, q, cts.Token);

        await adapter.SendBatchAsync([Message(string.Empty, q, "defer-me")], cts.Token);

        using InboundMessage received = await reader.NextAsync();
        Payload(received).Should().Be("defer-me");

        Func<Task> deferAttempt = () => adapter.SettleAsync(SettlementAction.Defer, received, cts.Token);
        await deferAttempt.Should().ThrowAsync<NotSupportedException>();

        // The delivery is still in flight after the rejected Defer attempt — it can still be settled.
        await adapter.SettleAsync(SettlementAction.Ack, received, cts.Token);

        await adapter.SendBatchAsync([Message(string.Empty, q, "marker")], cts.Token);
        using InboundMessage marker = await reader.NextAsync();
        Payload(marker).Should().Be("marker");
        await adapter.SettleAsync(SettlementAction.Ack, marker, cts.Token);
    }

    /// <summary>P12 — with the opt-in, <c>Defer</c> redelivers the same content on the source queue after a delay.</summary>
    [Fact]
    public async Task SettleAsync_DeferWithOptIn_RedeliversOnSourceQueueAfterDelay()
    {
        using CancellationTokenSource cts = new(TestTimeout);
        string q = Name("q");
        TimeSpan delay = TimeSpan.FromMilliseconds(200);

        ParitySetup setup = new(new TopologyDeclaration { Queues = [new QueueDeclaration(q, Durable: false)] })
        {
            Defer = new ParityDefer(q, delay),
        };

        ITransportAdapter adapter = await CreateAdapterAsync(setup, cts.Token);
        await using var _ = (IAsyncDisposable)adapter;
        await using ParityQueueReader reader = ParityQueueReader.Open(adapter, q, cts.Token);

        await adapter.SendBatchAsync([Message(string.Empty, q, "defer-me")], cts.Token);

        using InboundMessage received = await reader.NextAsync();
        Payload(received).Should().Be("defer-me");
        await adapter.SettleAsync(SettlementAction.Defer, received, cts.Token);

        using InboundMessage redelivered = await reader.NextAsync();
        Payload(redelivered).Should().Be("defer-me");
        await adapter.SettleAsync(SettlementAction.Ack, redelivered, cts.Token);
    }

    /// <summary>P13 — without guaranteed routing, an unroutable publish is still reported confirmed.</summary>
    [Fact]
    public async Task SendBatchAsync_UnroutableWithoutGuaranteedRouting_ReportsConfirmed()
    {
        using CancellationTokenSource cts = new(TestTimeout);
        string ex = Name("ex");

        ParitySetup setup = new(new TopologyDeclaration
        {
            Exchanges = [new ExchangeDeclaration(ex, ExchangeType.Direct, Durable: false)],
        });

        ITransportAdapter adapter = await CreateAdapterAsync(setup, cts.Token);
        await using var _ = (IAsyncDisposable)adapter;

        IReadOnlyList<SendResult> results =
            await adapter.SendBatchAsync([Message(ex, "no-such-binding", "unroutable")], cts.Token);
        results.Should().AllSatisfy(r => r.IsConfirmed.Should().BeTrue());
    }

    /// <summary>P14 — with guaranteed routing, an unroutable publish is reported not confirmed, but the rest of the batch is unaffected.</summary>
    [Fact]
    public async Task SendBatchAsync_UnroutableWithGuaranteedRouting_ReportsNotConfirmed()
    {
        using CancellationTokenSource cts = new(TestTimeout);
        string ex = Name("ex");
        string q = Name("q");
        const string boundKey = "bound";
        const string unboundKey = "unbound";

        ParitySetup setup = new(new TopologyDeclaration
        {
            Exchanges = [new ExchangeDeclaration(ex, ExchangeType.Direct, Durable: false)],
            Queues = [new QueueDeclaration(q, Durable: false)],
            ExchangeQueueBindings = [new ExchangeQueueBinding(ex, q, boundKey)],
        })
        {
            GuaranteedRouting = true,
        };

        ITransportAdapter adapter = await CreateAdapterAsync(setup, cts.Token);
        await using var _ = (IAsyncDisposable)adapter;
        await using ParityQueueReader reader = ParityQueueReader.Open(adapter, q, cts.Token);

        IReadOnlyList<SendResult> results = await adapter.SendBatchAsync(
            [Message(ex, unboundKey, "unroutable"), Message(ex, boundKey, "routable")],
            cts.Token);

        results.Should().HaveCount(2);
        results[0].IsConfirmed.Should().BeFalse("the routing key has no binding on the exchange");
        results[1].IsConfirmed.Should().BeTrue("the routing key is bound to a queue");

        using InboundMessage delivered = await reader.NextAsync();
        Payload(delivered).Should().Be("routable");
        await adapter.SettleAsync(SettlementAction.Ack, delivered, cts.Token);
    }

    /// <summary>
    /// P15 — a fan-out publish where one target queue is full reports not confirmed, but the queue with
    /// room still gets its copy; the full queue accepts new sends again once it is drained.
    /// </summary>
    [Fact]
    public async Task SendBatchAsync_FanoutWithOneFullQueue_ReportsNotConfirmedAndDeliversToQueuesWithRoom()
    {
        using CancellationTokenSource cts = new(TestTimeout);
        string full = Name("full");
        string roomy = Name("roomy");
        string fanoutEx = Name("fanout");

        ParitySetup setup = new(new TopologyDeclaration
        {
            Exchanges = [new ExchangeDeclaration(fanoutEx, ExchangeType.Fanout, Durable: false)],
            Queues = [new QueueDeclaration(full, Durable: false), new QueueDeclaration(roomy, Durable: false)],
            ExchangeQueueBindings =
            [
                new ExchangeQueueBinding(fanoutEx, full, string.Empty),
                new ExchangeQueueBinding(fanoutEx, roomy, string.Empty),
            ],
        })
        {
            BoundedQueues = new Dictionary<string, int> { [full] = 1 },
        };

        ITransportAdapter adapter = await CreateAdapterAsync(setup, cts.Token);
        await using var _ = (IAsyncDisposable)adapter;

        // Step 1 — fill the bounded queue through the default exchange; confirmed.
        IReadOnlyList<SendResult> fillResult =
            await adapter.SendBatchAsync([Message(string.Empty, full, "filler")], cts.Token);
        fillResult.Should().AllSatisfy(r => r.IsConfirmed.Should().BeTrue());

        // Step 2 — fan-out while the full queue has no active consumer yet: rejected there, accepted on
        // the queue with room. No NextAsync on the full queue happens before this point.
        IReadOnlyList<SendResult> fanoutResult =
            await adapter.SendBatchAsync([Message(fanoutEx, "any", "fanout-copy")], cts.Token);
        fanoutResult.Should().AllSatisfy(r => r.IsConfirmed.Should().BeFalse());

        // Step 3 — read the queue with room first; it holds only the fan-out copy.
        await using ParityQueueReader roomyReader = ParityQueueReader.Open(adapter, roomy, cts.Token);
        using InboundMessage roomyMessage = await roomyReader.NextAsync();
        Payload(roomyMessage).Should().Be("fanout-copy");
        await adapter.SettleAsync(SettlementAction.Ack, roomyMessage, cts.Token);

        // Step 4 — only now open the full queue's consumer and drain the filler, freeing its slot.
        await using ParityQueueReader fullReader = ParityQueueReader.Open(adapter, full, cts.Token);
        using InboundMessage fillerMessage = await fullReader.NextAsync();
        Payload(fillerMessage).Should().Be("filler");
        await adapter.SettleAsync(SettlementAction.Ack, fillerMessage, cts.Token);

        // Step 5 — the full queue has room again.
        IReadOnlyList<SendResult> markerResult =
            await adapter.SendBatchAsync([Message(string.Empty, full, "marker")], cts.Token);
        markerResult.Should().AllSatisfy(r => r.IsConfirmed.Should().BeTrue());

        // Step 6.
        using InboundMessage markerMessage = await fullReader.NextAsync();
        Payload(markerMessage).Should().Be("marker");
        await adapter.SettleAsync(SettlementAction.Ack, markerMessage, cts.Token);
    }

    /// <summary>P16 — a delivered message carries the routing key and exchange the transport actually used, not the publisher's own forged values.</summary>
    [Fact]
    public async Task ConsumeAsync_DeliveredMessage_CarriesRoutingKeyAndExchangeHeaders()
    {
        using CancellationTokenSource cts = new(TestTimeout);
        string ex = Name("ex");
        string q = Name("q");
        const string routingKey = "real-rk";

        ParitySetup setup = new(new TopologyDeclaration
        {
            Exchanges = [new ExchangeDeclaration(ex, ExchangeType.Direct, Durable: false)],
            Queues = [new QueueDeclaration(q, Durable: false)],
            ExchangeQueueBindings = [new ExchangeQueueBinding(ex, q, routingKey)],
        });

        ITransportAdapter adapter = await CreateAdapterAsync(setup, cts.Token);
        await using var _ = (IAsyncDisposable)adapter;
        await using ParityQueueReader reader = ParityQueueReader.Open(adapter, q, cts.Token);

        // The publisher additionally forges a BW-RoutingKey header. A transport that merely
        // reflected the publisher's own header back would still pass a test that omitted this — the
        // forged value is what proves the stamping is authoritative, not an echo.
        OutboundMessage forged = new(
            routingKey,
            new Dictionary<string, string>
            {
                [ExchangeHeaderName] = ex,
                [RoutingKeyHeaderName] = "forged-rk",
            },
            Encoding.UTF8.GetBytes("headers"),
            PayloadContentType);

        await adapter.SendBatchAsync([forged], cts.Token);

        using InboundMessage received = await reader.NextAsync();
        received.Headers[RoutingKeyHeaderName].Should().Be(
            routingKey, "the transport must stamp the routing key it actually used, not the publisher's forged header");
        received.Headers[ExchangeHeaderName].Should().Be(ex);
        await adapter.SettleAsync(SettlementAction.Ack, received, cts.Token);
    }

    // ── Difference tests — assert the RabbitMQ (reference) behavior; skipped, with a reason, where a
    //    transport cannot reproduce it ─────────────────────────────────────────────────────────────

    /// <summary>D1 — a message published to a durable queue survives the adapter restarting while the broker keeps running.</summary>
    [Fact]
    public async Task SendBatchAsync_DurableQueue_MessageSurvivesAdapterRestart()
    {
        SkipIfDifferent(ParityDifference.Durability);

        using CancellationTokenSource cts = new(TestTimeout);
        string q = Name("durable");
        ParitySetup setup = new(new TopologyDeclaration { Queues = [new QueueDeclaration(q, Durable: true)] });

        ITransportAdapter firstAdapter = await CreateAdapterAsync(setup, cts.Token);
        await firstAdapter.SendBatchAsync([Message(string.Empty, q, "durable-msg")], cts.Token);
        await ((IAsyncDisposable)firstAdapter).DisposeAsync();

        ITransportAdapter secondAdapter = await CreateAdapterAsync(setup, cts.Token);
        await using var _ = (IAsyncDisposable)secondAdapter;
        await using ParityQueueReader reader = ParityQueueReader.Open(secondAdapter, q, cts.Token);

        using InboundMessage received = await reader.NextAsync();
        Payload(received).Should().Be("durable-msg");
        await secondAdapter.SettleAsync(SettlementAction.Ack, received, cts.Token);
    }

    /// <summary>D2 — when a dead-letter queue itself is full, the broker's overflow policy (drop-head) applies to it too.</summary>
    [Fact]
    public async Task SettleAsync_NackIntoFullDeadLetterQueue_BrokerOverflowPolicyApplies()
    {
        SkipIfDifferent(ParityDifference.FullDeadLetterQueue);

        using CancellationTokenSource cts = new(TestTimeout);
        string src = Name("src");
        string dlx = Name("dlx");
        string dlq = Name("dlq");
        string overflowDlx = Name("overflow-dlx");
        string overflow = Name("overflow");

        ParitySetup setup = new(new TopologyDeclaration
        {
            Exchanges =
            [
                new ExchangeDeclaration(dlx, ExchangeType.Fanout, Durable: false),
                new ExchangeDeclaration(overflowDlx, ExchangeType.Fanout, Durable: false),
            ],
            Queues =
            [
                new QueueDeclaration(src, Durable: false, Arguments: new Dictionary<string, object>
                {
                    ["x-dead-letter-exchange"] = dlx,
                }),
                new QueueDeclaration(dlq, Durable: false, Arguments: new Dictionary<string, object>
                {
                    ["x-dead-letter-exchange"] = overflowDlx,
                    ["x-max-length"] = 1,
                }),
                new QueueDeclaration(overflow, Durable: false),
            ],
            ExchangeQueueBindings =
            [
                new ExchangeQueueBinding(dlx, dlq, string.Empty),
                new ExchangeQueueBinding(overflowDlx, overflow, string.Empty),
            ],
        });

        ITransportAdapter adapter = await CreateAdapterAsync(setup, cts.Token);
        await using var _ = (IAsyncDisposable)adapter;
        await using ParityQueueReader srcReader = ParityQueueReader.Open(adapter, src, cts.Token);
        await using ParityQueueReader overflowReader = ParityQueueReader.Open(adapter, overflow, cts.Token);
        await using ParityQueueReader dlqReader = ParityQueueReader.Open(adapter, dlq, cts.Token);

        await adapter.SendBatchAsync(
            [Message(string.Empty, src, "first"), Message(string.Empty, src, "second")], cts.Token);

        using InboundMessage first = await srcReader.NextAsync();
        Payload(first).Should().Be("first");
        await adapter.SettleAsync(SettlementAction.Nack, first, cts.Token);

        using InboundMessage second = await srcReader.NextAsync();
        Payload(second).Should().Be("second");
        await adapter.SettleAsync(SettlementAction.Nack, second, cts.Token);

        // The broker applies its overflow policy asynchronously; reading the overflow queue first blocks
        // until the dropped head actually arrives there — a positive signal instead of a fixed delay.
        using InboundMessage droppedHead = await overflowReader.NextAsync();
        Payload(droppedHead).Should().Be("first", "drop-head evicts the oldest dead-letter first");
        await adapter.SettleAsync(SettlementAction.Ack, droppedHead, cts.Token);

        using InboundMessage newest = await dlqReader.NextAsync();
        Payload(newest).Should().Be("second", "the newest dead-letter stays in the length-bounded queue");
        await adapter.SettleAsync(SettlementAction.Ack, newest, cts.Token);
    }

    /// <summary>D3 — on a classic queue with no delivery-limit, <c>Requeue</c> keeps redelivering the same content past what would be a redelivery limit elsewhere.</summary>
    [Fact]
    public async Task SettleAsync_RequeueBeyondRedeliveryLimit_KeepsRedeliveringOnClassicQueue()
    {
        SkipIfDifferent(ParityDifference.UnboundedRequeue);

        using CancellationTokenSource cts = new(TestTimeout);
        string q = Name("q");
        ParitySetup setup = new(new TopologyDeclaration { Queues = [new QueueDeclaration(q, Durable: false)] })
        {
            MaxRedeliveries = 3,
        };

        ITransportAdapter adapter = await CreateAdapterAsync(setup, cts.Token);
        await using var _ = (IAsyncDisposable)adapter;
        await using ParityQueueReader reader = ParityQueueReader.Open(adapter, q, cts.Token);

        await adapter.SendBatchAsync([Message(string.Empty, q, "unbounded")], cts.Token);

        for (int i = 0; i < setup.MaxRedeliveries!.Value + 1; i++)
        {
            using InboundMessage redelivered = await reader.NextAsync();
            Payload(redelivered).Should().Be(
                "unbounded", "a classic queue has no delivery-limit and keeps redelivering the same content");
            await adapter.SettleAsync(SettlementAction.Requeue, redelivered, cts.Token);
        }

        using InboundMessage finalDelivery = await reader.NextAsync();
        Payload(finalDelivery).Should().Be("unbounded");
        await adapter.SettleAsync(SettlementAction.Ack, finalDelivery, cts.Token);
    }

    /// <summary>D3b — the in-memory mirror of D3: past its redelivery limit, <c>Requeue</c> dead-letters the message instead of requeuing it again.</summary>
    [Fact]
    public async Task SettleAsync_RequeueBeyondRedeliveryLimit_DeadLettersMessage()
    {
        SkipIfDifferent(ParityDifference.BoundedRequeue);

        using CancellationTokenSource cts = new(TestTimeout);
        string src = Name("src");
        string dlx = Name("dlx");
        string dlq = Name("dlq");

        ParitySetup setup = new(new TopologyDeclaration
        {
            Exchanges = [new ExchangeDeclaration(dlx, ExchangeType.Fanout, Durable: false)],
            Queues =
            [
                new QueueDeclaration(src, Durable: false, Arguments: new Dictionary<string, object>
                {
                    ["x-dead-letter-exchange"] = dlx,
                }),
                new QueueDeclaration(dlq, Durable: false),
            ],
            ExchangeQueueBindings = [new ExchangeQueueBinding(dlx, dlq, string.Empty)],
        })
        {
            MaxRedeliveries = 3,
        };

        ITransportAdapter adapter = await CreateAdapterAsync(setup, cts.Token);
        await using var _ = (IAsyncDisposable)adapter;
        await using ParityQueueReader srcReader = ParityQueueReader.Open(adapter, src, cts.Token);
        await using ParityQueueReader dlqReader = ParityQueueReader.Open(adapter, dlq, cts.Token);

        await adapter.SendBatchAsync([Message(string.Empty, src, "d3b-msg")], cts.Token);

        for (int i = 0; i < setup.MaxRedeliveries!.Value; i++)
        {
            using InboundMessage redelivered = await srcReader.NextAsync();
            Payload(redelivered).Should().Be("d3b-msg");
            await adapter.SettleAsync(SettlementAction.Requeue, redelivered, cts.Token);
        }

        using InboundMessage lastDelivery = await srcReader.NextAsync();
        Payload(lastDelivery).Should().Be("d3b-msg");
        await adapter.SettleAsync(SettlementAction.Requeue, lastDelivery, cts.Token);

        using InboundMessage deadLettered = await dlqReader.NextAsync();
        Payload(deadLettered).Should().Be("d3b-msg");
        await adapter.SettleAsync(SettlementAction.Ack, deadLettered, cts.Token);

        await adapter.SendBatchAsync([Message(string.Empty, src, "marker")], cts.Token);
        using InboundMessage marker = await srcReader.NextAsync();
        Payload(marker).Should().Be("marker", "the dead-lettered delivery must never redeliver on the source queue");
        await adapter.SettleAsync(SettlementAction.Ack, marker, cts.Token);
    }

    /// <summary>
    /// D4 — a publisher-supplied <c>BW-*</c> header reaching the consumer unchanged is a known trust gap
    /// on RabbitMQ, not a guaranteed feature: it documents that a subscriber must never trust a
    /// publisher-supplied <c>BW-*</c> header as if the transport had stamped it.
    /// </summary>
    [Fact]
    public async Task ConsumeAsync_PublisherSuppliedBwHeader_IsPreservedByTransport()
    {
        SkipIfDifferent(ParityDifference.BwHeaderStrip);

        using CancellationTokenSource cts = new(TestTimeout);
        string q = Name("q");
        ParitySetup setup = new(new TopologyDeclaration { Queues = [new QueueDeclaration(q, Durable: false)] });

        ITransportAdapter adapter = await CreateAdapterAsync(setup, cts.Token);
        await using var _ = (IAsyncDisposable)adapter;
        await using ParityQueueReader reader = ParityQueueReader.Open(adapter, q, cts.Token);

        await SendD4MessageAsync(adapter, string.Empty, q, "forged-header-payload", cts.Token);

        using InboundMessage received = await reader.NextAsync();
        Payload(received).Should().Be("forged-header-payload");
        received.Headers.Should().ContainKey("BW-Forged").WhoseValue.Should().Be(
            "forged-value",
            "a publisher-supplied BW-* header reaching the consumer is a known RabbitMQ trust gap, not a " +
            "guaranteed feature — see the skip reason on the in-memory transport for the opposite, safer default");
        await adapter.SettleAsync(SettlementAction.Ack, received, cts.Token);
    }

    /// <summary>D5 — topology can be declared after the adapter has already started, and the newly declared queue is immediately usable.</summary>
    [Fact]
    public async Task DeployTopologyAsync_AfterAdapterStarted_DeclaresNewQueue()
    {
        SkipIfDifferent(ParityDifference.RuntimeTopology);

        using CancellationTokenSource cts = new(TestTimeout);
        string initialQueue = Name("initial");
        string newQueue = Name("new");
        ParitySetup setup = new(new TopologyDeclaration
        {
            Queues = [new QueueDeclaration(initialQueue, Durable: false)],
        });

        ITransportAdapter adapter = await CreateAdapterAsync(setup, cts.Token);
        await using var _ = (IAsyncDisposable)adapter;

        TopologyDeclaration expanded = setup.Topology with
        {
            Queues = [.. setup.Topology.Queues, new QueueDeclaration(newQueue, Durable: false)],
        };
        await adapter.DeployTopologyAsync(expanded, cts.Token);

        await using ParityQueueReader reader = ParityQueueReader.Open(adapter, newQueue, cts.Token);
        await adapter.SendBatchAsync([Message(string.Empty, newQueue, "runtime-topology")], cts.Token);

        using InboundMessage received = await reader.NextAsync();
        Payload(received).Should().Be("runtime-topology");
        await adapter.SettleAsync(SettlementAction.Ack, received, cts.Token);
    }

    /// <summary>D6 — a headers exchange, a message TTL, and a max-length queue argument are all accepted by the broker.</summary>
    [Fact]
    public async Task DeployTopologyAsync_HeadersExchangeAndQueueTtlAndMaxLength_AreAccepted()
    {
        SkipIfDifferent(ParityDifference.HeadersExchangeTtlMaxLength);

        using CancellationTokenSource cts = new(TestTimeout);
        string headersExchange = Name("headers-ex");
        string boundedQueue = Name("bounded");

        ParitySetup setup = new(new TopologyDeclaration
        {
            Exchanges = [new ExchangeDeclaration(headersExchange, ExchangeType.Headers, Durable: false)],
            Queues =
            [
                new QueueDeclaration(boundedQueue, Durable: false, Arguments: new Dictionary<string, object>
                {
                    ["x-message-ttl"] = 60_000,
                    ["x-max-length"] = 100,
                }),
            ],
        });

        ITransportAdapter adapter = await CreateAdapterAsync(setup, cts.Token);
        await using var _ = (IAsyncDisposable)adapter;

        // The broker accepted every declaration above without throwing; the queue is still fully usable
        // through the default exchange, which does not depend on header-based matching.
        await using ParityQueueReader reader = ParityQueueReader.Open(adapter, boundedQueue, cts.Token);
        await adapter.SendBatchAsync([Message(string.Empty, boundedQueue, "accepted")], cts.Token);

        using InboundMessage received = await reader.NextAsync();
        Payload(received).Should().Be("accepted");
        await adapter.SettleAsync(SettlementAction.Ack, received, cts.Token);
    }

    /// <summary>
    /// D7 — documentation only: a consumer outside its permitted virtual host is denied. Neither the
    /// in-memory transport (a single process shares one trust boundary) nor the shared test broker (no
    /// additional virtual hosts or restricted credentials are provisioned for it) can exercise this, so
    /// the scenario is always skipped; it exists so the row is not silently missing from this suite.
    /// </summary>
    [Fact]
    public Task ConsumeAsync_ConsumerOutsidePermittedVirtualHost_IsDenied()
    {
        SkipIfDifferent(ParityDifference.Confidentiality);
        return Task.CompletedTask;
    }
}
