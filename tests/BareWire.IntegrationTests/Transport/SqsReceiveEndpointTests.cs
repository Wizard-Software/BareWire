using System.Text.Json;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Serialization.Json;
using BareWire.Transport.AWS.SQS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BareWire.IntegrationTests.Transport;

/// <summary>Message consumed by <see cref="SqsReceiveEndpointConsumer"/> in the SQS receive-endpoint test.</summary>
public sealed record SqsReceiveEndpointOrder(string OrderId);

/// <summary>Signals the test when the endpoint-bound consumer receives its message.</summary>
public sealed class SqsReceiveEndpointConsumer(TaskCompletionSource<SqsReceiveEndpointOrder> received)
    : IConsumer<SqsReceiveEndpointOrder>
{
    public Task ConsumeAsync(ConsumeContext<SqsReceiveEndpointOrder> context)
    {
        received.TrySetResult(context.Message);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Bus-level end-to-end test proving that a consumer registered through
/// <c>ISqsConfigurator.ReceiveEndpoint</c> is started by the core bus and receives a message sent to
/// the queue. Gated behind <c>BAREWIRE_SQS_SERVICE_URL</c>: without it the test reports as Skipped.
/// </summary>
[Trait("Category", "AwsSqs")]
public sealed class SqsReceiveEndpointTests
{
    [Fact]
    [Trait("Category", "E2E")]
    public async Task ReceiveEndpoint_ConsumerRegistered_ReceivesMessageSentToQueue()
    {
        SqsTestEnvironment.SkipIfUnavailable();

        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        string queueName = $"recv-ep-sqs-{Guid.NewGuid():N}";
        TaskCompletionSource<SqsReceiveEndpointOrder> received =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using SqsTransportAdapter producer = SqsTestEnvironment.CreateAdapter();

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
                    services.AddTransient<SqsReceiveEndpointConsumer>();
                    services.AddBareWireJsonSerializer();
                    services.AddBareWireSqs(s =>
                    {
                        s.ServiceUrl(SqsTestEnvironment.ServiceUrl!);
                        s.AllowInsecureEndpoint();
                        s.UseExplicitCredentials("test", "test");
                        s.Region("us-east-1");
                        s.WaitTimeSeconds(1);
                        s.ReceiveEndpoint(queueName, e => e.Consumer<SqsReceiveEndpointConsumer, SqsReceiveEndpointOrder>());
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
                    body: JsonSerializer.SerializeToUtf8Bytes(new SqsReceiveEndpointOrder("ORD-1")),
                    contentType: "application/json");

                IReadOnlyList<SendResult> results = await producer.SendBatchAsync([outbound], cts.Token);
                results.Should().ContainSingle().Which.IsConfirmed.Should().BeTrue();

                // Assert
                SqsReceiveEndpointOrder order = await received.Task.WaitAsync(TimeSpan.FromSeconds(45), cts.Token);
                order.OrderId.Should().Be("ORD-1");
            }
            finally
            {
                await host.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            await SqsTestEnvironment.TryDeleteQueueAsync(queueName, CancellationToken.None);
        }
    }
}
