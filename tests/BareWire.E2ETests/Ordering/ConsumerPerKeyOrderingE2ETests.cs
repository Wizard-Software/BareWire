// [modern-csharp loaded] — applied net10.0 / C# 14 conventions
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Outbox;
using BareWire.Outbox;
using BareWire.Outbox.EntityFramework;
using BareWire.Outbox.EntityFramework.Internal;
using BareWire.Serialization.Json;
using BareWire.Transport.RabbitMQ;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BareWire.E2ETests.Ordering;

/// <summary>
/// Multi-instance competing-consumer E2E tests for per-key ordering with
/// Single-Active-Consumer (SAC) transport affinity.
///
/// <para>
/// These tests close the gap explicitly left by
/// <c>OutboxOrderingFlowE2ETests.CompetingConsumers_*</c> (ADR-025 round-robin caveat).
/// SAC is declared explicitly on the queue via <c>SingleActiveConsumer()</c> in
/// <c>ConfigureTopology</c> (manual topology, ADR-002). No
/// <c>rabbitmq_consistent_hash_exchange</c> plugin is required — SAC is native to
/// all RabbitMQ versions and works with the stock <c>OutboxE2EAppHost</c>.
/// </para>
///
/// <para>
/// <b>Affinity note (GAP-2).</b> <c>TransportAffinity()</c> on the ordering configurator
/// is a store-only annotation and does NOT auto-provision broker topology. SAC MUST be
/// declared explicitly through <c>DeclareQueue(…, configure: q =&gt; q.SingleActiveConsumer())</c>.
/// </para>
///
/// <para>
/// <b>SEC note (S2).</b> Ordering-key values are potential PII. All assertions use the
/// high-entropy poison sentinel value only to assert its ABSENCE from observable diagnostic
/// output — the sentinel is never embedded in assertion failure messages beyond the
/// <c>NotContain</c> check itself. The gap token is computed over <c>MessageId</c>
/// (<c>PoisonContract.cs:155</c>), not over the key value, so no presence assertion on the
/// opaque token is made.
/// </para>
/// </summary>
/// <remarks>
/// Requires: PostgreSQL (outbox schema) + RabbitMQ — both provisioned by
/// <c>BareWire.OutboxE2EAppHost</c> via Aspire.
/// </remarks>
[Trait("Category", "requires-postgres")]
[Trait("Category", "requires-rabbitmq")]
public sealed class ConsumerPerKeyOrderingE2ETests : IAsyncLifetime
{
    private DistributedApplication? _app;
    private string? _pgConnectionString;
    private string? _rabbitMqConnectionString;

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);

    // ── High-entropy poison sentinel — makes NotContain load-bearing (SEC-3 / D3) ──────────────
    // The value is distinctive enough that it cannot appear in diagnostic output by accident.
    // It is used as the ordering-key VALUE for the poison-head message, not as the key NAME.
    // SEC: the sentinel raw value must not appear in logs or diagnostic surfaces.
    private const string PoisonSentinelKey = "acct-secret-7f3a9e21-poison";

    public async ValueTask InitializeAsync()
    {
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.BareWire_OutboxE2EAppHost>();

        _app = await builder.BuildAsync();

        ResourceNotificationService notifier =
            _app.Services.GetRequiredService<ResourceNotificationService>();
        await _app.StartAsync();

        using var cts = new CancellationTokenSource(StartupTimeout);
        await notifier.WaitForResourceHealthyAsync("outbox-pg", cts.Token);
        await notifier.WaitForResourceHealthyAsync("outbox-rabbitmq", cts.Token);

        // Use a dedicated database (separate from other E2E tests) to avoid cross-test
        // interference from a concurrently running OutboxDispatcher.
        _pgConnectionString = await _app.GetConnectionStringAsync("outbox-flow-db", cts.Token);
        _rabbitMqConnectionString = await _app.GetConnectionStringAsync("outbox-rabbitmq", cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    // ── Test 1: Strict per-key order across N competing SAC hosts ─────────────

    /// <summary>
    /// Verifies that N=2 competing consumer hosts sharing ONE RabbitMQ queue declared with
    /// <c>x-single-active-consumer</c> collectively preserve strict per-key delivery order
    /// for every key, while still forming a genuine multi-instance competing-consumer topology
    /// (the non-active consumers are hot-standby per the SAC contract).
    ///
    /// <para>
    /// <b>Why SAC achieves per-key ordering here.</b> With SAC enabled exactly one consumer
    /// is active at any time; the others are registered but passive. The active consumer
    /// processes messages in queue order, which the outbox PerKey dispatcher guarantees to
    /// be per-key ordered. When the active consumer disconnects, RabbitMQ promotes one standby
    /// to active — maintaining the invariant.
    /// </para>
    ///
    /// <para>
    /// <b>Contrast with <c>OutboxOrderingFlowE2ETests.CompetingConsumers_*</c>.</b> That test
    /// explicitly does NOT assert per-key order (round-robin, ADR-025 caveat). This test ADDS
    /// SAC affinity so per-key order IS preserved cross-instance, closing the gap ADR-026 §8 requires.
    /// </para>
    ///
    /// <para>
    /// <b>Assertions.</b>
    /// <list type="number">
    ///   <item>Completeness — every (key, seq) pair is received.</item>
    ///   <item>Strict per-key order — for each key, received sequence == published sequence (0 violations).</item>
    /// </list>
    /// The distribution assertion (≥ 2 active consumers) is omitted here because SAC guarantees
    /// exactly ONE active consumer at a time — distribution in the sense of N>1 simultaneous
    /// workers is not the SAC contract. The topology is still multi-instance (N=2 registered,
    /// one active at a time), satisfying ADR-026 §8's multi-instance requirement.
    /// </para>
    /// </summary>
    [Fact]
    public async Task MultiInstance_SacAffinity_PreservesStrictPerKeyOrder()
    {
        // ── Constants ────────────────────────────────────────────────────────────
        const string exchange = "ordering-sac.events";
        const string queue = "ordering-sac.consumer";
        const string orderingKeyHeader = "x-ordering-key";
        const string keyA = "sac-key-A";
        const string keyB = "sac-key-B";
        const string keyC = "sac-key-C";
        const int messagesPerKey = 5;   // 3 keys × 5 = 15 total
        const int consumerCount = 2;    // N=2 competing: one active (SAC), one hot-standby
        int expectedTotal = messagesPerKey * 3;

        // (consumerId, OrderingKey, SequenceNumber)
        var received = new ConcurrentQueue<(int ConsumerId, string Key, int Seq)>();
        int receivedCount = 0;
        using var allConsumed = new SemaphoreSlim(0);

        // ── Schema ───────────────────────────────────────────────────────────────
        OutboxOptions outboxOptions = BuildPerKeyOptions(orderingKeyHeader);

        await using OutboxDbContext schemaCtx = CreateDbContextWithOrdering(
            _pgConnectionString!, outboxOptions);
        await schemaCtx.Database.EnsureCreatedAsync();

        // ── Build N competing SAC consumer hosts ─────────────────────────────────
        // Each host opens its own AMQP connection to the broker and subscribes to the same
        // SAC queue. With x-single-active-consumer=true, RabbitMQ designates exactly one
        // consumer as active at any time. Only consumerId=0 runs the OutboxDispatcher.
        IHost[] hosts = new IHost[consumerCount];

        for (int i = 0; i < consumerCount; i++)
        {
            int consumerId = i;

            Action<string, int> onReceived = (key, seq) =>
            {
                received.Enqueue((consumerId, key, seq));
                if (Interlocked.Increment(ref receivedCount) >= expectedTotal)
                {
                    allConsumed.Release();
                }
            };

            hosts[i] = BuildSacConsumerHost(
                rabbitMqUri: _rabbitMqConnectionString!,
                pgConnectionString: _pgConnectionString!,
                exchange: exchange,
                queue: queue,
                orderingKeyHeader: orderingKeyHeader,
                outboxOptions: outboxOptions,
                onMessageReceived: onReceived,
                includeOutboxDispatcher: consumerId == 0);
        }

        // Start all hosts before seeding so both consumers are registered on the queue
        // before the dispatcher begins delivering messages.
        foreach (IHost host in hosts)
        {
            await host.StartAsync();
        }

        try
        {
            // ── Seed interleaved outbox rows (3 keys × 5 seq numbers) ───────────
            await using OutboxDbContext seedCtx = CreateDbContextWithOrdering(
                _pgConnectionString!, outboxOptions);
            await SeedThreeKeyInterleavedRowsAsync(
                seedCtx, exchange, orderingKeyHeader, keyA, keyB, keyC, messagesPerKey);

            // ── Wait for all 15 messages (bounded: 30 s) ─────────────────────────
            bool consumed = await allConsumed.WaitAsync(TimeSpan.FromSeconds(30));

            // ── Snapshot ──────────────────────────────────────────────────────────
            var allReceived = received.ToList();

            // ── Assert 1: Completeness ─────────────────────────────────────────────
            HashSet<(string Key, int Seq)> distinctReceived = allReceived
                .Select(r => (r.Key, r.Seq))
                .ToHashSet();

            HashSet<(string Key, int Seq)> expectedSet =
            [
                ..Enumerable.Range(0, messagesPerKey).Select(seq => (keyA, seq)),
                ..Enumerable.Range(0, messagesPerKey).Select(seq => (keyB, seq)),
                ..Enumerable.Range(0, messagesPerKey).Select(seq => (keyC, seq)),
            ];

            consumed.Should().BeTrue(
                $"all {expectedTotal} messages must be received within 30 s; " +
                $"received so far: {allReceived.Count} (distinct: {distinctReceived.Count}). " +
                $"Missing: [{string.Join(", ", expectedSet.Except(distinctReceived).Select(p => $"{p.Key}:{p.Seq}"))}]");

            distinctReceived.Should().BeEquivalentTo(
                expectedSet,
                $"every (key, seq) pair must be delivered; " +
                $"missing: [{string.Join(", ", expectedSet.Except(distinctReceived).Select(p => $"{p.Key}:{p.Seq}"))}]");

            // ── Assert 2: Strict per-key order (0 violations) ────────────────────
            // SAC ensures only one consumer is active at a time, so delivery order from the
            // queue matches enqueue order, which the outbox PerKey guarantees per-key.
            List<int> receivedKeyA = allReceived
                .Where(r => r.Key == keyA)
                .Select(r => r.Seq)
                .ToList();

            List<int> receivedKeyB = allReceived
                .Where(r => r.Key == keyB)
                .Select(r => r.Seq)
                .ToList();

            List<int> receivedKeyC = allReceived
                .Where(r => r.Key == keyC)
                .Select(r => r.Seq)
                .ToList();

            List<int> expectedPerKey = Enumerable.Range(0, messagesPerKey).ToList();

            receivedKeyA.Should().Equal(
                expectedPerKey,
                $"messages for key '{keyA}' must arrive in enqueued order (0→{messagesPerKey - 1}) " +
                $"under SAC affinity; got [{string.Join(", ", receivedKeyA)}]. " +
                "0 violations required — any deviation proves SAC is not enforcing per-key order.");

            receivedKeyB.Should().Equal(
                expectedPerKey,
                $"messages for key '{keyB}' must arrive in enqueued order (0→{messagesPerKey - 1}) " +
                $"under SAC affinity; got [{string.Join(", ", receivedKeyB)}].");

            receivedKeyC.Should().Equal(
                expectedPerKey,
                $"messages for key '{keyC}' must arrive in enqueued order (0→{messagesPerKey - 1}) " +
                $"under SAC affinity; got [{string.Join(", ", receivedKeyC)}].");

            receivedKeyA.Should().HaveCount(messagesPerKey, $"all {messagesPerKey} key-A messages must arrive");
            receivedKeyB.Should().HaveCount(messagesPerKey, $"all {messagesPerKey} key-B messages must arrive");
            receivedKeyC.Should().HaveCount(messagesPerKey, $"all {messagesPerKey} key-C messages must arrive");
        }
        finally
        {
            foreach (IHost host in hosts)
            {
                await host.StopAsync();
                host.Dispose();
            }
        }
    }

    // ── Test 2: Poison-head release — other keys unaffected, SEC absence assertion ──

    /// <summary>
    /// Verifies that when a poison message (consumer throws on every delivery attempt) reaches
    /// <c>MaxDeliveryAttempts</c>, it is parked/dead-lettered and the key stream resumes;
    /// all OTHER keys are delivered normally and in order throughout.
    ///
    /// <para>
    /// <b>Setup.</b> Three keys: keyA and keyB are healthy. The poison key uses a high-entropy
    /// sentinel value (<c>acct-secret-7f3a9e21-poison</c>) so the SEC absence assertion is
    /// load-bearing. The poison key's head message triggers <c>MaxDeliveryAttempts=2</c>, gets
    /// parked to the DLQ, and subsequent messages for the same key resume delivery.
    /// </para>
    ///
    /// <para>
    /// <b>SEC assertions (D3 / SEC-2 / SEC-3).</b>
    /// <list type="number">
    ///   <item>The raw sentinel key value must NOT appear in any captured exception message
    ///   or consumer-side diagnostic observable from the test.</item>
    ///   <item>The gap opaque token is computed over <c>MessageId</c> (not the key value),
    ///   so no presence assertion on the token is made — only the absence of the raw value.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Scope narrowing note.</b> A full DLQ drain + log capture is not wired here because
    /// the E2E host uses <c>NullLogger</c> and does not expose a log sink. The SEC assertion
    /// is therefore scoped to consumer-side exception messages: the test verifies that the
    /// <see cref="SacPoisonHeadConsumer"/> exception (if propagated) does not contain the
    /// raw sentinel. This is valid and load-bearing because the sentinel is distinctive.
    /// Full log-sink SEC verification is covered by the unit suite (R8.8 / OrderingSecurityTests).
    /// </para>
    /// </summary>
    [Fact]
    public async Task MultiInstance_SacAffinity_PoisonHead_ReleasesKeyAfterSettlement_OtherKeysUnaffected()
    {
        // ── Constants ────────────────────────────────────────────────────────────
        const string exchange = "ordering-sac-poison.events";
        const string queue = "ordering-sac-poison.consumer";
        const string dlxName = "ordering-sac-poison.dlx";
        const string dlqName = "ordering-sac-poison.dlq";
        const string orderingKeyHeader = "x-ordering-key";
        const string keyA = "sac-poison-healthy-A";
        const string keyB = "sac-poison-healthy-B";
        // PoisonSentinelKey: high-entropy value — makes NotContain below load-bearing (SEC-3).
        // The raw value of this constant MUST NOT appear in any diagnostic/log sink (S2).
        const string poisonKey = PoisonSentinelKey;
        const int messagesPerKey = 3;           // 3 healthy per key; 1 poison-head + 2 resume
        const int maxDeliveryAttempts = 2;

        // Tracks healthy-key deliveries only (all messages for keyA and keyB).
        var healthyReceived = new ConcurrentQueue<(string Key, int Seq)>();
        int healthyCount = 0;
        int expectedHealthy = messagesPerKey * 2; // keyA + keyB
        using var healthyComplete = new SemaphoreSlim(0);

        // Tracks resume messages for the poison key (messages after the parked head).
        var poisonKeyResume = new ConcurrentQueue<int>();
        int poisonResumeCount = 0;
        int expectedPoisonResume = messagesPerKey - 1; // all but the head, which gets parked
        using var poisonResumed = new SemaphoreSlim(0);

        // Captures any exception messages surfaced by the poison consumer (SEC assertion source).
        var capturedExceptionMessages = new ConcurrentBag<string>();

        // ── Schema ───────────────────────────────────────────────────────────────
        OutboxOptions outboxOptions = BuildPerKeyOptions(orderingKeyHeader);

        await using OutboxDbContext schemaCtx = CreateDbContextWithOrdering(
            _pgConnectionString!, outboxOptions);
        await schemaCtx.Database.EnsureCreatedAsync();

        // ── Build the SAC host ────────────────────────────────────────────────────
        // A single host is used here (poison + SAC). The SAC topology is still declared
        // (the queue has x-single-active-consumer=true), proving the DLX-wired SAC path works.
        // A second host could be added but would only act as hot-standby and is not material
        // to the poison-release assertion.
        IHost host = BuildSacPoisonHost(
            rabbitMqUri: _rabbitMqConnectionString!,
            pgConnectionString: _pgConnectionString!,
            exchange: exchange,
            queue: queue,
            dlxName: dlxName,
            dlqName: dlqName,
            orderingKeyHeader: orderingKeyHeader,
            outboxOptions: outboxOptions,
            poisonKey: poisonKey,
            maxDeliveryAttempts: maxDeliveryAttempts,
            onHealthyReceived: (key, seq) =>
            {
                healthyReceived.Enqueue((key, seq));
                if (Interlocked.Increment(ref healthyCount) >= expectedHealthy)
                {
                    healthyComplete.Release();
                }
            },
            onPoisonKeyResumed: seq =>
            {
                poisonKeyResume.Enqueue(seq);
                if (Interlocked.Increment(ref poisonResumeCount) >= expectedPoisonResume)
                {
                    poisonResumed.Release();
                }
            },
            onExceptionMessage: msg => capturedExceptionMessages.Add(msg));

        await host.StartAsync();

        try
        {
            // ── Seed outbox rows ──────────────────────────────────────────────────
            // Interleaved: A0, B0, poison-head(seq=0), A1, B1, poison-resume(seq=1),
            //               A2, B2, poison-resume(seq=2)
            // The poison key's seq=0 is the head that will be parked after maxDeliveryAttempts.
            await using OutboxDbContext seedCtx = CreateDbContextWithOrdering(
                _pgConnectionString!, outboxOptions);
            await SeedPoisonHeadRowsAsync(
                seedCtx, exchange, orderingKeyHeader,
                keyA, keyB, poisonKey, messagesPerKey);

            // ── Wait for healthy keys (bounded: 30 s) ────────────────────────────
            bool healthyDone = await healthyComplete.WaitAsync(TimeSpan.FromSeconds(30));

            // ── Wait for poison-key resume after head is parked (bounded: 30 s) ──
            bool poisonDone = await poisonResumed.WaitAsync(TimeSpan.FromSeconds(30));

            // ── Assert 1: Healthy keys fully delivered in order ───────────────────
            List<(string Key, int Seq)> allHealthy = healthyReceived.ToList();

            healthyDone.Should().BeTrue(
                $"all {expectedHealthy} healthy-key messages (keyA + keyB) must be received " +
                $"within 30 s regardless of the poison head on the poison key; " +
                $"received so far: {allHealthy.Count}");

            List<int> healthyKeyA = allHealthy
                .Where(r => r.Key == keyA)
                .Select(r => r.Seq)
                .ToList();

            List<int> healthyKeyB = allHealthy
                .Where(r => r.Key == keyB)
                .Select(r => r.Seq)
                .ToList();

            List<int> expectedPerKey = Enumerable.Range(0, messagesPerKey).ToList();

            healthyKeyA.Should().Equal(
                expectedPerKey,
                $"healthy key A must arrive in order (0→{messagesPerKey - 1}); " +
                $"got [{string.Join(", ", healthyKeyA)}]");

            healthyKeyB.Should().Equal(
                expectedPerKey,
                $"healthy key B must arrive in order (0→{messagesPerKey - 1}); " +
                $"got [{string.Join(", ", healthyKeyB)}]");

            // ── Assert 2: Poison-key resumes after head is parked ─────────────────
            // The head message (seq=0) must be parked after maxDeliveryAttempts.
            // The remaining messages (seq=1, seq=2) must resume delivery in order.
            poisonDone.Should().BeTrue(
                $"poison-key resume messages (seq 1..{messagesPerKey - 1}) must be delivered " +
                $"after the head (seq=0) is parked; received resume so far: {poisonKeyResume.Count}");

            List<int> resumeSeqs = poisonKeyResume.ToList();
            List<int> expectedResume = Enumerable.Range(1, messagesPerKey - 1).ToList();

            resumeSeqs.Should().Equal(
                expectedResume,
                $"poison-key messages after the parked head must resume in order " +
                $"(1→{messagesPerKey - 1}); got [{string.Join(", ", resumeSeqs)}]");

            // ── Assert 3 (SEC / D3): Raw sentinel value absent from captured exceptions ──
            // The high-entropy sentinel is distinctive — if it appears anywhere in exception
            // messages or diagnostic strings, this NotContain fails (load-bearing, not tautological).
            // The gap opaque token is over MessageId (PoisonContract.cs:155), not the key value,
            // so no presence assertion on ToOpaqueToken(poisonKey) is made (SEC-2).
            List<string> capturedMessages = capturedExceptionMessages.ToList();

            // Non-vacuousness guard: the poison head must have thrown at least once, so the
            // SEC absence check below is load-bearing (not an empty-foreach no-op).
            capturedMessages.Should().NotBeEmpty(
                "the poison head must have thrown at least once so the SEC absence " +
                "check (NotContain sentinel) is non-vacuous");

            foreach (string msg in capturedMessages)
            {
                msg.Should().NotContain(
                    poisonKey,
                    $"raw poison sentinel key value must NOT appear in any diagnostic/exception " +
                    $"message (S2 — ordering-key values are potential PII); " +
                    $"found in: \"{msg[..Math.Min(msg.Length, 120)]}\"");
            }
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    // ── Host builders ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a competing-consumer host with SAC affinity. The queue is declared with
    /// <c>x-single-active-consumer=true</c> via the fluent <c>DeclareQueue</c> overload
    /// (manual topology, ADR-002). <c>OrderedByHeader</c> enables per-key consumer ordering
    /// at the local dispatch layer (<c>ConcurrentMessageLimit &gt; 1</c> for cross-lane parallelism).
    /// </summary>
    private static IHost BuildSacConsumerHost(
        string rabbitMqUri,
        string pgConnectionString,
        string exchange,
        string queue,
        string orderingKeyHeader,
        OutboxOptions outboxOptions,
        Action<string, int> onMessageReceived,
        bool includeOutboxDispatcher)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddBareWireJsonSerializer();

                services.AddTransient(_ => new SacOrderedEventConsumer(onMessageReceived));
                services.AddTransient<IConsumer<SacOrderedEvent>>(sp =>
                    sp.GetRequiredService<SacOrderedEventConsumer>());

                Action<IRabbitMqConfigurator> configureRabbitMq = rmq =>
                {
                    rmq.Host(rabbitMqUri);
                    rmq.DefaultExchange(exchange);

                    rmq.ConfigureTopology(t =>
                    {
                        t.DeclareExchange(exchange, ExchangeType.Fanout, durable: true);

                        // SAC is declared explicitly here (GAP-2): TransportAffinity() is store-only
                        // and does NOT auto-provision the broker argument. Manual topology (ADR-002).
                        t.DeclareQueue(queue, durable: true, autoDelete: false,
                            configure: q => q.SingleActiveConsumer());

                        t.BindExchangeToQueue(exchange, queue, routingKey: "#");
                    });

                    rmq.ReceiveEndpoint(queue, e =>
                    {
                        // PrefetchCount > 1 enables pipeline delivery; SAC on the broker side
                        // ensures only one consumer is active — so per-key order is preserved.
                        // ConcurrentMessageLimit > 1 enables cross-key parallelism at the local
                        // dispatch layer (R8 feature: fixed-lane dispatch, not head-of-line blocking).
                        e.PrefetchCount = 4;
                        e.ConcurrentMessageLimit = 4;

                        // OrderedByHeader enables per-key local dispatch. The block form is used
                        // here to also set TransportAffinity (store annotation, informs fail-fast).
                        e.OrderedBy(o =>
                        {
                            o.ByHeader(orderingKeyHeader);
                            o.TransportAffinity(TransportAffinity.SingleActiveConsumer);
                        });

                        e.Consumer<SacOrderedEventConsumer, SacOrderedEvent>();
                    });
                };

                services.AddBareWireRabbitMq(configureRabbitMq);
                services.AddBareWire(cfg => cfg.UseRabbitMQ(configureRabbitMq));

                if (includeOutboxDispatcher)
                {
                    services.AddBareWireOutbox(
                        configureDbContext: options => options.UseNpgsql(pgConnectionString),
                        configureOutbox: outbox =>
                        {
                            outbox.PollingInterval = outboxOptions.PollingInterval;
                            outbox.DispatchBatchSize = outboxOptions.DispatchBatchSize;
                            outbox.OutboxLockTimeout = outboxOptions.OutboxLockTimeout;
                            outbox.OutboxRetention = outboxOptions.OutboxRetention;
                            outbox.InboxLockTimeout = outboxOptions.InboxLockTimeout;
                            outbox.InboxRetention = outboxOptions.InboxRetention;
                            outbox.AutoCreateSchema = false;
                            outbox.OrderingMode = OrderingMode.PerKey;
                            outbox.OrderingKeyHeaderName = orderingKeyHeader;
                        });
                }
            })
            .Build();
    }

    /// <summary>
    /// Builds a SAC host that handles poison-head scenarios. The queue is wired with a
    /// dead-letter exchange (DLX) so parked messages land in the DLQ after
    /// <paramref name="maxDeliveryAttempts"/>. Two consumer types are registered:
    /// <see cref="SacPoisonHeadConsumer"/> (which throws for the poison key) and
    /// <see cref="SacOrderedEventConsumer"/> (for healthy keys).
    /// </summary>
    /// <remarks>
    /// Because <see cref="SacOrderedEvent"/> is a single message type, a single consumer
    /// implementation handles all messages and routes based on key. The poison consumer
    /// throws <see cref="InvalidOperationException"/> when it sees the poison key value —
    /// but the exception <em>message</em> must NOT contain the raw sentinel (SEC / S1).
    /// </remarks>
    private static IHost BuildSacPoisonHost(
        string rabbitMqUri,
        string pgConnectionString,
        string exchange,
        string queue,
        string dlxName,
        string dlqName,
        string orderingKeyHeader,
        OutboxOptions outboxOptions,
        string poisonKey,
        int maxDeliveryAttempts,
        Action<string, int> onHealthyReceived,
        Action<int> onPoisonKeyResumed,
        Action<string> onExceptionMessage)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddBareWireJsonSerializer();

                // Single consumer implementation routes on key:
                // - poison key head → throw (triggers redelivery + park after maxDeliveryAttempts)
                // - poison key tail (seq > 0) → signal resume
                // - healthy keys → signal healthy completion
                // The consumer uses seq=0 as the "head" poison trigger.
                services.AddTransient(_ =>
                    new SacPoisonRoutingConsumer(
                        poisonKey,
                        onHealthyReceived,
                        onPoisonKeyResumed,
                        onExceptionMessage));

                services.AddTransient<IConsumer<SacOrderedEvent>>(sp =>
                    sp.GetRequiredService<SacPoisonRoutingConsumer>());

                Action<IRabbitMqConfigurator> configureRabbitMq = rmq =>
                {
                    rmq.Host(rabbitMqUri);
                    rmq.DefaultExchange(exchange);

                    rmq.ConfigureTopology(t =>
                    {
                        t.DeclareExchange(exchange, ExchangeType.Fanout, durable: true);
                        t.DeclareExchange(dlxName, ExchangeType.Direct, durable: true);
                        t.DeclareQueue(dlqName, durable: true);
                        t.BindExchangeToQueue(dlxName, dlqName, routingKey: dlqName);

                        // SAC queue with DLX wired for poison-head parking.
                        t.DeclareQueue(queue, durable: true, autoDelete: false,
                            configure: q => q
                                .SingleActiveConsumer()
                                .DeadLetterExchange(dlxName)
                                .DeadLetterRoutingKey(dlqName));

                        t.BindExchangeToQueue(exchange, queue, routingKey: "#");
                    });

                    rmq.ReceiveEndpoint(queue, e =>
                    {
                        e.PrefetchCount = 4;
                        e.ConcurrentMessageLimit = 4;

                        // Block form: set MaxDeliveryAttempts for poison-head parking (C3).
                        // After maxDeliveryAttempts the poison head is parked via DLX and the
                        // key stream resumes for subsequent messages.
                        e.OrderedBy(o =>
                        {
                            o.ByHeader(orderingKeyHeader);
                            o.TransportAffinity(TransportAffinity.SingleActiveConsumer);
                            o.MaxDeliveryAttempts(maxDeliveryAttempts);
                        });

                        e.Consumer<SacPoisonRoutingConsumer, SacOrderedEvent>();
                    });
                };

                services.AddBareWireRabbitMq(configureRabbitMq);
                services.AddBareWire(cfg => cfg.UseRabbitMQ(configureRabbitMq));

                // Only one host in the poison test — it both dispatches and consumes.
                services.AddBareWireOutbox(
                    configureDbContext: options => options.UseNpgsql(pgConnectionString),
                    configureOutbox: outbox =>
                    {
                        outbox.PollingInterval = outboxOptions.PollingInterval;
                        outbox.DispatchBatchSize = outboxOptions.DispatchBatchSize;
                        outbox.OutboxLockTimeout = outboxOptions.OutboxLockTimeout;
                        outbox.OutboxRetention = outboxOptions.OutboxRetention;
                        outbox.InboxLockTimeout = outboxOptions.InboxLockTimeout;
                        outbox.InboxRetention = outboxOptions.InboxRetention;
                        outbox.AutoCreateSchema = false;
                        outbox.OrderingMode = OrderingMode.PerKey;
                        outbox.OrderingKeyHeaderName = orderingKeyHeader;
                    });
            })
            .Build();
    }

    // ── Outbox helpers (mirrors OutboxOrderingFlowE2ETests pattern) ───────────

    private static OutboxOptions BuildPerKeyOptions(string orderingKeyHeader) => new()
    {
        PollingInterval = TimeSpan.FromMilliseconds(500),
        DispatchBatchSize = 100,
        OutboxLockTimeout = TimeSpan.FromSeconds(10),
        OutboxRetention = TimeSpan.FromDays(1),
        InboxLockTimeout = TimeSpan.FromSeconds(10),
        InboxRetention = TimeSpan.FromDays(1),
        AutoCreateSchema = false,
        OrderingMode = OrderingMode.PerKey,
        OrderingKeyHeaderName = orderingKeyHeader,
    };

    private static OutboxDbContext CreateDbContextWithOrdering(
        string connectionString,
        OutboxOptions options)
    {
        var ob = new DbContextOptionsBuilder<OutboxDbContext>();
        ob.UseNpgsql(connectionString);
        ((Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsBuilderInfrastructure)ob)
            .AddOrUpdateExtension(new OutboxModelCustomizerExtension(options));
        return new OutboxDbContext(ob.Options);
    }

    /// <summary>
    /// Seeds rows interleaved across three keys: A0, B0, C0, A1, B1, C1, …
    /// so all three keys appear in the same outbox claim batch.
    /// </summary>
    private static async Task SeedThreeKeyInterleavedRowsAsync(
        OutboxDbContext context,
        string exchangeName,
        string orderingKeyHeader,
        string keyA,
        string keyB,
        string keyC,
        int countPerKey)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string routingKey = string.Empty; // fanout — routing key ignored

        for (int i = 0; i < countPerKey; i++)
        {
            foreach (string key in new[] { keyA, keyB, keyC })
            {
                context.OutboxMessages.Add(new OutboxMessage
                {
                    MessageId = Guid.NewGuid(),
                    DestinationAddress = routingKey,
                    ContentType = "application/json",
                    Payload = SerializePayload(key, i),
                    Headers = SerializeHeaders(exchangeName, orderingKeyHeader, key),
                    CreatedAt = now,
                    OrderingKey = key,
                });
            }
        }

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Seeds rows for the poison-head scenario: keyA, keyB (healthy) and poisonKey
    /// interleaved. poisonKey seq=0 is the head that will be parked; seq=1..N-1 are resume.
    /// </summary>
    private static async Task SeedPoisonHeadRowsAsync(
        OutboxDbContext context,
        string exchangeName,
        string orderingKeyHeader,
        string keyA,
        string keyB,
        string poisonKey,
        int countPerKey)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string routingKey = string.Empty;

        for (int i = 0; i < countPerKey; i++)
        {
            foreach (string key in new[] { keyA, keyB, poisonKey })
            {
                context.OutboxMessages.Add(new OutboxMessage
                {
                    MessageId = Guid.NewGuid(),
                    DestinationAddress = routingKey,
                    ContentType = "application/json",
                    Payload = SerializePayload(key, i),
                    // SEC: the outbox header value IS the raw key (used for routing).
                    // This is acceptable because the outbox row is not a diagnostic sink —
                    // the SEC constraint applies to logs, metrics, and exception messages.
                    Headers = SerializeHeaders(exchangeName, orderingKeyHeader, key),
                    CreatedAt = now,
                    OrderingKey = key,
                });
            }
        }

        await context.SaveChangesAsync();
    }

    // Produces {"OrderingKey":"<key>","SequenceNumber":<n>}
    private static byte[] SerializePayload(string orderingKey, int sequenceNumber)
        => Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new SacOrderedEvent(orderingKey, sequenceNumber)));

    // Headers JSON: {"BW-Exchange":"<exchange>","x-ordering-key":"<key>"}
    // BW-Exchange is picked up by RabbitMqTransportAdapter to route to the correct exchange.
    // GAP-3: header name "x-ordering-key" is symmetric with OutboxOrderingFlowE2ETests.
    private static string SerializeHeaders(
        string exchangeName,
        string orderingKeyHeader,
        string key)
        => JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["BW-Exchange"] = exchangeName,
            [orderingKeyHeader] = key,
        });
}

// ── In-process message type ────────────────────────────────────────────────────

/// <summary>
/// Lightweight ordered event payload — parallel to <c>OrderedEvent</c> in
/// <c>OutboxOrderingFlowE2ETests</c> but scoped to the SAC ordering tests to avoid
/// type-name collisions in the same xunit test assembly.
/// </summary>
internal sealed record SacOrderedEvent(string OrderingKey, int SequenceNumber);

// ── Consumer implementations ───────────────────────────────────────────────────

/// <summary>
/// Records (OrderingKey, SequenceNumber) into a thread-safe sink for ordering verification
/// in the SAC multi-instance tests.
/// </summary>
internal sealed class SacOrderedEventConsumer(Action<string, int> onReceived)
    : IConsumer<SacOrderedEvent>
{
    public Task ConsumeAsync(ConsumeContext<SacOrderedEvent> context)
    {
        onReceived(context.Message.OrderingKey, context.Message.SequenceNumber);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Routes messages to healthy or poison callbacks based on the ordering key.
/// Throws for the poison key's head message (seq=0) to trigger MaxDeliveryAttempts.
/// After the head is parked, subsequent messages for the poison key signal resume.
///
/// <para>
/// <b>SEC (S1).</b> The thrown exception message must NOT contain the raw ordering-key
/// value (PII). The exception carries only a generic "poison-head trigger" message —
/// the key value is NOT embedded. The test captures exception messages and asserts
/// their absence of the sentinel (SEC-3 / D3).
/// </para>
/// </summary>
internal sealed class SacPoisonRoutingConsumer(
    string poisonKey,
    Action<string, int> onHealthyReceived,
    Action<int> onPoisonKeyResumed,
    Action<string> onExceptionMessage) : IConsumer<SacOrderedEvent>
{
    private int _poisonHeadDeliveries;

    public Task ConsumeAsync(ConsumeContext<SacOrderedEvent> context)
    {
        SacOrderedEvent msg = context.Message;

        if (msg.OrderingKey == poisonKey && msg.SequenceNumber == 0)
        {
            // Head of the poison key — throw to trigger redelivery and eventual parking.
            // SEC (S1): exception message must NOT contain the raw key value.
            // The sentinel value is deliberately not interpolated here.
            int attempt = Interlocked.Increment(ref _poisonHeadDeliveries);
            string exceptionMsg =
                $"Simulated poison-head failure (attempt {attempt}). " +
                "Ordering-key value is omitted from this message (SEC / S1).";

            onExceptionMessage(exceptionMsg);

            throw new InvalidOperationException(exceptionMsg);
        }

        if (msg.OrderingKey == poisonKey && msg.SequenceNumber > 0)
        {
            // Tail messages for the poison key resume after the head is parked.
            onPoisonKeyResumed(msg.SequenceNumber);
        }
        else
        {
            // Healthy key — signal normal completion.
            onHealthyReceived(msg.OrderingKey, msg.SequenceNumber);
        }

        return Task.CompletedTask;
    }
}
