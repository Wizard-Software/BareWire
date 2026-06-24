using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);

var rabbitmq = builder.AddRabbitMQ("rmq")
    .WithManagementPlugin()
    // Enable the consistent-hash exchange plugin so the BareWire.Transport.RabbitMQ
    // integration tests (R8.2) can declare an 'x-consistent-hash' exchange against the
    // real broker. The stock RabbitMQ image does NOT honour RABBITMQ_ENABLED_PLUGINS at
    // runtime, so the plugin set is supplied via the broker's enabled_plugins file. Because
    // WithManagementPlugin() swaps to the '-management' image variant, this file MUST also
    // list rabbitmq_management — otherwise the management plugin (and its health check) breaks.
    .WithContainerFiles(
        destinationPath: "/etc/rabbitmq",
        entries:
        [
            new ContainerFile
            {
                Name = "enabled_plugins",
                Contents = "[rabbitmq_management,rabbitmq_consistent_hash_exchange].\n",
            },
        ]);

// Kafka broker for the BareWire.Transport.Kafka integration tests (R1.5).
// Aspire provisions a single-broker KRaft-mode container; the connection string
// resolves to the bootstrap-server list (host:port) once the resource is healthy.
var kafka = builder.AddKafka("kafka");

// Redis container for BareWire.Saga.Redis integration tests (R6.3).
// Connection string resolves to a StackExchange.Redis-compatible host:port once the resource is healthy.
var redis = builder.AddRedis("redis");

builder.Build().Run();
