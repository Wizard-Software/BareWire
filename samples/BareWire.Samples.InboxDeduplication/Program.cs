// BareWire.Samples.InboxDeduplication — demonstrates inbox deduplication (exactly-once
// consumer processing) with composite key (MessageId + ConsumerType).
//
// What this sample shows:
//   - ADR-001  Raw-first: System.Text.Json serializer, no envelope by default.
//   - ADR-002  Manual topology: exchanges and queues declared explicitly.
//   - Inbox deduplication: TransactionalOutboxMiddleware prevents duplicate processing
//     even when the same message is published multiple times or redelivered by the broker.
//   - Multi-consumer composite key: the same MessageId is processed independently by
//     EmailNotificationConsumer and AuditLogConsumer — each gets its own inbox entry.
//   - Inbox state inspection: GET /inbox shows the composite (MessageId, ConsumerType) entries.
//
// Architecture:
//   POST /payments
//       └→ PublishAsync(PaymentReceived) → RabbitMQ "payment.events" fanout exchange
//
//   EmailNotificationConsumer (queue: "payment-email-notifications")
//       └→ TransactionalOutboxMiddleware: inbox dedup check (exactly-once)
//       └→ INSERT NotificationLog (Type="Email")
//
//   AuditLogConsumer (queue: "payment-audit-log")
//       └→ TransactionalOutboxMiddleware: inbox dedup check (exactly-once)
//       └→ INSERT NotificationLog (Type="Audit")
//
//   POST /payments/duplicate?paymentId=...&messageId=...&amount=...
//       └→ Re-publishes with the same MessageId → both consumers reject (duplicate)
//
//   POST /payments/redeliver?paymentId=...&messageId=...&amount=...
//       └→ Simulates broker redelivery → inbox prevents double processing
//
// Prerequisites (runtime, NOT required to compile):
//   - RabbitMQ broker (default: amqp://guest:guest@localhost:5672/)
//   - PostgreSQL server (default: Host=localhost;Database=barewiredb;Username=postgres;Password=postgres)
//   When running via Aspire AppHost, both are provisioned automatically.

using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire;
using BareWire.Transport.RabbitMQ;
using BareWire.Outbox.EntityFramework;
using BareWire.Samples.ServiceDefaults;
using BareWire.Samples.InboxDeduplication.Consumers;
using BareWire.Samples.InboxDeduplication.Data;
using BareWire.Samples.InboxDeduplication.Messages;
using BareWire.Serialization.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

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
// 3. EF Core — application DbContext for notification log entities
// ─────────────────────────────────────────────────────────────────────────────

builder.Services.AddDbContext<NotificationDbContext>(o => o.UseNpgsql(dbConnectionString));

// ─────────────────────────────────────────────────────────────────────────────
// 4. BareWire messaging — serializer, transport, topology, endpoints
// ─────────────────────────────────────────────────────────────────────────────

// ADR-001: Raw-first — registers SystemTextJsonSerializer (IMessageSerializer)
// and SystemTextJsonRawDeserializer (IMessageDeserializer) as singletons.
builder.Services.AddBareWireJsonSerializer();

// Register both consumers in DI (resolved per-message by ConsumerDispatcher).
builder.Services.AddTransient<EmailNotificationConsumer>();
builder.Services.AddTransient<AuditLogConsumer>();

Action<IRabbitMqConfigurator> configureRabbitMq = rmq =>
{
    // Connection to the RabbitMQ broker.
    rmq.Host(rabbitMqConnectionString);
    rmq.DefaultExchange("payment.events");

    // ADR-002: Manual topology — declare all exchanges, queues, and bindings explicitly.
    // Fanout exchange: every PaymentReceived published to "payment.events" is delivered
    // to BOTH consumer queues — each consumer processes independently.
    rmq.ConfigureTopology(t =>
    {
        t.DeclareExchange("payment.events", ExchangeType.Fanout, durable: true);

        // Queue for email notifications.
        t.DeclareQueue("payment-email-notifications", durable: true);
        t.BindExchangeToQueue("payment.events", "payment-email-notifications", routingKey: "#");

        // Queue for audit log entries.
        t.DeclareQueue("payment-audit-log", durable: true);
        t.BindExchangeToQueue("payment.events", "payment-audit-log", routingKey: "#");
    });

    // Endpoint 1: EmailNotificationConsumer processes PaymentReceived events.
    // Inbox deduplication is applied transparently by TransactionalOutboxMiddleware.
    rmq.ReceiveEndpoint("payment-email-notifications", e =>
    {
        e.Consumer<EmailNotificationConsumer, PaymentReceived>();
    });

    // Endpoint 2: AuditLogConsumer processes the same PaymentReceived events independently.
    // The inbox composite key (MessageId + ConsumerType) allows both consumers to process
    // the same message — but each only once.
    rmq.ReceiveEndpoint("payment-audit-log", e =>
    {
        e.Consumer<AuditLogConsumer, PaymentReceived>();
    });
};

builder.Services.AddBareWireRabbitMq(configureRabbitMq);
builder.Services.AddBareWire(cfg =>
{
    cfg.UseRabbitMQ(configureRabbitMq);
});

// ─────────────────────────────────────────────────────────────────────────────
// 5. Transactional outbox / inbox (EF Core + PostgreSQL)
// ─────────────────────────────────────────────────────────────────────────────

// This sample focuses on the INBOX side of AddBareWireOutbox.
// The outbox dispatcher is still registered (needed by the middleware pipeline),
// but the key feature here is inbox deduplication with composite key behavior.
builder.Services.AddBareWireOutbox(
    configureDbContext: options => options.UseNpgsql(dbConnectionString),
    configureOutbox: outbox =>
    {
        // Short lock timeout for demo — in production, use 30 s+ (default).
        outbox.InboxLockTimeout = TimeSpan.FromSeconds(10);

        // Short retention for demo — inbox entries cleaned up after 1 hour.
        outbox.InboxRetention = TimeSpan.FromHours(1);

        // Standard outbox settings (not the focus of this sample).
        outbox.PollingInterval = TimeSpan.FromSeconds(1);
        outbox.DispatchBatchSize = 100;

        // Automatically create Outbox/Inbox tables at host startup (development convenience).
        outbox.AutoCreateSchema = true;
    });

// ─────────────────────────────────────────────────────────────────────────────
// 6. Build the application
// ─────────────────────────────────────────────────────────────────────────────

WebApplication app = builder.Build();

// Development only — use migrations in production.
// CreateTablesAsync creates tables for this DbContext model only (unlike EnsureCreatedAsync
// which is a no-op when any table already exists in a shared database).
using (IServiceScope scope = app.Services.CreateScope())
{
    NotificationDbContext notificationDb = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
    try
    {
        var creator = notificationDb.Database.GetInfrastructure().GetRequiredService<IRelationalDatabaseCreator>();
        await creator.CreateTablesAsync().ConfigureAwait(false);
    }
    catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P07")
    {
        // Tables already exist from a previous run — safe to ignore in development.
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 7. HTTP endpoints
// ─────────────────────────────────────────────────────────────────────────────

// Health check endpoints: /health, /health/live, /health/ready.
app.MapServiceDefaults();

// POST /payments — publish a PaymentReceived event to the bus.
// Both EmailNotificationConsumer and AuditLogConsumer will process it (once each).
// Returns both PaymentId and MessageId — use MessageId with /duplicate and /redeliver endpoints
// to demonstrate inbox deduplication with the original message identity.
app.MapPost("/payments", async (
    PaymentRequest request,
    IPublishEndpoint bus,
    CancellationToken cancellationToken) =>
{
    string paymentId = Guid.NewGuid().ToString("N");
    Guid messageId = Guid.NewGuid();
    DateTime now = DateTime.UtcNow;

    // Pass the pre-generated message-id so the caller can reference it later.
    var headers = new Dictionary<string, string> { ["message-id"] = messageId.ToString() };
    await bus.PublishAsync(
        new PaymentReceived(paymentId, request.Payer, request.Payee, request.Amount, now),
        headers,
        cancellationToken).ConfigureAwait(false);

    return Results.Accepted(value: new
    {
        PaymentId = paymentId,
        MessageId = messageId,
        Note = "PaymentReceived published. Both Email and Audit consumers will process it exactly once. " +
               $"Use /duplicate?paymentId={paymentId}&messageId={messageId}&amount={request.Amount} " +
               $"or /redeliver?paymentId={paymentId}&messageId={messageId}&amount={request.Amount} " +
               "to test inbox deduplication."
    });
})
.Produces(StatusCodes.Status202Accepted)
.WithName("PublishPayment");

// POST /payments/duplicate?paymentId=...&messageId=...&amount=... — re-publish with the original MessageId.
// The inbox rejects processing for both consumers because entries already exist
// for this (MessageId, ConsumerType) composite key.
app.MapPost("/payments/duplicate", async (
    string paymentId,
    Guid messageId,
    decimal amount,
    IPublishEndpoint bus,
    CancellationToken cancellationToken) =>
{
    // Re-use the original message-id so the inbox recognises this as a duplicate.
    var headers = new Dictionary<string, string> { ["message-id"] = messageId.ToString() };
    await bus.PublishAsync(
        new PaymentReceived(paymentId, "Alice", "Bob", amount, DateTime.UtcNow),
        headers,
        cancellationToken).ConfigureAwait(false);

    return Results.Ok(new
    {
        PaymentId = paymentId,
        MessageId = messageId,
        Amount = amount,
        Note = $"Duplicate PaymentReceived (amount: {amount}) published with the same MessageId. " +
               "Check logs — inbox deduplication will reject both consumers."
    });
})
.Produces(StatusCodes.Status200OK)
.WithName("PublishDuplicate");

// POST /payments/redeliver?paymentId=...&messageId=...&amount=... — simulates a broker redelivery scenario.
// In production, RabbitMQ might redeliver a message after a consumer crash or nack.
// The inbox prevents the handler from running a second time for the same (MessageId, ConsumerType).
app.MapPost("/payments/redeliver", async (
    string paymentId,
    Guid messageId,
    decimal amount,
    IPublishEndpoint bus,
    CancellationToken cancellationToken) =>
{
    // Re-use the original message-id to simulate a broker redelivery of the same message.
    var headers = new Dictionary<string, string> { ["message-id"] = messageId.ToString() };
    await bus.PublishAsync(
        new PaymentReceived(paymentId, "Alice", "Bob", amount, DateTime.UtcNow),
        headers,
        cancellationToken).ConfigureAwait(false);

    return Results.Ok(new
    {
        PaymentId = paymentId,
        MessageId = messageId,
        Amount = amount,
        Note = $"Simulated redelivery (amount: {amount}) with the same MessageId. " +
               "Inbox prevents double processing — check logs for 'duplicate' entries."
    });
})
.Produces(StatusCodes.Status200OK)
.WithName("SimulateRedelivery");

// GET /notifications — list all processed notification logs from PostgreSQL.
// After a single payment, you should see exactly 2 entries: one Email, one Audit.
// After duplicate/redeliver attempts, the count stays the same.
app.MapGet("/notifications", async (
    NotificationDbContext dbContext,
    CancellationToken cancellationToken) =>
{
    List<NotificationLog> notifications = await dbContext.NotificationLogs
        .OrderByDescending(n => n.ProcessedAt)
        .ToListAsync(cancellationToken)
        .ConfigureAwait(false);

    return Results.Ok(new
    {
        Count = notifications.Count,
        Notifications = notifications
    });
})
.Produces<object>()
.WithName("GetNotifications");

// GET /inbox — inspect the inbox table to see deduplication entries.
// Each row has a composite key (MessageId, ConsumerType). After one payment,
// you should see 2 rows: same MessageId, different ConsumerType values.
app.MapGet("/inbox", async (
    OutboxDbContext outboxDb,
    CancellationToken cancellationToken) =>
{
    var inboxEntries = await outboxDb.Database
        .SqlQuery<InboxEntry>(
            $"""
            SELECT "MessageId", "ConsumerType", "ReceivedAt", "ProcessedAt", "ExpiresAt"
            FROM "InboxMessages"
            ORDER BY "ReceivedAt" DESC
            """)
        .ToListAsync(cancellationToken)
        .ConfigureAwait(false);

    return Results.Ok(new
    {
        Count = inboxEntries.Count,
        InboxEntries = inboxEntries,
        Note = "Each (MessageId, ConsumerType) pair is unique. " +
               "Same MessageId appears once per consumer type."
    });
})
.Produces<object>()
.WithName("GetInboxEntries");

app.Run();

// ─────────────────────────────────────────────────────────────────────────────
// Supporting types
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Request body for POST /payments.</summary>
internal sealed record PaymentRequest(string Payer, string Payee, decimal Amount);

/// <summary>Projection for raw SQL query against InboxMessages table.</summary>
internal sealed record InboxEntry(
    Guid MessageId,
    string ConsumerType,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ProcessedAt,
    DateTimeOffset ExpiresAt);
