var builder = DistributedApplication.CreateBuilder(args);

var rabbitmq = builder.AddRabbitMQ("rmq")
    .WithManagementPlugin();

// Kafka broker for the BareWire.Transport.Kafka integration tests (R1.5).
// Aspire provisions a single-broker KRaft-mode container; the connection string
// resolves to the bootstrap-server list (host:port) once the resource is healthy.
var kafka = builder.AddKafka("kafka");

builder.Build().Run();
