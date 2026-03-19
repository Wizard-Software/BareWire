// BareWire.Samples.TransactionalOutbox — demonstrates exactly-once delivery via
// Transactional Outbox + Inbox deduplication.
//
// What this sample shows:
//   - ADR-001  Raw-first: System.Text.Json serializer, no envelope by default.
//   - ADR-002  Manual topology: exchange and queue declared explicitly.
//   - Transactional Outbox: business entity + outbox message written atomically in one SaveChangesAsync().
//   - Inbox deduplication: TransactionalOutboxMiddleware prevents duplicate processing on redelivery.
//   - OutboxDispatcher: background service polls outbox table every 1 s, dispatches in batches of 100.
//   - OutboxCleanupService: background service purges delivered records after the retention window.
//   - Resilience demo: if RabbitMQ is unavailable, messages wait in PostgreSQL; delivered after reconnect.
//
// Architecture:
//   POST /transfers
//       └→ EF Core: INSERT Transfer (status="Pending") + INSERT OutboxMessage — single SaveChangesAsync()
//
//   OutboxDispatcher (background, polls every 1 s)
//       └→ reads pending OutboxMessages → publishes TransferInitiated to RabbitMQ
//
//   TransferConsumer (queue: "transfers")
//       └→ TransactionalOutboxMiddleware: inbox dedup check (exactly-once)
//       └→ UPDATE Transfer status="Completed" — committed atomically
//
// Prerequisites (runtime, NOT required to compile):
//   - RabbitMQ broker (default: amqp://guest:guest@localhost:5672/)
//   - PostgreSQL server (default: Host=localhost;Database=barewiredb;Username=postgres;Password=postgres)
//   When running via Aspire AppHost, both are provisioned automatically.

using BareWire.Abstractions;
using BareWire.Core;
using BareWire.Outbox.EntityFramework;
using BareWire.Samples.ServiceDefaults;
using BareWire.Samples.TransactionalOutbox.Consumers;
using BareWire.Samples.TransactionalOutbox.Data;
using BareWire.Samples.TransactionalOutbox.Messages;
using BareWire.Samples.TransactionalOutbox.Models;
using BareWire.Serialization.Json;
using Microsoft.EntityFrameworkCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ─────────────────────────────────────────────────────────────────────────────
// 1. Shared defaults: OpenTelemetry observability + health checks
// ─────────────────────────────────────────────────────────────────────────────

builder.AddServiceDefaults();

// ─────────────────────────────────────────────────────────────────────────────
// 2. Configuration
// ─────────────────────────────────────────────────────────────────────────────

string rabbitMqConnectionString =
    builder.Configuration.GetConnectionString("rabbitmq")
    ?? "amqp://guest:guest@localhost:5672/";

string dbConnectionString =
    builder.Configuration.GetConnectionString("barewiredb")
    ?? "Host=localhost;Database=barewiredb;Username=postgres;Password=postgres";

// ─────────────────────────────────────────────────────────────────────────────
// 3. EF Core — application DbContext for Transfer entities
// ─────────────────────────────────────────────────────────────────────────────

builder.Services.AddDbContext<TransferDbContext>(o => o.UseNpgsql(dbConnectionString));

// ─────────────────────────────────────────────────────────────────────────────
// 4. BareWire messaging — serializer, transport, topology, endpoints
// ─────────────────────────────────────────────────────────────────────────────

// ADR-001: Raw-first — registers SystemTextJsonSerializer (IMessageSerializer)
// and SystemTextJsonRawDeserializer (IMessageDeserializer) as singletons.
builder.Services.AddBareWireJsonSerializer();

// Register the consumer in DI (resolved per-message by ConsumerDispatcher).
builder.Services.AddTransient<TransferConsumer>();

builder.Services.AddBareWire(cfg =>
{
    cfg.UseRabbitMQ(rmq =>
    {
        // Connection to the RabbitMQ broker.
        rmq.Host(rabbitMqConnectionString);

        // ADR-002: Manual topology — declare all exchanges, queues, and bindings explicitly.
        rmq.ConfigureTopology(t =>
        {
            // Fanout exchange — every TransferInitiated published to "transfer.events"
            // is delivered to the "transfers" consumer queue.
            t.DeclareExchange("transfer.events", ExchangeType.Fanout, durable: true);

            t.DeclareQueue("transfers", durable: true);
            t.BindExchangeToQueue("transfer.events", "transfers", routingKey: "#");
        });

        // Endpoint: TransferConsumer processes TransferInitiated events.
        // Inbox deduplication is applied transparently by TransactionalOutboxMiddleware.
        rmq.ReceiveEndpoint("transfers", e =>
        {
            e.Consumer<TransferConsumer, TransferInitiated>();
        });
    });
});

// ─────────────────────────────────────────────────────────────────────────────
// 5. Transactional outbox / inbox (EF Core + PostgreSQL)
// ─────────────────────────────────────────────────────────────────────────────

// Registers OutboxDbContext (its own DbContext, same connection string as TransferDbContext
// so they share the same PostgreSQL database and participate in the same TransactionScope).
// Also registers: OutboxDispatcher (polls every 1 s), OutboxCleanupService (retention cleanup),
// TransactionalOutboxMiddleware (inbox dedup + atomic commit), EfCoreOutboxStore, EfCoreInboxStore.
builder.Services.AddBareWireOutbox(
    configureDbContext: options => options.UseNpgsql(dbConnectionString),
    configureOutbox: outbox =>
    {
        // How often OutboxDispatcher wakes up to forward pending messages to RabbitMQ.
        outbox.PollingInterval = TimeSpan.FromSeconds(1);

        // Maximum number of outbox messages dispatched per polling cycle.
        outbox.DispatchBatchSize = 100;
    });

// ─────────────────────────────────────────────────────────────────────────────
// 6. Build the application
// ─────────────────────────────────────────────────────────────────────────────

WebApplication app = builder.Build();

// Development only — use migrations in production.
using (IServiceScope scope = app.Services.CreateScope())
{
    TransferDbContext transferDb = scope.ServiceProvider.GetRequiredService<TransferDbContext>();
    await transferDb.Database.EnsureCreatedAsync().ConfigureAwait(false);

    // Ensure the outbox/inbox schema is also created in the same database.
    OutboxDbContext outboxDb = scope.ServiceProvider.GetRequiredService<OutboxDbContext>();
    await outboxDb.Database.EnsureCreatedAsync().ConfigureAwait(false);
}

// ─────────────────────────────────────────────────────────────────────────────
// 7. HTTP endpoints
// ─────────────────────────────────────────────────────────────────────────────

// Health check endpoints: /health, /health/live, /health/ready.
app.MapServiceDefaults();

// POST /transfers — create a Transfer entity + publish TransferInitiated via outbox, atomically.
//
// The key pattern:
//   1. INSERT Transfer (status = "Pending") into application table.
//   2. INSERT OutboxMessage into outbox table (via TransactionalOutboxMiddleware / outbox buffer).
//   3. Single SaveChangesAsync() — both rows committed in one transaction.
//   4. OutboxDispatcher picks up the outbox row and publishes to RabbitMQ within ~1 s.
//
// If RabbitMQ is down, the Transfer is still saved; it will be delivered once the broker restarts.
app.MapPost("/transfers", async (
    TransferRequest request,
    TransferDbContext dbContext,
    IPublishEndpoint bus,
    CancellationToken cancellationToken) =>
{
    string transferId = Guid.NewGuid().ToString("N");
    DateTime now = DateTime.UtcNow;

    // 1. Create the Transfer business entity (status = Pending).
    Transfer transfer = new()
    {
        TransferId = transferId,
        FromAccount = request.FromAccount,
        ToAccount = request.ToAccount,
        Amount = request.Amount,
        Status = "Pending",
        CreatedAt = now
    };
    dbContext.Transfers.Add(transfer);

    // 2. Publish TransferInitiated via the outbox.
    //    TransactionalOutboxMiddleware intercepts PublishAsync and buffers the message.
    //    Both the Transfer row and the OutboxMessage row are committed together below.
    await bus.PublishAsync(
        new TransferInitiated(transferId, request.FromAccount, request.ToAccount, request.Amount, now),
        cancellationToken).ConfigureAwait(false);

    // 3. Atomic commit: INSERT Transfer + INSERT OutboxMessage in one transaction.
    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

    return Results.Accepted(
        $"/transfers/{transferId}",
        new { TransferId = transferId, Status = "Pending" });
})
.Produces(StatusCodes.Status202Accepted)
.WithName("InitiateTransfer");

// GET /transfers — list all transfers from PostgreSQL with their current status.
// Status transitions: Pending → Completed (set by TransferConsumer after RabbitMQ delivery).
app.MapGet("/transfers", async (
    TransferDbContext dbContext,
    CancellationToken cancellationToken) =>
{
    List<Transfer> transfers = await dbContext.Transfers
        .OrderByDescending(t => t.CreatedAt)
        .ToListAsync(cancellationToken)
        .ConfigureAwait(false);

    return Results.Ok(transfers);
})
.Produces<List<Transfer>>()
.WithName("GetTransfers");

// GET /outbox/pending — show pending (not yet dispatched) outbox message count.
//
// OutboxDbContext.OutboxMessages is internal to BareWire.Outbox.EntityFramework,
// so we query via raw SQL. This endpoint is for observability only — in production,
// use structured monitoring (metrics, health checks) rather than querying the outbox table directly.
app.MapGet("/outbox/pending", async (
    OutboxDbContext outboxDb,
    CancellationToken cancellationToken) =>
{
    // SqlQuery<T> requires a record/class with matching column names.
    // We use a simple count query to avoid exposing internal OutboxMessage structure.
    int pendingCount = await outboxDb.Database
        .SqlQuery<int>($"SELECT COUNT(*)::int FROM \"OutboxMessages\" WHERE \"DeliveredAt\" IS NULL")
        .FirstOrDefaultAsync(cancellationToken)
        .ConfigureAwait(false);

    return Results.Ok(new
    {
        PendingCount = pendingCount,
        Note = "OutboxDispatcher polls every 1 s — pending messages are dispatched to RabbitMQ automatically."
    });
})
.Produces<object>()
.WithName("GetOutboxPending");

app.Run();

// ─────────────────────────────────────────────────────────────────────────────
// Supporting types
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Request body for POST /transfers.</summary>
internal sealed record TransferRequest(string FromAccount, string ToAccount, decimal Amount);
