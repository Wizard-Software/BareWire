using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Routing;
using BareWire.Abstractions.Transport;
using BareWire.Transport.AWS.SQS;
using BareWire.Transport.AzureServiceBus;
using BareWire.Transport.Google.PubSub;
using BareWire.Transport.InMemory;
using BareWire.Transport.Kafka;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Transport;

public sealed class MultiTransportRegistrationTests
{
    private sealed record OrderCreated(string OrderId);

    private sealed class TestConsumer : IConsumer<OrderCreated>
    {
        public Task ConsumeAsync(ConsumeContext<OrderCreated> context) => Task.CompletedTask;
    }

    private static void AddRabbitMq(IServiceCollection services, string queue = "orders") =>
        services.AddBareWireRabbitMq(rmq =>
        {
            rmq.Host("amqp://guest:guest@localhost:5672/");
            rmq.ReceiveEndpoint(queue, e => e.Consumer<TestConsumer, OrderCreated>());
        });

    private static void AddInMemory(IServiceCollection services, string queue = "q") =>
        services.AddBareWireInMemory(t =>
        {
            t.ConfigureTopology(topo => topo.DeclareQueue(queue));
            t.ReceiveEndpoint(queue, e => e.Consumer<TestConsumer, OrderCreated>());
        });

    private static void AddKafka(IServiceCollection services, string topic = "payments") =>
        services.AddBareWireKafka(k =>
        {
            k.BootstrapServers("localhost:9092");
            k.ConsumerGroup("test-group");
            k.ReceiveEndpoint(topic, e => e.Consumer<TestConsumer, OrderCreated>());
        });

    private static void AddSqs(IServiceCollection services, string queue = "orders-queue") =>
        services.AddBareWireSqs(k =>
        {
            k.Region("eu-central-1");
            k.UseExplicitCredentials("test", "test");
            k.ReceiveEndpoint(queue, e => e.Consumer<TestConsumer, OrderCreated>());
        });

    private static void AddAzureServiceBus(IServiceCollection services, string queue = "orders-queue") =>
        services.AddBareWireAzureServiceBus(k =>
        {
            k.ConnectionString("Endpoint=sb://localhost/;SharedAccessKeyName=k;SharedAccessKey=dGVzdA==");
            k.ReceiveEndpoint(queue, e => e.Consumer<TestConsumer, OrderCreated>());
        });

    private static void AddPubSub(IServiceCollection services, string subscription = "orders-sub") =>
        services.AddBareWirePubSub(k =>
        {
            k.ProjectId("test-project");
            k.UseEmulator("localhost:8085");
            k.ReceiveEndpoint(subscription, e => e.Consumer<TestConsumer, OrderCreated>());
        });

    private static void AssertRejected(Action act) =>
        act.Should().Throw<BareWireConfigurationException>()
            .Which.Message.Should().Contain("single transport").And.Contain("not supported yet");

    private static void AssertRejectedAsRepeatedCall(Action act, string methodName) =>
        act.Should().Throw<BareWireConfigurationException>()
            .Which.Message.Should().Contain(methodName).And.Contain("already called");

    [Fact]
    public void AddBareWireKafka_AfterRabbitMqWithReceiveEndpoints_ThrowsConfigurationException()
    {
        var services = new ServiceCollection();
        AddRabbitMq(services);

        Action act = () => AddKafka(services);

        AssertRejected(act);
    }

    [Fact]
    public void AddBareWireKafka_WhenRejected_LeavesFirstTransportRegistrationIntact()
    {
        var services = new ServiceCollection();
        AddRabbitMq(services);

        try
        {
            AddKafka(services);
        }
        catch (BareWireConfigurationException)
        {
            // Expected: asserted by the other tests; this test inspects the leftover registrations.
        }

        services.Count(d => d.ServiceType == typeof(ITransportAdapter) && !d.IsKeyedService).Should().Be(1);
        services.Should().NotContain(d => d.ServiceType == typeof(KafkaTransportOptions));
        ServiceDescriptor bindingsDescriptor = services.Single(d => d.ServiceType == typeof(IReadOnlyList<EndpointBinding>));
        var bindings = (IReadOnlyList<EndpointBinding>)bindingsDescriptor.ImplementationInstance!;
        bindings.Select(b => b.EndpointName).Should().Equal("orders");
    }

    [Fact]
    public void AddBareWireRabbitMq_AfterInMemory_ThrowsConfigurationException()
    {
        var services = new ServiceCollection();
        AddInMemory(services);

        Action act = () => AddRabbitMq(services);

        AssertRejected(act);
    }

    [Fact]
    public void AddBareWireInMemory_AfterSqs_ThrowsConfigurationException()
    {
        var services = new ServiceCollection();
        AddSqs(services);

        Action act = () => AddInMemory(services);

        AssertRejected(act);
    }

    [Fact]
    public void AddBareWireSqs_AfterPubSub_ThrowsConfigurationException()
    {
        var services = new ServiceCollection();
        AddPubSub(services);

        Action act = () => AddSqs(services);

        AssertRejected(act);
    }

    [Fact]
    public void AddBareWireAzureServiceBus_AfterKafka_ThrowsConfigurationException()
    {
        var services = new ServiceCollection();
        AddKafka(services);

        Action act = () => AddAzureServiceBus(services);

        AssertRejected(act);
    }

    [Fact]
    public void AddBareWirePubSub_AfterAzureServiceBus_ThrowsConfigurationException()
    {
        var services = new ServiceCollection();
        AddAzureServiceBus(services);

        Action act = () => AddPubSub(services);

        AssertRejected(act);
    }

    [Fact]
    public void AddBareWireRabbitMq_CalledTwice_ThrowsConfigurationException()
    {
        var services = new ServiceCollection();
        AddRabbitMq(services, queue: "orders");

        Action act = () => AddRabbitMq(services, queue: "payments");

        AssertRejectedAsRepeatedCall(act, "AddBareWireRabbitMq");
    }

    [Fact]
    public void AddBareWireInMemory_CalledTwice_ThrowsConfigurationException()
    {
        var services = new ServiceCollection();
        AddInMemory(services, queue: "orders");

        Action act = () => AddInMemory(services, queue: "payments");

        AssertRejectedAsRepeatedCall(act, "AddBareWireInMemory");
    }

    [Fact]
    public void AddBareWireKafka_CalledTwice_ThrowsConfigurationException()
    {
        var services = new ServiceCollection();
        AddKafka(services, topic: "orders");

        Action act = () => AddKafka(services, topic: "payments");

        AssertRejectedAsRepeatedCall(act, "AddBareWireKafka");
    }

    [Fact]
    public void AddBareWireSqs_CalledTwice_ThrowsConfigurationException()
    {
        var services = new ServiceCollection();
        AddSqs(services, queue: "orders-queue");

        Action act = () => AddSqs(services, queue: "payments-queue");

        AssertRejectedAsRepeatedCall(act, "AddBareWireSqs");
    }

    [Fact]
    public void AddBareWireAzureServiceBus_CalledTwice_ThrowsConfigurationException()
    {
        var services = new ServiceCollection();
        AddAzureServiceBus(services, queue: "orders-queue");

        Action act = () => AddAzureServiceBus(services, queue: "payments-queue");

        AssertRejectedAsRepeatedCall(act, "AddBareWireAzureServiceBus");
    }

    [Fact]
    public void AddBareWirePubSub_CalledTwice_ThrowsConfigurationException()
    {
        var services = new ServiceCollection();
        AddPubSub(services, subscription: "orders-sub");

        Action act = () => AddPubSub(services, subscription: "payments-sub");

        AssertRejectedAsRepeatedCall(act, "AddBareWirePubSub");
    }

    [Fact]
    public void AddBareWireRabbitMq_CalledTwice_LeavesFirstRegistrationIntact()
    {
        var services = new ServiceCollection();
        services.AddBareWireRabbitMq(rmq =>
        {
            rmq.Host("amqp://guest:guest@localhost:5672/");
            rmq.ConfigureTopology(t => t.DeclareExchange("first-exchange", ExchangeType.Fanout));
            rmq.MapExchange<OrderCreated>("first-exchange");
            rmq.ReceiveEndpoint("orders", e => e.Consumer<TestConsumer, OrderCreated>());
        });
        ServiceDescriptor[] before = services.ToArray();

        Action act = () => services.AddBareWireRabbitMq(rmq =>
        {
            rmq.Host("amqp://guest:guest@localhost:5672/");
            rmq.ConfigureTopology(t => t.DeclareExchange("second-exchange", ExchangeType.Fanout));
            rmq.MapExchange<OrderCreated>("second-exchange");
            rmq.ReceiveEndpoint("payments", e => e.Consumer<TestConsumer, OrderCreated>());
        });

        act.Should().Throw<BareWireConfigurationException>();
        services.Should().Equal(before); // same descriptor instances, same order
        var exchangeResolver = (IExchangeResolver)services
            .Single(d => d.ServiceType == typeof(IExchangeResolver)).ImplementationInstance!;
        exchangeResolver.Resolve<OrderCreated>().Should().Be("first-exchange");
        var bindings = (IReadOnlyList<EndpointBinding>)services
            .Single(d => d.ServiceType == typeof(IReadOnlyList<EndpointBinding>)).ImplementationInstance!;
        bindings.Select(b => b.EndpointName).Should().Equal("orders");
    }

    [Fact]
    public void AddBareWireKafka_CalledTwice_DoesNotInvokeSecondConfigureDelegate()
    {
        var services = new ServiceCollection();
        AddKafka(services, topic: "payments");
        bool invoked = false;

        Action act = () => services.AddBareWireKafka(_ => invoked = true);

        act.Should().Throw<BareWireConfigurationException>();
        invoked.Should().BeFalse();
    }

    [Fact]
    public void AddBareWireKafka_WithOnlyKeyedTransportAdapterRegistered_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<ITransportAdapter>("other", Substitute.For<ITransportAdapter>());

        Action act = () => AddKafka(services);

        act.Should().NotThrow();
    }
}
