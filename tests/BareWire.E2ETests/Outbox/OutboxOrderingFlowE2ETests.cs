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

namespace BareWire.E2ETests.Outbox;

/// <summary>
/// Full outbox-ordering circuit E2E test: PostgreSQL outbox with PerKey ordering →
/// OutboxDispatcher (IHostedService) → RabbitMQ broker → a single consumer →
/// asserts that per-key order is preserved at the consumer.
/// </summary>
/// <remarks>
/// <para>
/// Ordering holds only under a single-consumer configuration. The test enforces this by
/// setting PrefetchCount=1 on the receive endpoint and running a single consumer instance.
/// With multiple consumers sharing a queue, RabbitMQ delivers round-robin and ordering
/// would require application-level partitioning (see E2E-012) — that is an explicitly
/// out-of-scope scenario here per ADR-025.
/// </para>
/// </remarks>
[Trait("Category", "requires-postgres")]
[Trait("Category", "requires-rabbitmq")]
public sealed class OutboxOrderingFlowE2ETests : IAsyncLifetime
{
    private DistributedApplication? _app;
    private string? _pgConnectionString;
    private string? _rabbitMqConnectionString;

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);

    public async ValueTask InitializeAsync()
    {
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.BareWire_OutboxE2EAppHost>();

        _app = await builder.BuildAsync();

        var notifier = _app.Services.GetRequiredService<ResourceNotificationService>();
        await _app.StartAsync();

        using var cts = new CancellationTokenSource(StartupTimeout);
        await notifier.WaitForResourceHealthyAsync("outbox-pg", cts.Token);
        await notifier.WaitForResourceHealthyAsync("outbox-rabbitmq", cts.Token);

        // Dedicated database (separate from OutboxClaimE2ETests' "outbox-db") so this test's
        // polling OutboxDispatcher cannot claim rows under test in the parallel class.
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

    // ── E2E-R7.7: Per-key ordering — full outbox-to-consumer circuit ──────────

    /// <summary>
    /// Publishes N messages for the SAME ordering key through the PostgreSQL outbox (PerKey mode),
    /// lets the OutboxDispatcher deliver them to RabbitMQ, and asserts the single consumer
    /// receives them in the exact same order they were enqueued.
    ///
    /// A second interleaved key is included to verify that distinct keys do not block each other.
    /// Only per-key order is asserted — cross-key interleaving is expected and allowed.
    /// </summary>
    [Fact]
    public async Task PerKeyOrdering_FullCircuit_ConsumerReceivesInPublishedOrder()
    {
        // ── Constants ──────────────────────────────────────────────────────────
        const string exchange = "ordering-test.events";
        const string queue = "ordering-test.consumer";
        const string orderingKeyHeader = "x-ordering-key";
        const string keyA = "order-key-A";
        const string keyB = "order-key-B";
        const int messagesPerKey = 4;  // total per key: 4+4 = 8 messages

        // Consumer receives (orderingKey, sequenceNumber) pairs in arrival order.
        var received = new ConcurrentQueue<(string Key, int Seq)>();
        using var allConsumed = new SemaphoreSlim(0);
        int expectedTotal = messagesPerKey * 2;

        // ── Schema ────────────────────────────────────────────────────────────
        OutboxOptions outboxOptions = BuildPerKeyOptions(orderingKeyHeader);

        await using OutboxDbContext schemaCtx = CreateDbContextWithOrdering(_pgConnectionString!, outboxOptions);
        await schemaCtx.Database.EnsureCreatedAsync();

        // ── In-process host: BareWire bus + outbox dispatcher + consumer ──────
        // SINGLE CONSUMER (PrefetchCount=1) ensures FIFO delivery from RabbitMQ queue.
        // Multiple consumers would round-robin and legitimately reorder within a key.
        IHost host = BuildInProcessHost(
            rabbitMqUri: _rabbitMqConnectionString!,
            pgConnectionString: _pgConnectionString!,
            exchange: exchange,
            queue: queue,
            orderingKeyHeader: orderingKeyHeader,
            outboxOptions: outboxOptions,
            onMessageReceived: (key, seq) =>
            {
                received.Enqueue((key, seq));
                if (received.Count >= expectedTotal)
                {
                    allConsumed.Release();
                }
            });

        await host.StartAsync();

        try
        {
            // ── Seed outbox rows with PerKey ordering keys ────────────────────
            // Insert key-A and key-B rows interleaved so the dispatcher sees both keys
            // in a single poll cycle. Insertion order: A1, B1, A2, B2, A3, B3, A4, B4.
            // The dispatcher must deliver A messages in A1→A2→A3→A4 order, and B messages
            // in B1→B2→B3→B4 order, but A and B rows may interleave across each other.
            await using OutboxDbContext seedCtx = CreateDbContextWithOrdering(_pgConnectionString!, outboxOptions);
            await SeedInterleavedOrderedRowsAsync(
                seedCtx, exchange, orderingKeyHeader, keyA, keyB, messagesPerKey);

            // ── Wait for all messages to be consumed (bounded poll: 30 s) ─────
            bool consumed = await allConsumed.WaitAsync(TimeSpan.FromSeconds(30));

            // ── Assert ────────────────────────────────────────────────────────
            var allReceived = received.ToList(); // snapshot

            consumed.Should().BeTrue(
                $"consumer should receive all {expectedTotal} messages within 30 s; " +
                $"received so far: {allReceived.Count}. " +
                $"Received order: [{string.Join(", ", allReceived.Select(r => $"{r.Key}:{r.Seq}"))}]");

            // Separate by key and assert strict per-key ordering.
            var receivedKeyA = allReceived.Where(r => r.Key == keyA).Select(r => r.Seq).ToList();
            var receivedKeyB = allReceived.Where(r => r.Key == keyB).Select(r => r.Seq).ToList();

            var expectedKeyA = Enumerable.Range(0, messagesPerKey).ToList();
            var expectedKeyB = Enumerable.Range(0, messagesPerKey).ToList();

            receivedKeyA.Should().Equal(
                expectedKeyA,
                $"messages for key '{keyA}' must arrive in enqueued order " +
                $"(0→{messagesPerKey - 1}); got [{string.Join(", ", receivedKeyA)}]");

            receivedKeyB.Should().Equal(
                expectedKeyB,
                $"messages for key '{keyB}' must arrive in enqueued order " +
                $"(0→{messagesPerKey - 1}); got [{string.Join(", ", receivedKeyB)}]");

            receivedKeyA.Should().HaveCount(messagesPerKey, $"all {messagesPerKey} key-A messages must arrive");
            receivedKeyB.Should().HaveCount(messagesPerKey, $"all {messagesPerKey} key-B messages must arrive");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

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

    private static OutboxDbContext CreateDbContextWithOrdering(string connectionString, OutboxOptions options)
    {
        var ob = new DbContextOptionsBuilder<OutboxDbContext>();
        ob.UseNpgsql(connectionString);
        ((Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsBuilderInfrastructure)ob)
            .AddOrUpdateExtension(new OutboxModelCustomizerExtension(options));
        return new OutboxDbContext(ob.Options);
    }

    /// <summary>
    /// Seeds rows interleaved across two keys: A0, B0, A1, B1, … so both keys appear in
    /// the same claim batch. The sequence number is embedded in the JSON payload so the
    /// consumer can verify ordering without needing a separate sequence header.
    /// </summary>
    private static async Task SeedInterleavedOrderedRowsAsync(
        OutboxDbContext context,
        string exchangeName,
        string orderingKeyHeader,
        string keyA,
        string keyB,
        int countPerKey)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string routingKey = string.Empty; // fanout — routing key is ignored

        for (int i = 0; i < countPerKey; i++)
        {
            // Key-A row
            context.OutboxMessages.Add(new OutboxMessage
            {
                MessageId = Guid.NewGuid(),
                DestinationAddress = routingKey,
                ContentType = "application/json",
                Payload = SerializePayload(keyA, i),
                Headers = SerializeHeaders(exchangeName, orderingKeyHeader, keyA),
                CreatedAt = now,
                OrderingKey = keyA,
            });

            // Key-B row (interleaved with A)
            context.OutboxMessages.Add(new OutboxMessage
            {
                MessageId = Guid.NewGuid(),
                DestinationAddress = routingKey,
                ContentType = "application/json",
                Payload = SerializePayload(keyB, i),
                Headers = SerializeHeaders(exchangeName, orderingKeyHeader, keyB),
                CreatedAt = now,
                OrderingKey = keyB,
            });
        }

        await context.SaveChangesAsync();
    }

    // Produces {"OrderingKey":"<key>","SequenceNumber":<n>}
    private static byte[] SerializePayload(string orderingKey, int sequenceNumber)
        => Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new OrderedEvent(orderingKey, sequenceNumber)));

    // Headers JSON: {"BW-Exchange":"<exchange>","x-ordering-key":"<key>"}
    // BW-Exchange is picked up by RabbitMqTransportAdapter to route to the correct exchange.
    private static string SerializeHeaders(string exchangeName, string orderingKeyHeader, string key)
        => JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["BW-Exchange"] = exchangeName,
            [orderingKeyHeader] = key,
        });

    private static IHost BuildInProcessHost(
        string rabbitMqUri,
        string pgConnectionString,
        string exchange,
        string queue,
        string orderingKeyHeader,
        OutboxOptions outboxOptions,
        Action<string, int> onMessageReceived)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                // JSON serializer (ADR-001 raw-first)
                services.AddBareWireJsonSerializer();

                // Register the ordered-event consumer so DI can resolve it
                services.AddTransient(_ => new OrderedEventConsumer(onMessageReceived));
                services.AddTransient<IConsumer<OrderedEvent>>(sp =>
                    sp.GetRequiredService<OrderedEventConsumer>());

                // RabbitMQ transport — single endpoint with PrefetchCount=1
                // to guarantee FIFO delivery from the queue.
                Action<IRabbitMqConfigurator> configureRabbitMq = rmq =>
                {
                    rmq.Host(rabbitMqUri);
                    rmq.DefaultExchange(exchange);

                    rmq.ConfigureTopology(t =>
                    {
                        t.DeclareExchange(exchange, ExchangeType.Fanout, durable: true);
                        t.DeclareQueue(queue, durable: true);
                        t.BindExchangeToQueue(exchange, queue, routingKey: "#");
                    });

                    // PrefetchCount=1 — single-consumer FIFO: the broker only delivers the
                    // next message after the current one is acknowledged. With multiple
                    // concurrent consumers or higher prefetch, delivery order is not guaranteed
                    // (see ADR-025 transport caveat). This is the minimal correct configuration
                    // for per-key ordering verification through RabbitMQ.
                    rmq.ReceiveEndpoint(queue, e =>
                    {
                        e.PrefetchCount = 1;
                        e.Consumer<OrderedEventConsumer, OrderedEvent>();
                    });
                };

                services.AddBareWireRabbitMq(configureRabbitMq);
                services.AddBareWire(cfg => cfg.UseRabbitMQ(configureRabbitMq));

                // Transactional outbox with PerKey ordering — AutoCreateSchema=false
                // (schema was created explicitly above from the test)
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
}

// ── In-process message types ───────────────────────────────────────────────────

/// <summary>Lightweight ordered event payload written directly into the outbox.</summary>
internal sealed record OrderedEvent(string OrderingKey, int SequenceNumber);

/// <summary>
/// Records (OrderingKey, SequenceNumber) from each received <see cref="OrderedEvent"/>
/// into a thread-safe sink so the test can assert per-key ordering.
/// </summary>
internal sealed class OrderedEventConsumer(Action<string, int> onReceived) : IConsumer<OrderedEvent>
{
    public Task ConsumeAsync(ConsumeContext<OrderedEvent> context)
    {
        onReceived(context.Message.OrderingKey, context.Message.SequenceNumber);
        return Task.CompletedTask;
    }
}
