using System.Text.Json;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Serialization.Json;
using BareWire.Transport.Google.PubSub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BareWire.IntegrationTests.Transport;

/// <summary>Message consumed by <see cref="PubSubReceiveEndpointConsumer"/>.</summary>
public sealed record PubSubReceiveEndpointOrder(string OrderId);

/// <summary>Signals the test when the endpoint-bound consumer receives its message.</summary>
public sealed class PubSubReceiveEndpointConsumer(TaskCompletionSource<PubSubReceiveEndpointOrder> received)
    : IConsumer<PubSubReceiveEndpointOrder>
{
    public Task ConsumeAsync(ConsumeContext<PubSubReceiveEndpointOrder> context)
    {
        received.TrySetResult(context.Message);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Bus-level end-to-end test proving that a consumer registered through
/// <c>IPubSubConfigurator.ReceiveEndpoint</c> is started by the core bus and receives a message published
/// to the topic. Gated behind <c>BAREWIRE_PUBSUB_EMULATOR_HOST</c>: without it the test reports as Skipped.
/// </summary>
[Trait("Category", "GooglePubSub")]
public sealed class PubSubReceiveEndpointTests
{
    private const string ProjectId = "barewire-test";

    [Fact]
    [Trait("Category", "E2E")]
    public async Task ReceiveEndpoint_ConsumerRegistered_ReceivesMessagePublishedToTopic()
    {
        PubSubTestEnvironment.SkipIfUnavailable();

        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        string suffix = Guid.NewGuid().ToString("N");
        string topicName = $"recv-ep-topic-{suffix}";
        string subName = $"recv-ep-sub-{suffix}";
        TaskCompletionSource<PubSubReceiveEndpointOrder> received =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using PubSubTransportAdapter producer = PubSubTestEnvironment.CreateAdapter(ProjectId);

        try
        {
            // Topology is manual: provision the topic and subscription out of band.
            await producer.DeployTopologyAsync(
                new TopologyDeclaration
                {
                    Exchanges = [new ExchangeDeclaration(topicName, ExchangeType.Fanout)],
                    Queues = [new QueueDeclaration(Name: subName, Durable: true)],
                    ExchangeQueueBindings =
                        [new ExchangeQueueBinding(ExchangeName: topicName, QueueName: subName, RoutingKey: string.Empty)],
                },
                cts.Token);

            using IHost host = Host.CreateDefaultBuilder()
                .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
                .ConfigureServices(services =>
                {
                    services.AddSingleton(received);
                    services.AddTransient<PubSubReceiveEndpointConsumer>();
                    services.AddBareWireJsonSerializer();
                    services.AddBareWirePubSub(p =>
                    {
                        p.ProjectId(ProjectId);
                        p.UseEmulator(PubSubTestEnvironment.EmulatorHost!);
                        p.ReceiveEndpoint(
                            subName,
                            e => e.Consumer<PubSubReceiveEndpointConsumer, PubSubReceiveEndpointOrder>());
                    });
                    services.AddBareWire(_ => { });
                })
                .Build();

            await host.StartAsync(cts.Token);

            try
            {
                // Act
                OutboundMessage outbound = new(
                    routingKey: topicName,
                    headers: new Dictionary<string, string>(),
                    body: JsonSerializer.SerializeToUtf8Bytes(new PubSubReceiveEndpointOrder("ORD-1")),
                    contentType: "application/json");

                IReadOnlyList<SendResult> results = await producer.SendBatchAsync([outbound], cts.Token);
                results.Should().ContainSingle().Which.IsConfirmed.Should().BeTrue();

                // Assert
                PubSubReceiveEndpointOrder order = await received.Task.WaitAsync(TimeSpan.FromSeconds(45), cts.Token);
                order.OrderId.Should().Be("ORD-1");
            }
            finally
            {
                await host.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            await PubSubTestEnvironment.TryDeleteSubscriptionAsync(ProjectId, subName, CancellationToken.None);
            await PubSubTestEnvironment.TryDeleteTopicAsync(ProjectId, topicName, CancellationToken.None);
        }
    }
}
