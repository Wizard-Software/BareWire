// BareWire.Samples.InMemoryModularMonolith — a three-module monolith (Ordering, Billing, Shipping)
// exchanging events over a single BareWire bus, switchable between the in-memory transport and
// RabbitMQ by changing exactly one registration call.
//
// What this sample shows:
//   - A modular monolith: three modules in one process, sharing one topology and one set of
//     consumers, with the transport selected by the "Transport" configuration key.
//   - Fanout + topic routing: OrderPlaced fans out to both Billing and Shipping; Billing's
//     PaymentCaptured is routed to Shipping via a topic exchange and binding pattern.
//   - Transactional outbox + inbox (EF Core + SQLite): the outbox is registered so the inbox
//     deduplicates deliveries into the fan-out/topic consumers by message id. A publish made from
//     inside a consumer (Billing's PaymentCaptured) goes directly to the transport, NOT through the
//     outbox buffer — see the remark on OrderPlacedBillingConsumer.
//   - Graceful shutdown: ApplicationStopping / ApplicationStopped are logged, and the in-memory
//     transport's DrainTimeout is kept below HostOptions.ShutdownTimeout.
//   - A bounded smoke mode (Smoke:Enabled=true) that runs end-to-end without a broker, without
//     Docker, and without an HTTP client — see Smoke/SmokeRunner.cs.
//
// Architecture:
//   POST /orders ─→ OrderingModule.PlaceOrderAsync ─→ PublishAsync(OrderPlaced)
//        exchange "modulith.ordering" (Fanout)
//           ├─→ queue "billing.order-placed"  ─→ OrderPlacedBillingConsumer
//           │        └─ PaymentLedger.TryRecord + PublishAsync(PaymentCaptured) [direct, not outbox]
//           │              exchange "modulith.billing" (Topic), routing key "billing.payment.captured"
//           │                 └─→ queue "shipping.payment-captured" ─→ PaymentCapturedShippingConsumer
//           └─→ queue "shipping.order-placed" ─→ OrderPlacedShippingConsumer
//   ShipmentBoard: OrderReceived + PaymentConfirmed ⇒ ReadyToShip
//   GET /orders/{orderId} ─→ { orderId, billingCaptured, shippingStatus, transport }
//
// Prerequisites (runtime, NOT required to compile):
//   - Transport=InMemory (default): none — runs standalone, including in smoke mode.
//   - Transport=RabbitMQ: a RabbitMQ broker at ConnectionStrings:rabbitmq (provisioned automatically
//     by the Aspire AppHost).

using System.Text.RegularExpressions;

using BareWire.Outbox.EntityFramework;
using BareWire.Samples.InMemoryModularMonolith.Messaging;
using BareWire.Samples.InMemoryModularMonolith.Modules.Billing;
using BareWire.Samples.InMemoryModularMonolith.Modules.Ordering;
using BareWire.Samples.InMemoryModularMonolith.Modules.Shipping;
using BareWire.Samples.InMemoryModularMonolith.Smoke;
using BareWire.Samples.ServiceDefaults;
using BareWire.Serialization.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ─────────────────────────────────────────────────────────────────────────────
// 1. Configuration
// ─────────────────────────────────────────────────────────────────────────────

builder.AddServiceDefaults();

TransportKind transport = TransportKindParser.Parse(builder.Configuration["Transport"]);

string rabbitMqConnectionString =
    builder.Configuration.GetConnectionString("rabbitmq")
    ?? "amqp://guest:guest@localhost:5672/";

// Default SQLite file is per-process (Guid-suffixed, in the temp directory) so two runs never share
// an outbox/inbox database — a shared file would let one process's non-atomic claim steal the other's
// rows. Module state lives in memory only, so the file itself carries nothing worth persisting; it is
// deleted in the `finally` below, after app.Run() returns.
string modulithConnectionString =
    builder.Configuration.GetConnectionString("modulith")
    ?? $"Data Source={Path.Combine(Path.GetTempPath(), $"barewire-inmemory-modular-monolith-{transport}-{Guid.NewGuid():N}.db")}";

builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(15));

// ─────────────────────────────────────────────────────────────────────────────
// 2. BareWire messaging — serializer, transport, topology, endpoints
// ─────────────────────────────────────────────────────────────────────────────

// Raw-first: registers SystemTextJsonSerializer / SystemTextJsonRawDeserializer. No envelope by default.
builder.Services.AddBareWireJsonSerializer();

builder.Services.TryAddSingleton(TimeProvider.System);

// Module state — bounded singletons (see PaymentLedger / ShipmentBoard capacity handling).
builder.Services.AddSingleton<PaymentLedger>();
builder.Services.AddSingleton<ShipmentBoard>();

// OrderingModule depends on IPublishEndpoint, which is itself registered as a singleton — a Scoped
// OrderingModule injected into the singleton SmokeRunner would fail DI scope validation at Build()
// in Development.
builder.Services.AddSingleton<OrderingModule>();

// Consumers are resolved per-message from DI.
builder.Services.AddTransient<OrderPlacedBillingConsumer>();
builder.Services.AddTransient<OrderPlacedShippingConsumer>();
builder.Services.AddTransient<PaymentCapturedShippingConsumer>();

// The ONLY place the two transports diverge — see Messaging/TransportRegistration.cs.
builder.Services.AddModulithTransport(transport, rabbitMqConnectionString);

// ─────────────────────────────────────────────────────────────────────────────
// 3. Transactional outbox / inbox (EF Core + SQLite)
// ─────────────────────────────────────────────────────────────────────────────

// Registers the outbox AND the inbox: redeliveries into the fan-out/topic consumers above are
// deduplicated by message id. A publish made from inside a consumer (OrderPlacedBillingConsumer's
// PaymentCaptured) is NOT buffered by this outbox — see the remark on that consumer.
builder.Services.AddBareWireOutbox(
    configureDbContext: options => options
        .UseSqlite(modulithConnectionString)
        // Microsoft.Data.Sqlite does not support enlisting in a System.Transactions ambient
        // transaction (there is no distributed/ambient-transaction support in the SQLite ADO.NET
        // provider at all). BareWire's transactional-outbox middleware wraps every consume in a
        // TransactionScope so a consumer's OWN DbContext write can commit atomically with the
        // outbox/inbox writes — this sample has no such write (module state lives in memory only),
        // so there is nothing that needs that atomicity here. Without this suppression, EVERY
        // message would fail at the inbox "mark processed" step with an
        // AmbientTransactionWarning-turned-exception; ignoring it lets that write commit on its own
        // (still correct — just not atomic with anything, which nothing here requires).
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.AmbientTransactionWarning)),
    configureOutbox: outbox =>
    {
        outbox.PollingInterval = TimeSpan.FromMilliseconds(200);
        outbox.DispatchBatchSize = 100;

        // Automatically create Outbox/Inbox tables at host startup (development convenience).
        outbox.AutoCreateSchema = true;

        // This is a single-instance / development demo running on SQLite.
        // Production multi-instance deployments require an atomic provider (PostgreSQL) — see README.
        // Without this flag, the startup guard would throw BareWireConfigurationException because
        // the default dialects target PostgreSQL and SQLite has no matching atomic dialect.
        outbox.AllowNonAtomicProvider = true;
    });

// ─────────────────────────────────────────────────────────────────────────────
// 4. Smoke mode — opt-in bounded end-to-end run, no broker and no HTTP client required
// ─────────────────────────────────────────────────────────────────────────────

if (builder.Configuration.GetValue("Smoke:Enabled", false))
{
    builder.Services.AddHostedService<SmokeRunner>();
}

// ─────────────────────────────────────────────────────────────────────────────
// 5. Build the application
// ─────────────────────────────────────────────────────────────────────────────

WebApplication app = builder.Build();

ILogger<Program> startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
IHostApplicationLifetime lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

lifetime.ApplicationStopping.Register(() => ProgramLog.GracefulShutdownStarted(startupLogger, transport));
lifetime.ApplicationStopped.Register(() => ProgramLog.GracefulShutdownCompleted(startupLogger));

ProgramLog.ModularMonolithStarted(startupLogger, transport);

// ─────────────────────────────────────────────────────────────────────────────
// 6. HTTP endpoints
// ─────────────────────────────────────────────────────────────────────────────

app.MapServiceDefaults();

// POST /orders — places an order via OrderingModule (the same path the smoke runner uses).
app.MapPost("/orders", async (
    PlaceOrderRequest request,
    OrderingModule ordering,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.CustomerId)
        || !OrderValidation.CustomerIdPattern().IsMatch(request.CustomerId))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["customerId"] = ["CustomerId must be 1-64 characters from [A-Za-z0-9._-]."],
        });
    }

    if (request.Amount <= 0)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["amount"] = ["Amount must be greater than zero."],
        });
    }

    string orderId = await ordering
        .PlaceOrderAsync(request.CustomerId, request.Amount, cancellationToken)
        .ConfigureAwait(false);

    return Results.Accepted($"/orders/{orderId}", new { orderId });
})
.Produces(StatusCodes.Status202Accepted)
.ProducesValidationProblem()
.WithName("PlaceOrder");

// GET /orders/{orderId} — combined view across Billing and Shipping module state.
app.MapGet("/orders/{orderId}", (
    string orderId,
    PaymentLedger ledger,
    ShipmentBoard board) =>
{
    bool billingCaptured = ledger.IsCaptured(orderId);
    ShipmentStatus? shippingStatus = board.GetStatus(orderId);

    if (!billingCaptured && shippingStatus is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(new
    {
        orderId,
        billingCaptured,
        shippingStatus = shippingStatus?.ToString(),
        transport = transport.ToString(),
    });
})
.Produces<object>()
.Produces(StatusCodes.Status404NotFound)
.WithName("GetOrderStatus");

try
{
    app.Run();
}
finally
{
    CleanupSqliteFile(modulithConnectionString);
}

// ─────────────────────────────────────────────────────────────────────────────
// Local functions
// ─────────────────────────────────────────────────────────────────────────────

static void CleanupSqliteFile(string connectionString)
{
    // Best-effort: module state lives in memory only, so the SQLite file is a disposable dev
    // artifact — never something the working tree or a shared temp directory should retain.
    SqliteConnection.ClearAllPools();

    var sqliteBuilder = new SqliteConnectionStringBuilder(connectionString);
    string dbPath = sqliteBuilder.DataSource;
    if (string.IsNullOrEmpty(dbPath))
    {
        return;
    }

    foreach (string suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
    {
        string candidate = dbPath + suffix;
        try
        {
            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup — the file may still be transiently locked.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Supporting types
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Request body for <c>POST /orders</c>.</summary>
internal sealed record PlaceOrderRequest(string CustomerId, decimal Amount);

/// <summary>Validation helpers for <c>POST /orders</c>.</summary>
internal static partial class OrderValidation
{
    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$")]
    public static partial Regex CustomerIdPattern();
}

/// <summary>Source-generated startup/shutdown log messages for the top-level <c>Program</c>.</summary>
internal static partial class ProgramLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Modular monolith started with transport {Transport}")]
    public static partial void ModularMonolithStarted(ILogger logger, TransportKind transport);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Graceful shutdown started: draining in-flight messages ({Transport})")]
    public static partial void GracefulShutdownStarted(ILogger logger, TransportKind transport);

    [LoggerMessage(Level = LogLevel.Information, Message = "Graceful shutdown completed")]
    public static partial void GracefulShutdownCompleted(ILogger logger);
}
