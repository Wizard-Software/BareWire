var builder = DistributedApplication.CreateBuilder(args);

var rabbitmq = builder.AddRabbitMQ("rabbitmq")
    .WithManagementPlugin();

var postgres = builder.AddPostgres("postgres")
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

builder.Build().Run();
