var builder = DistributedApplication.CreateBuilder(args);

// ── Infrastructure ──────────────────────────────────────────────────────────
// ContainerLifetime.Session ensures fresh containers on every AppHost run —
// no stale queues or database rows from previous sessions.
// Change to ContainerLifetime.Persistent if you need data to survive restarts
// (e.g. testing long-running sagas or debugging specific message sequences).
var rabbitmq = builder.AddRabbitMQ("rabbitmq")
    .WithLifetime(ContainerLifetime.Session)
    .WithManagementPlugin();

// No max_prepared_transactions configuration is needed. Every transactional-outbox sample shares the
// outbox middleware's pinned connection for its consumer business write (via IOutboxConnectionAccessor),
// so each consume commits single-phase — no second connection enlists, and System.Transactions.TransactionScope
// never escalates to a two-phase (prepared) commit. PostgreSQL therefore runs with its default
// max_prepared_transactions=0 (2PC disabled).
var postgresServer = builder.AddPostgres("postgres")
    .WithLifetime(ContainerLifetime.Session);

var postgres = postgresServer.AddDatabase("barewiredb");

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

// Replica count for the competing-responders sample. Set to 2 for CI (smallest number that
// demonstrates the competing-responder + first-in-wins scenario). Each replica binds its own
// unique queue to the per-type fanout exchange so every request reaches all replicas.
const int CompetingRespondersReplicaCount = 2;

builder.AddProject<Projects.BareWire_Samples_CompetingResponders>("competing-responders")
    .WithReplicas(CompetingRespondersReplicaCount)
    .WithReference(rabbitmq)
    .WaitFor(rabbitmq);

// Consumer routing keys sample: one shared queue, three consumers with different patterns,
// demonstrating most-specific-wins dispatch and type-less interop via AcceptUntyped().
// RabbitMQ only — no Postgres (pure messaging sample).
builder.AddProject<Projects.BareWire_Samples_ConsumerRoutingKeys>("consumer-routing-keys")
    .WithReference(rabbitmq)
    .WaitFor(rabbitmq);

// Consumer definition showcase: ConsumerDefinition<TConsumer> discovered via explicit DI
// registration, colocating a retry policy and routing-key patterns next to the consumer, plus the
// opt-in transport-level DeclareTopology helper. RabbitMQ only — no Postgres (pure messaging sample).
builder.AddProject<Projects.BareWire_Samples_ConsumerDefinitionShowcase>("consumer-definition-showcase")
    .WithReference(rabbitmq)
    .WaitFor(rabbitmq);

builder.Build().Run();
