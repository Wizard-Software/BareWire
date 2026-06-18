var builder = DistributedApplication.CreateBuilder(args);

var rabbitmq = builder.AddRabbitMQ("rmq")
    .WithManagementPlugin();

// Kafka broker for the BareWire.Transport.Kafka integration tests (R1.5).
// Aspire provisions a single-broker KRaft-mode container; the connection string
// resolves to the bootstrap-server list (host:port) once the resource is healthy.
var kafka = builder.AddKafka("kafka");

// Redis container for BareWire.Saga.Redis integration tests (R6.3).
// Connection string resolves to a StackExchange.Redis-compatible host:port once the resource is healthy.
var redis = builder.AddRedis("redis");

builder.Build().Run();
