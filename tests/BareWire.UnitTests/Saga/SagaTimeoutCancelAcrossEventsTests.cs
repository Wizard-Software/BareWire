using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Saga;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Saga;
using BareWire.Serialization.Json;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Configuration;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Saga;

/// <summary>
/// Reproduces cancellation of a native scheduled timeout from a later saga event: a timeout scheduled
/// by one event of a saga must be cancellable by a later event of the SAME saga, even though the two
/// events are dispatched as two separate messages through the same <see cref="SagaMessageDispatcher{TStateMachine,TSaga}"/>.
/// </summary>
public sealed class SagaTimeoutCancelAcrossEventsTests
{
    private sealed record OrderPlaced(Guid OrderId);
    private sealed record PaymentReceived(Guid OrderId);
    private sealed record PaymentTimedOut(Guid OrderId);
    private sealed record ShipmentTimedOut(Guid OrderId);

    private sealed class CancelSagaState : ISagaState
    {
        public Guid CorrelationId { get; set; }
        public string CurrentState { get; set; } = "Initial";
        public int Version { get; set; }
    }

    private sealed class CancelStateMachine : BareWireStateMachine<CancelSagaState>
    {
        public CancelStateMachine()
        {
            var orderPlaced = Event<OrderPlaced>();
            var paymentReceived = Event<PaymentReceived>();

            CorrelateBy<OrderPlaced>(e => e.OrderId);
            CorrelateBy<PaymentReceived>(e => e.OrderId);

            var paymentTimeout = Schedule<PaymentTimedOut>(s => s.Delay = TimeSpan.FromMinutes(30));
            var shipmentTimeout = Schedule<ShipmentTimedOut>(s => s.Delay = TimeSpan.FromMinutes(60));

            Initially(() =>
            {
                When(orderPlaced, b => b
                    .ScheduleTimeout((_, evt) => new PaymentTimedOut(evt.OrderId), paymentTimeout)
                    .ScheduleTimeout((_, evt) => new ShipmentTimedOut(evt.OrderId), shipmentTimeout)
                    .TransitionTo("AwaitingPayment"));
            });

            During("AwaitingPayment", () =>
            {
                When(paymentReceived, b => b
                    .CancelTimeout<PaymentTimedOut>()
                    .TransitionTo("Paid"));
            });
        }
    }

    private const string EndpointName = "saga-endpoint";

    // The dispatcher-built provider computes due times from TimeProvider.System (production wiring,
    // unchanged), while the in-memory scheduler measures delay against the adapter's clock. Seeding the
    // fake with the real "now" keeps both clocks aligned; Advance(31 min) / Advance(61 min) give a margin
    // over the milliseconds that pass between seeding and scheduling.
    private static FakeTimeProvider AlignedClock() => new(TimeProvider.System.GetUtcNow());

    // ── Fixtures ──────────────────────────────────────────────────────────────────────────────────

    private static InMemoryTransportAdapter CreateAdapter(TimeProvider timeProvider, string queue)
    {
        var c = new InMemoryConfigurator();
        c.ConfigureTopology(t => t.DeclareQueue(queue));
        InMemoryTransportOptions o = c.Build();
        return new InMemoryTransportAdapter(o, new InMemoryBroker(o), timeProvider: timeProvider);
    }

    private static ServiceCollection BuildServices(InMemoryTransportAdapter adapter)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISagaRepository<CancelSagaState>>(new InMemorySagaRepository<CancelSagaState>());
        services.AddSingleton<ITransportAdapter>(adapter);
        services.AddSingleton<IMessageSerializer>(new SystemTextJsonSerializer());
        return services;
    }

    private static InMemoryQueue Queue(InMemoryTransportAdapter adapter, string name)
    {
        adapter.Broker.TryGetQueue(name, out InMemoryQueue? queue).Should().BeTrue();
        return queue!;
    }

    // The in-memory timer delivers without being awaited, so delivery is observed by bounded polling.
    private static async Task WaitForOccupancyAsync(InMemoryTransportAdapter adapter, string queueName, int expected)
    {
        InMemoryQueue queue = Queue(adapter, queueName);
        var stopwatch = Stopwatch.StartNew();

        while (queue.Occupancy != expected && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
        }

        queue.Occupancy.Should().Be(expected);
    }

    private static async Task<InboundMessage> ConsumeOneAsync(
        InMemoryTransportAdapter adapter, string queueName, TimeSpan timeout)
    {
        IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(queueName, new FlowControlOptions(), TestContext.Current.CancellationToken)
                .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        (await enumerator.MoveNextAsync().AsTask().WaitAsync(timeout, TestContext.Current.CancellationToken))
            .Should().BeTrue();
        return enumerator.Current;
    }

    private static async Task DispatchAsync<TEvent>(
        SagaMessageDispatcher<CancelStateMachine, CancelSagaState> dispatcher,
        TEvent evt,
        IPublishEndpoint publishEndpoint,
        ISendEndpointProvider sendEndpointProvider,
        IDeserializerResolver deserializerResolver)
        where TEvent : class
    {
        ReadOnlySequence<byte> body = SerializeToSequence(evt);
        var headers = new Dictionary<string, string>
        {
            ["BW-MessageType"] = typeof(TEvent).Name,
            ["content-type"] = "application/json",
        };

        bool dispatched = await dispatcher.TryDispatchAsync(
            body,
            headers,
            messageId: Guid.NewGuid().ToString(),
            endpointName: EndpointName,
            publishEndpoint,
            sendEndpointProvider,
            deserializerResolver,
            TestContext.Current.CancellationToken);

        dispatched.Should().BeTrue();
    }

    private static ReadOnlySequence<byte> SerializeToSequence<T>(T value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
        return new ReadOnlySequence<byte>(bytes);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelTimeout_InLaterEvent_PreventsDeliveryOfNativeTimeout()
    {
        // Arrange — one SagaMessageDispatcher shared by both messages, real ServiceCollection DI.
        FakeTimeProvider fake = AlignedClock();
        using InMemoryTransportAdapter adapter = CreateAdapter(fake, queue: EndpointName);
        using ServiceProvider serviceProvider = BuildServices(adapter).BuildServiceProvider();
        IServiceScopeFactory scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var machine = new CancelStateMachine();
        var definition = StateMachineDefinition<CancelSagaState>.Build(machine);
        var dispatcher = new SagaMessageDispatcher<CancelStateMachine, CancelSagaState>(
            definition, machine, scopeFactory, NullLoggerFactory.Instance);

        var deserializerResolver = Substitute.For<IDeserializerResolver>();
        deserializerResolver.Resolve(Arg.Any<string?>()).Returns(new SystemTextJsonRawDeserializer());
        var publishEndpoint = Substitute.For<IPublishEndpoint>();
        var sendEndpointProvider = Substitute.For<ISendEndpointProvider>();

        var orderId = Guid.NewGuid();

        // Act — OrderPlaced and PaymentReceived as two separate messages through the same dispatcher.
        await DispatchAsync(dispatcher, new OrderPlaced(orderId), publishEndpoint, sendEndpointProvider, deserializerResolver);
        await DispatchAsync(dispatcher, new PaymentReceived(orderId), publishEndpoint, sendEndpointProvider, deserializerResolver);

        // Assert (primary RED/GREEN signal, synchronous): today 2 (cancel is a no-op across events);
        // only ShipmentTimedOut should remain scheduled once cancellation works end to end.
        adapter.PendingScheduledCount.Should().Be(1);

        fake.Advance(TimeSpan.FromMinutes(31));
        await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        Queue(adapter, EndpointName).Occupancy.Should().Be(0); // PaymentTimedOut not delivered
    }

    [Fact]
    public async Task CancelTimeout_ForOneType_LeavesOtherTimeoutTypeScheduled()
    {
        // Arrange — identical to the test above.
        FakeTimeProvider fake = AlignedClock();
        using InMemoryTransportAdapter adapter = CreateAdapter(fake, queue: EndpointName);
        using ServiceProvider serviceProvider = BuildServices(adapter).BuildServiceProvider();
        IServiceScopeFactory scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var machine = new CancelStateMachine();
        var definition = StateMachineDefinition<CancelSagaState>.Build(machine);
        var dispatcher = new SagaMessageDispatcher<CancelStateMachine, CancelSagaState>(
            definition, machine, scopeFactory, NullLoggerFactory.Instance);

        var deserializerResolver = Substitute.For<IDeserializerResolver>();
        deserializerResolver.Resolve(Arg.Any<string?>()).Returns(new SystemTextJsonRawDeserializer());
        var publishEndpoint = Substitute.For<IPublishEndpoint>();
        var sendEndpointProvider = Substitute.For<ISendEndpointProvider>();

        var orderId = Guid.NewGuid();

        // Act
        await DispatchAsync(dispatcher, new OrderPlaced(orderId), publishEndpoint, sendEndpointProvider, deserializerResolver);
        await DispatchAsync(dispatcher, new PaymentReceived(orderId), publishEndpoint, sendEndpointProvider, deserializerResolver);

        fake.Advance(TimeSpan.FromMinutes(61));

        // Assert — bounded poll: today both PaymentTimedOut and ShipmentTimedOut are delivered (cancel
        // is a no-op), so occupancy settles at 2 and the wait below fails with that observed value.
        await WaitForOccupancyAsync(adapter, EndpointName, expected: 1);

        InboundMessage delivered = await ConsumeOneAsync(adapter, EndpointName, TimeSpan.FromSeconds(5));
        delivered.Headers["BW-MessageType"].Should().Be(nameof(ShipmentTimedOut));
    }
}
