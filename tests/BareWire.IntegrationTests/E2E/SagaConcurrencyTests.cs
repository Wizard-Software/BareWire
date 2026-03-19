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

namespace BareWire.IntegrationTests.E2E;

/// <summary>
/// E2E-4: SAGA concurrency test.
///
/// <para>
/// Publishes events for 10 independent sagas concurrently (interleaved) and verifies
/// that all 10 sagas reach their final state. Uses the same
/// <see cref="BareWire.IntegrationTests.Saga.OrderSagaStateMachine"/> and
/// <see cref="EfCoreSagaRepository{TSaga}"/> patterns from the existing saga E2E tests.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
public sealed class SagaConcurrencyTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>, IAsyncLifetime
{
    private const int SagaCount = 10;

    // ── SQLite infrastructure ─────────────────────────────────────────────────

    private SqliteConnection _connection = null!;
    private SagaDbContext _dbContext = null!;
    private EfCoreSagaRepository<BareWire.IntegrationTests.Saga.OrderSagaState> _repository = null!;
    private StateMachineExecutor<BareWire.IntegrationTests.Saga.OrderSagaState> _executor = null!;

    // ── IAsyncLifetime ────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        ISagaModelConfiguration[] configurations =
            [new SagaModelConfiguration<BareWire.IntegrationTests.Saga.OrderSagaState>()];

        var options = new DbContextOptionsBuilder<SagaDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new SagaDbContext(options, configurations);
        await _dbContext.Database.EnsureCreatedAsync();

        _repository = new EfCoreSagaRepository<BareWire.IntegrationTests.Saga.OrderSagaState>(_dbContext);

        var stateMachine = new BareWire.IntegrationTests.Saga.OrderSagaStateMachine();
        var definition = StateMachineDefinition<BareWire.IntegrationTests.Saga.OrderSagaState>.Build(stateMachine);

        _executor = new StateMachineExecutor<BareWire.IntegrationTests.Saga.OrderSagaState>(
            definition,
            _repository,
            NullLogger<StateMachineExecutor<BareWire.IntegrationTests.Saga.OrderSagaState>>.Instance);
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

    private static async Task<(string ExchangeName, string QueueName)> DeployTopologyAsync(
        RabbitMqTransportAdapter adapter,
        string suffix,
        CancellationToken ct)
    {
        string exchangeName = $"e2e-saga-conc-ex-{suffix}";
        string queueName = $"e2e-saga-conc-q-{suffix}";

        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(exchangeName, ExchangeType.Direct, durable: false, autoDelete: false);
        configurator.DeclareQueue(queueName, durable: false, autoDelete: false);
        configurator.BindExchangeToQueue(exchangeName, queueName, routingKey: queueName);
        await adapter.DeployTopologyAsync(configurator.Build(), ct);

        return (exchangeName, queueName);
    }

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

    private async Task ConsumeAndProcessAsync<TEvent>(
        RabbitMqTransportAdapter adapter,
        string queueName,
        CancellationToken ct)
        where TEvent : class
    {
        FlowControlOptions flow = new() { MaxInFlightMessages = 50, InternalQueueCapacity = 200 };

        await foreach (InboundMessage inbound in adapter.ConsumeAsync(queueName, flow, ct))
        {
            TEvent @event = DeserializeBody<TEvent>(inbound.Body);

            var publishEndpoint = Substitute.For<IPublishEndpoint>();
            var sendEndpointProvider = Substitute.For<ISendEndpointProvider>();

            ConsumeContext consumeContext = new E2ESagaConcurrencyConsumeContext(
                messageId: Guid.NewGuid(),
                correlationId: null,
                conversationId: null,
                sourceAddress: null,
                destinationAddress: null,
                sentTime: DateTimeOffset.UtcNow,
                headers: inbound.Headers,
                contentType: inbound.Headers.TryGetValue("content-type", out string? ct2)
                    ? ct2
                    : "application/json",
                rawBody: inbound.Body,
                publishEndpoint: publishEndpoint,
                sendEndpointProvider: sendEndpointProvider);

            _dbContext.ChangeTracker.Clear();
            await _executor.ProcessEventAsync(@event, consumeContext, ct);

            await adapter.SettleAsync(SettlementAction.Ack, inbound, ct);
            return;
        }

        throw new InvalidOperationException("Consume stream ended before any message arrived.");
    }

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

    // ── E2E-4: SAGA concurrency ───────────────────────────────────────────────

    /// <summary>
    /// E2E-4: Creates 10 unique correlation IDs (order IDs), publishes
    /// <see cref="BareWire.IntegrationTests.Saga.OrderCreated"/> + <see cref="BareWire.IntegrationTests.Saga.PaymentReceived"/>
    /// events for each saga in interleaved order, processes all events through the executor,
    /// and verifies that all 10 sagas are finalized (deleted from storage by <c>Finalize()</c>).
    /// </summary>
    [Fact]
    public async Task ConcurrentSagas_InterleavedEvents_AllSagasReachFinalState()
    {
        // Arrange — 2 min timeout (10 sagas × 2 events × round-trips)
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));

        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");
        (string exchangeName, string queueName) =
            await DeployTopologyAsync(adapter, suffix, cts.Token);

        // Create 10 unique order IDs
        Guid[] orderIds = Enumerable.Range(0, SagaCount)
            .Select(_ => Guid.NewGuid())
            .ToArray();

        // Phase 1 — publish all OrderCreated events (interleaved for all 10 sagas)
        for (int i = 0; i < SagaCount; i++)
        {
            await PublishEventAsync(
                adapter, exchangeName, queueName,
                new BareWire.IntegrationTests.Saga.OrderCreated(
                    OrderId: orderIds[i],
                    OrderNumber: $"ORD-CONC-{i:D3}",
                    Amount: 100m * (i + 1)),
                cts.Token);
        }

        // Phase 2 — publish all PaymentReceived events (interleaved — reversed order to stress concurrency)
        for (int i = SagaCount - 1; i >= 0; i--)
        {
            await PublishEventAsync(
                adapter, exchangeName, queueName,
                new BareWire.IntegrationTests.Saga.PaymentReceived(OrderId: orderIds[i]),
                cts.Token);
        }

        // Process all OrderCreated events (10 messages)
        for (int i = 0; i < SagaCount; i++)
        {
            await ConsumeAndProcessAsync<BareWire.IntegrationTests.Saga.OrderCreated>(
                adapter, queueName, cts.Token);
        }

        // Process all PaymentReceived events (10 messages)
        for (int i = 0; i < SagaCount; i++)
        {
            await ConsumeAndProcessAsync<BareWire.IntegrationTests.Saga.PaymentReceived>(
                adapter, queueName, cts.Token);
        }

        // Assert — all 10 sagas are finalized (Finalize() deletes them from the repository)
        _dbContext.ChangeTracker.Clear();

        for (int i = 0; i < SagaCount; i++)
        {
            BareWire.IntegrationTests.Saga.OrderSagaState? saga =
                await _dbContext.Set<BareWire.IntegrationTests.Saga.OrderSagaState>()
                    .FindAsync([orderIds[i]], cts.Token);

            saga.Should().BeNull(
                because: $"saga {orderIds[i]} (ORD-CONC-{i:D3}) must be finalized after PaymentReceived");
        }
    }
}

// ── Local ConsumeContext for E2E saga concurrency tests ───────────────────────

/// <summary>
/// A minimal concrete subclass of <see cref="ConsumeContext"/> used in E2E saga concurrency tests.
/// </summary>
internal sealed class E2ESagaConcurrencyConsumeContext(
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
