# Aspire Integration

BareWire samples are orchestrated using .NET Aspire, which provisions shared infrastructure and wires up service discovery automatically.

## AppHost

The Aspire AppHost declares shared resources and references them from each sample project:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var rabbitmq = builder.AddRabbitMQ("rabbitmq").WithManagementPlugin();
var postgres = builder.AddPostgres("postgres").AddDatabase("barewiredb");

builder.AddProject<BareWire_Samples_BasicPublishConsume>("basic-publish-consume")
    .WithReference(rabbitmq)
    .WithReference(postgres);

builder.AddProject<BareWire_Samples_RequestResponse>("request-response")
    .WithReference(rabbitmq)
    .WithReference(postgres);

// ... all other samples

builder.Build().Run();
```

> See: `samples/BareWire.Samples.AppHost/Program.cs`

## Running All Samples

```bash
dotnet run --project samples/BareWire.Samples.AppHost/
```

This starts:
- A RabbitMQ broker with the management plugin (accessible at `http://localhost:15672`)
- A PostgreSQL instance shared by all samples
- All sample applications with injected connection strings
- The Aspire Dashboard for observability

## Service Defaults

The `BareWire.Samples.ServiceDefaults` project provides shared configuration that each sample references:

```csharp
// In each sample's Program.cs
builder.AddServiceDefaults();
// ...
app.MapServiceDefaults();
```

This registers:
- OpenTelemetry tracing and metrics with OTLP exporter
- Health check endpoints (`/health`, `/health/live`, `/health/ready`)
- BareWire observability integration

> See: `samples/BareWire.Samples.ServiceDefaults/ServiceDefaultsExtensions.cs`

## Running Individual Samples

You can also run individual samples directly, but you'll need to provide your own RabbitMQ and PostgreSQL instances:

```bash
# Start RabbitMQ
docker run -d --name rabbitmq -p 5672:5672 -p 15672:15672 rabbitmq:management

# Start PostgreSQL
docker run -d --name postgres -p 5432:5432 -e POSTGRES_PASSWORD=password postgres

# Run a single sample
dotnet run --project samples/BareWire.Samples.BasicPublishConsume/
```
