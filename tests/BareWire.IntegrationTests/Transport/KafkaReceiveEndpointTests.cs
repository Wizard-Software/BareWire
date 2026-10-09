using System.Text.Json;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Serialization.Json;
using BareWire.Transport.Kafka;
using BareWire.Transport.Kafka.Configuration;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.IntegrationTests.Transport;

/// <summary>Message consumed by <see cref="KafkaReceiveEndpointConsumer"/> in the Kafka receive-endpoint test.</summary>
public sealed record KafkaReceiveEndpointOrder(string OrderId);

/// <summary>Signals the test when the endpoint-bound consumer receives its message.</summary>
public sealed class KafkaReceiveEndpointConsumer(TaskCompletionSource<KafkaReceiveEndpointOrder> received)
    : IConsumer<KafkaReceiveEndpointOrder>
{
    public Task ConsumeAsync(ConsumeContext<KafkaReceiveEndpointOrder> context)
    {
        received.TrySetResult(context.Message);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Bus-level end-to-end test proving that a consumer registered through
/// <see cref="IKafkaConfigurator.ReceiveEndpoint"/> is started by the core bus and receives a message
/// produced to the topic on a real Kafka broker.
/// </summary>
[Trait("Category", "Integration")]
public sealed class KafkaReceiveEndpointTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    [Fact]
    [Trait("Category", "E2E")]
    public async Task ReceiveEndpoint_ConsumerRegistered_ReceivesMessagePublishedToTopic()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        string suffix = Guid.NewGuid().ToString("N");
        string topic = $"recv-ep-{suffix}";
        string group = $"grp-{suffix}";
        string bootstrap = fixture.GetKafkaBootstrapServers();

        TaskCompletionSource<KafkaReceiveEndpointOrder> received =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Provision the topic out of band (topology is manual) and produce with a standalone adapter.
        await using KafkaTransportAdapter producer = new(
            new KafkaTransportOptions
            {
                BootstrapServers = bootstrap,
                GroupId = $"producer-{suffix}",
                AutoOffsetReset = AutoOffsetReset.Earliest,
            },
            NullLogger<KafkaTransportAdapter>.Instance);

        KafkaTopologyConfigurator topology = new();
        topology.DeclareQueue(topic, durable: true, autoDelete: false);
        await producer.DeployTopologyAsync(topology.Build(), cts.Token);

        using IHost host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .ConfigureServices(services =>
            {
                services.AddSingleton(received);
                services.AddTransient<KafkaReceiveEndpointConsumer>();
                services.AddBareWireJsonSerializer();
                services.AddBareWireKafka(k =>
                {
                    k.BootstrapServers(bootstrap);
                    k.ConsumerGroup(group);
                    k.ReceiveEndpoint(topic, e => e.Consumer<KafkaReceiveEndpointConsumer, KafkaReceiveEndpointOrder>());
                });
                services.AddBareWire(_ => { });
            })
            .Build();

        await host.StartAsync(cts.Token);

        try
        {
            // Act — raw JSON, no type header: the endpoint has a single consumer so the core dispatches to it.
            OutboundMessage outbound = new(
                routingKey: topic,
                headers: new Dictionary<string, string>(),
                body: JsonSerializer.SerializeToUtf8Bytes(new KafkaReceiveEndpointOrder("ORD-1")),
                contentType: "application/json");

            IReadOnlyList<SendResult> results = await producer.SendBatchAsync([outbound], cts.Token);
            results.Should().ContainSingle().Which.IsConfirmed.Should().BeTrue();

            // Assert
            KafkaReceiveEndpointOrder order = await received.Task.WaitAsync(TimeSpan.FromSeconds(45), cts.Token);
            order.OrderId.Should().Be("ORD-1");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }
}
