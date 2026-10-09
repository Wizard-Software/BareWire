using System.Text.Json;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Serialization.Json;
using BareWire.Transport.AzureServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BareWire.IntegrationTests.Transport;

/// <summary>Message consumed by <see cref="AzureServiceBusReceiveEndpointConsumer"/>.</summary>
public sealed record AzureServiceBusReceiveEndpointOrder(string OrderId);

/// <summary>Signals the test when the endpoint-bound consumer receives its message.</summary>
public sealed class AzureServiceBusReceiveEndpointConsumer(
    TaskCompletionSource<AzureServiceBusReceiveEndpointOrder> received)
    : IConsumer<AzureServiceBusReceiveEndpointOrder>
{
    public Task ConsumeAsync(ConsumeContext<AzureServiceBusReceiveEndpointOrder> context)
    {
        received.TrySetResult(context.Message);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Bus-level end-to-end test proving that a consumer registered through
/// <c>IAzureServiceBusConfigurator.ReceiveEndpoint</c> is started by the core bus and receives a message
/// sent to the queue. Gated behind <c>BAREWIRE_ASB_CONNECTION_STRING</c>: without it the test reports as Skipped.
/// </summary>
[Trait("Category", "AzureServiceBus")]
public sealed class AzureServiceBusReceiveEndpointTests
{
    [Fact]
    [Trait("Category", "E2E")]
    public async Task ReceiveEndpoint_ConsumerRegistered_ReceivesMessageSentToQueue()
    {
        AzureServiceBusTestEnvironment.SkipIfUnavailable();

        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        string queueName = $"recv-ep-asb-{Guid.NewGuid():N}";
        TaskCompletionSource<AzureServiceBusReceiveEndpointOrder> received =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using AzureServiceBusTransportAdapter producer = AzureServiceBusTestEnvironment.CreateSasAdapter();

        try
        {
            // Topology is manual: provision the queue out of band.
            await producer.DeployTopologyAsync(
                new TopologyDeclaration { Queues = [new QueueDeclaration(Name: queueName, Durable: true)] },
                cts.Token);

            using IHost host = Host.CreateDefaultBuilder()
                .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
                .ConfigureServices(services =>
                {
                    services.AddSingleton(received);
                    services.AddTransient<AzureServiceBusReceiveEndpointConsumer>();
                    services.AddBareWireJsonSerializer();
                    services.AddBareWireAzureServiceBus(a =>
                    {
                        a.UseSasAuth(AzureServiceBusTestEnvironment.ConnectionString!);
                        a.ReceiveEndpoint(
                            queueName,
                            e => e.Consumer<AzureServiceBusReceiveEndpointConsumer, AzureServiceBusReceiveEndpointOrder>());
                    });
                    services.AddBareWire(_ => { });
                })
                .Build();

            await host.StartAsync(cts.Token);

            try
            {
                // Act
                OutboundMessage outbound = new(
                    routingKey: queueName,
                    headers: new Dictionary<string, string>(),
                    body: JsonSerializer.SerializeToUtf8Bytes(new AzureServiceBusReceiveEndpointOrder("ORD-1")),
                    contentType: "application/json");

                IReadOnlyList<SendResult> results = await producer.SendBatchAsync([outbound], cts.Token);
                results.Should().ContainSingle().Which.IsConfirmed.Should().BeTrue();

                // Assert
                AzureServiceBusReceiveEndpointOrder order =
                    await received.Task.WaitAsync(TimeSpan.FromSeconds(45), cts.Token);
                order.OrderId.Should().Be("ORD-1");
            }
            finally
            {
                await host.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            await AzureServiceBusTestEnvironment.TryDeleteQueueAsync(queueName, CancellationToken.None);
        }
    }
}
