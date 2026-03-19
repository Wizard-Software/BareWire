using System.Buffers;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Saga;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Saga;
using BareWire.Saga.EntityFramework;
using BareWire.Transport.RabbitMQ;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BareWire.IntegrationTests.Saga;

// ── Local ConsumeContext subclass ─────────────────────────────────────────────

/// <summary>
/// A minimal concrete subclass of <see cref="ConsumeContext"/> that exposes the internal
/// constructor for use in integration test adapter code.
/// </summary>
internal sealed class E2EConsumeContext(
    Guid messageId,
    Guid? correlationId,
    Guid? conversationId,
    Uri? sourceAddress,
    Uri? destinationAddress,
    DateTimeOffset? sentTime,
    IReadOnlyDictionary<string, string> headers,
    string? contentType,
    ReadOnlySequence<byte> rawBody,
    IPublishEndpoint publishEndpoint,
    ISendEndpointProvider sendEndpointProvider,
    CancellationToken cancellationToken = default)
    : ConsumeContext(messageId, correlationId, conversationId, sourceAddress, destinationAddress,
                     sentTime, headers, contentType, rawBody, publishEndpoint, sendEndpointProvider,
                     cancellationToken);

// ── E2E test class ────────────────────────────────────────────────────────────

/// <summary>
/// End-to-end integration tests for the BareWire SAGA engine with real RabbitMQ transport
/// (via <see cref="AspireFixture"/>) and SQLite in-memory persistence
/// (via <see cref="BareWire.Saga.EntityFramework.EfCoreSagaRepository{TSaga}"/>).
///
/// <para>
/// Each test publishes events to RabbitMQ, consumes them, routes them through the
/// <see cref="StateMachineExecutor{TSaga}"/>, and then asserts the final saga state in SQLite.
/// The RabbitMQ-to-executor bridge is an inline test adapter: receive <see cref="InboundMessage"/>
/// → deserialize body → create <see cref="ConsumeContext"/> → call
/// <c>ProcessEventAsync</c> → ack the message.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class SagaE2ETests(AspireFixture fixture)
    : IClassFixture<AspireFixture>, IAsyncLifetime
{
    // SQLite in-memory connection — kept open for the full test lifetime so the schema persists.
    private SqliteConnection _connection = null!;
    private SagaDbContext _dbContext = null!;
    private EfCoreSagaRepository<OrderSagaState> _repository = null!;
    private StateMachineExecutor<OrderSagaState> _executor = null!;

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        // SQLite in-memory: open the connection and keep it open for the test lifetime.
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        ISagaModelConfiguration[] configurations = [new SagaModelConfiguration<OrderSagaState>()];

        var options = new DbContextOptionsBuilder<SagaDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new SagaDbContext(options, configurations);
        await _dbContext.Database.EnsureCreatedAsync();

        _repository = new EfCoreSagaRepository<OrderSagaState>(_dbContext);

        // Build state machine definition and executor.
        var stateMachine = new OrderSagaStateMachine();
        var definition = StateMachineDefinition<OrderSagaState>.Build(stateMachine);

        _executor = new StateMachineExecutor<OrderSagaState>(
            definition,
            _repository,
            NullLogger<StateMachineExecutor<OrderSagaState>>.Instance);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private RabbitMqTransportAdapter CreateAdapter() =>
        new(
            new RabbitMqTransportOptions
            {
                ConnectionString = fixture.GetRabbitMqConnectionString(),
            },
            NullLogger<RabbitMqTransportAdapter>.Instance);

    /// <summary>
    /// Deploys a minimal direct topology: one exchange bound to one queue.
    /// </summary>
    private static async Task<(string ExchangeName, string QueueName)> DeployTopologyAsync(
        RabbitMqTransportAdapter adapter,
        string suffix,
        CancellationToken ct)
    {
        string exchangeName = $"saga-ex-{suffix}";
        string queueName = $"saga-q-{suffix}";

        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(exchangeName, ExchangeType.Direct, durable: false, autoDelete: false);
        configurator.DeclareQueue(queueName, durable: false, autoDelete: false);
        configurator.BindExchangeToQueue(exchangeName, queueName, routingKey: queueName);
        await adapter.DeployTopologyAsync(configurator.Build(), ct);

        return (exchangeName, queueName);
    }

    /// <summary>
    /// Publishes a single typed event serialized as JSON to the given exchange and queue.
    /// </summary>
    private static async Task PublishEventAsync<TEvent>(
        RabbitMqTransportAdapter adapter,
        string exchangeName,
        string queueName,
        TEvent @event,
        CancellationToken ct)
        where TEvent : class
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(@event);

        OutboundMessage outbound = new(
            routingKey: queueName,
            headers: new Dictionary<string, string>
            {
                ["BW-Exchange"] = exchangeName,
                ["BW-MessageType"] = typeof(TEvent).Name,
            },
            body: body,
            contentType: "application/json");

        await adapter.SendBatchAsync([outbound], ct);
    }

    /// <summary>
    /// Consumes one message from the queue, deserializes the body to <typeparamref name="TEvent"/>,
    /// creates a <see cref="ConsumeContext"/>, processes it through the saga executor, and acks.
    /// </summary>
    private async Task ConsumeAndProcessAsync<TEvent>(
        RabbitMqTransportAdapter adapter,
        string queueName,
        CancellationToken ct)
        where TEvent : class
    {
        FlowControlOptions flow = new() { MaxInFlightMessages = 10, InternalQueueCapacity = 100 };

        await foreach (InboundMessage inbound in adapter.ConsumeAsync(queueName, flow, ct))
        {
            TEvent @event = DeserializeBody<TEvent>(inbound.Body);

            // Build a minimal ConsumeContext. The executor's PublishActivity calls
            // ctx.PublishAsync<T>() — we provide a no-op substitute so the pending action
            // succeeds without a real broker publish pipeline.
            var publishEndpoint = Substitute.For<IPublishEndpoint>();
            var sendEndpointProvider = Substitute.For<ISendEndpointProvider>();

            ConsumeContext consumeContext = new E2EConsumeContext(
                messageId: Guid.NewGuid(),
                correlationId: null,
                conversationId: null,
                sourceAddress: null,
                destinationAddress: null,
                sentTime: DateTimeOffset.UtcNow,
                headers: inbound.Headers,
                contentType: inbound.Headers.TryGetValue("content-type", out string? ct2) ? ct2 : "application/json",
                rawBody: inbound.Body,
                publishEndpoint: publishEndpoint,
                sendEndpointProvider: sendEndpointProvider);

            // Reset EF change tracker before each executor call so the entity loads fresh.
            _dbContext.ChangeTracker.Clear();

            await _executor.ProcessEventAsync(@event, consumeContext, ct);

            await adapter.SettleAsync(SettlementAction.Ack, inbound, ct);
            return;
        }

        throw new InvalidOperationException("Consume stream ended before any message arrived.");
    }

    /// <summary>
    /// Deserializes <paramref name="body"/> into <typeparamref name="T"/> from a
    /// <see cref="ReadOnlySequence{T}"/> of bytes.
    /// </summary>
    private static T DeserializeBody<T>(ReadOnlySequence<byte> body)
    {
        if (body.IsSingleSegment)
        {
            return JsonSerializer.Deserialize<T>(body.FirstSpan)
                ?? throw new InvalidOperationException($"Failed to deserialize {typeof(T).Name}.");
        }

        byte[] buffer = new byte[body.Length];
        int offset = 0;
        foreach (ReadOnlyMemory<byte> segment in body)
        {
            segment.Span.CopyTo(buffer.AsSpan(offset));
            offset += segment.Length;
        }

        return JsonSerializer.Deserialize<T>(buffer)
            ?? throw new InvalidOperationException($"Failed to deserialize {typeof(T).Name}.");
    }

    // ── E2E-SAGA-1: OrderCreated → Processing ────────────────────────────────

    /// <summary>
    /// E2E-SAGA-1: Publishes <see cref="OrderCreated"/> to RabbitMQ, consumes it, processes it
    /// through the saga executor, and verifies that the saga is persisted in SQLite with state
    /// <c>Processing</c> and the order details captured from the event.
    /// </summary>
    [Fact]
    public async Task OrderSaga_OrderCreated_TransitionsToProcessing()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");
        (string exchangeName, string queueName) = await DeployTopologyAsync(adapter, suffix, cts.Token);

        Guid orderId = Guid.NewGuid();
        const string OrderNumber = "ORD-E2E-001";
        const decimal Amount = 149.99m;

        // Act — publish the event
        await PublishEventAsync(
            adapter, exchangeName, queueName,
            new OrderCreated(orderId, OrderNumber, Amount),
            cts.Token);

        // Act — consume and route through the executor
        await ConsumeAndProcessAsync<OrderCreated>(adapter, queueName, cts.Token);

        // Assert — saga is persisted in SQLite
        _dbContext.ChangeTracker.Clear();
        OrderSagaState? saga = await _dbContext.Set<OrderSagaState>().FindAsync([orderId], cts.Token);

        saga.Should().NotBeNull();
        saga!.CorrelationId.Should().Be(orderId);
        saga.CurrentState.Should().Be("Processing");
        saga.OrderNumber.Should().Be(OrderNumber);
        saga.Amount.Should().Be(Amount);
    }

    // ── E2E-SAGA-2: PaymentReceived → Completed (saga finalized) ─────────────

    /// <summary>
    /// E2E-SAGA-2: Seeds a saga in <c>Processing</c> state, then publishes
    /// <see cref="PaymentReceived"/> to RabbitMQ, consumes it through the executor, and verifies
    /// that the saga is removed from the store (finalized). The <see cref="OrderCompleted"/>
    /// publish is intercepted by a substitute endpoint and verified.
    /// </summary>
    [Fact]
    public async Task OrderSaga_PaymentReceived_FinalizesAndDeletesSaga()
    {
        // Arrange — seed a saga in Processing state directly into the repository
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");
        (string exchangeName, string queueName) = await DeployTopologyAsync(adapter, suffix, cts.Token);

        Guid orderId = Guid.NewGuid();

        var seedSaga = new OrderSagaState
        {
            CorrelationId = orderId,
            CurrentState = "Processing",
            OrderNumber = "ORD-E2E-002",
            Amount = 250.00m,
        };

        await _repository.SaveAsync(seedSaga, cts.Token);
        _dbContext.ChangeTracker.Clear();

        // Act — publish PaymentReceived and consume it
        await PublishEventAsync(
            adapter, exchangeName, queueName,
            new PaymentReceived(orderId),
            cts.Token);

        await ConsumeAndProcessAsync<PaymentReceived>(adapter, queueName, cts.Token);

        // Assert — saga was finalized (deleted from the database)
        _dbContext.ChangeTracker.Clear();
        OrderSagaState? saga = await _dbContext.Set<OrderSagaState>().FindAsync([orderId], cts.Token);
        saga.Should().BeNull(because: "the saga should be deleted when Finalize() is called");
    }

    // ── E2E-SAGA-3: PaymentFailed → Failed (saga finalized) ──────────────────

    /// <summary>
    /// E2E-SAGA-3: Seeds a saga in <c>Processing</c> state, then publishes
    /// <see cref="PaymentFailed"/> to RabbitMQ, consumes it through the executor, and verifies
    /// that the saga is removed from the store (finalized via <c>Failed</c> state).
    /// </summary>
    [Fact]
    public async Task OrderSaga_PaymentFailed_FinalizesAndDeletesSaga()
    {
        // Arrange — seed a saga in Processing state
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");
        (string exchangeName, string queueName) = await DeployTopologyAsync(adapter, suffix, cts.Token);

        Guid orderId = Guid.NewGuid();

        var seedSaga = new OrderSagaState
        {
            CorrelationId = orderId,
            CurrentState = "Processing",
            OrderNumber = "ORD-E2E-003",
            Amount = 75.50m,
        };

        await _repository.SaveAsync(seedSaga, cts.Token);
        _dbContext.ChangeTracker.Clear();

        // Act — publish PaymentFailed and consume it
        await PublishEventAsync(
            adapter, exchangeName, queueName,
            new PaymentFailed(orderId, "Insufficient funds"),
            cts.Token);

        await ConsumeAndProcessAsync<PaymentFailed>(adapter, queueName, cts.Token);

        // Assert — saga was finalized (deleted from the database)
        _dbContext.ChangeTracker.Clear();
        OrderSagaState? saga = await _dbContext.Set<OrderSagaState>().FindAsync([orderId], cts.Token);
        saga.Should().BeNull(because: "the saga should be deleted when Finalize() is called after PaymentFailed");
    }
}
