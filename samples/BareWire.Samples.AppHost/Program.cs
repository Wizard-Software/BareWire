var builder = DistributedApplication.CreateBuilder(args);

// ── Infrastructure ──────────────────────────────────────────────────────────
// ContainerLifetime.Session ensures fresh containers on every AppHost run —
// no stale queues or database rows from previous sessions.
// Change to ContainerLifetime.Persistent if you need data to survive restarts
// (e.g. testing long-running sagas or debugging specific message sequences).
var rabbitmq = builder.AddRabbitMQ("rabbitmq")
    .WithLifetime(ContainerLifetime.Session)
    .WithManagementPlugin();

var postgres = builder.AddPostgres("postgres")
    .WithLifetime(ContainerLifetime.Session)
    .AddDatabase("barewiredb");

builder.AddProject<Projects.BareWire_Samples_BasicPublishConsume>("basic-publish-consume")
    .WithReference(rabbitmq)
    .WithReference(postgres)
    .WaitFor(rabbitmq)
    .WaitFor(postgres);

builder.AddProject<Projects.BareWire_Samples_RequestResponse>("request-response")
    .WithReference(rabbitmq)
    .WithReference(postgres)
    .WaitFor(rabbitmq)
    .WaitFor(postgres);

builder.AddProject<Projects.BareWire_Samples_RawMessageInterop>("raw-message-interop")
    .WithReference(rabbitmq)
    .WithReference(postgres)
    .WaitFor(rabbitmq)
    .WaitFor(postgres);

builder.AddProject<Projects.BareWire_Samples_SagaOrderFlow>("saga-order-flow")
    .WithReference(rabbitmq)
    .WithReference(postgres)
    .WaitFor(rabbitmq)
    .WaitFor(postgres);

builder.AddProject<Projects.BareWire_Samples_TransactionalOutbox>("transactional-outbox")
    .WithReference(rabbitmq)
    .WithReference(postgres)
    .WaitFor(rabbitmq)
    .WaitFor(postgres);

builder.AddProject<Projects.BareWire_Samples_RetryAndDlq>("retry-and-dlq")
    .WithReference(rabbitmq)
    .WithReference(postgres)
    .WaitFor(rabbitmq)
    .WaitFor(postgres);

builder.AddProject<Projects.BareWire_Samples_BackpressureDemo>("backpressure-demo")
    .WithReference(rabbitmq)
    .WithReference(postgres)
    .WaitFor(rabbitmq)
    .WaitFor(postgres);

builder.AddProject<Projects.BareWire_Samples_ObservabilityShowcase>("observability-showcase")
    .WithReference(rabbitmq)
    .WithReference(postgres)
    .WaitFor(rabbitmq)
    .WaitFor(postgres);

builder.AddProject<Projects.BareWire_Samples_MultiConsumerPartitioning>("multi-consumer-partitioning")
    .WithReference(rabbitmq)
    .WithReference(postgres)
    .WaitFor(rabbitmq)
    .WaitFor(postgres);

builder.AddProject<Projects.BareWire_Samples_InboxDeduplication>("inbox-deduplication")
    .WithReference(rabbitmq)
    .WithReference(postgres)
    .WaitFor(rabbitmq)
    .WaitFor(postgres);

builder.AddProject<Projects.BareWire_Samples_RabbitMQ>("rabbitmq-sample")
    .WithReference(rabbitmq)
    .WaitFor(rabbitmq);

builder.AddProject<Projects.BareWire_Samples_MassTransitInterop>("masstransit-interop")
    .WithReference(rabbitmq)
    .WaitFor(rabbitmq);

builder.AddProject<Projects.BareWire_Samples_MassTransitRequestResponse>("masstransit-request-response")
    .WithReference(rabbitmq)
    .WaitFor(rabbitmq);

builder.AddProject<Projects.BareWire_Samples_CloudEventsInterop>("cloudevents-interop")
    .WithReference(rabbitmq)
    .WaitFor(rabbitmq);

// Replica count for the ordered-consumers sample. Set to 2 for CI (fastest startup while
// still demonstrating competing consumers + SAC failover). Raise to 3 locally for a more
// vivid multi-replica showcase.
const int OrderedConsumersReplicaCount = 2;

builder.AddProject<Projects.BareWire_Samples_OrderedConsumers>("ordered-consumers")
    .WithReplicas(OrderedConsumersReplicaCount)
    .WithReference(rabbitmq)
    .WithReference(postgres)
    .WaitFor(rabbitmq)
    .WaitFor(postgres);

builder.Build().Run();
