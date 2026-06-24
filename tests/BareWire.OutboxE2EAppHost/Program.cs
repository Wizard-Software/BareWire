// Minimal Aspire AppHost for outbox E2E tests.
// Provisions a PostgreSQL container and a RabbitMQ container (no sample application projects).
var builder = DistributedApplication.CreateBuilder(args);

var outboxPg = builder.AddPostgres("outbox-pg")
    .WithLifetime(ContainerLifetime.Session);

// Two logical databases on the same server so test classes that share the session
// container do not collide: OutboxClaimE2ETests uses "outbox-db"; the full-circuit
// ordering test (which runs a polling OutboxDispatcher) uses its own "outbox-flow-db"
// so its dispatcher never claims rows under test in the other class (classes run in parallel).
outboxPg.AddDatabase("outbox-db");
outboxPg.AddDatabase("outbox-flow-db");

builder.AddRabbitMQ("outbox-rabbitmq")
    .WithLifetime(ContainerLifetime.Session);

builder.Build().Run();
