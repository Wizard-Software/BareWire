// Minimal Aspire AppHost for outbox E2E tests.
// Provisions a single PostgreSQL container (no sample application projects).
var builder = DistributedApplication.CreateBuilder(args);

builder.AddPostgres("outbox-pg")
    .WithLifetime(ContainerLifetime.Session)
    .AddDatabase("outbox-db");

builder.Build().Run();
