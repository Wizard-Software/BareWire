using System.Buffers;
using BenchmarkDotNet.Attributes;
using BareWire.Abstractions;
using BareWire.Abstractions.Saga;
using BareWire.Saga;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.Benchmarks;

/// <summary>
/// Benchmarks for SAGA state machine throughput using <see cref="InMemorySagaRepository{TSaga}"/>.
/// Measures the full state-transition cycle: load → execute activities → persist.
/// </summary>
/// <remarks>
/// Performance targets:
/// <list type="bullet">
/// <item><description>StateTransition_InMemory: &gt; 100K transitions/s, &lt; 512 B/transition</description></item>
/// </list>
/// NOTE: [EventPipeProfiler] is intentionally omitted — BenchmarkDotNet has a known bug with
/// .NET 10 where runtime detection treats it as v1 (https://github.com/dotnet/BenchmarkDotNet/issues/2699).
/// Add [EventPipeProfiler] after BenchmarkDotNet ships a fix.
/// <para>
/// The <see cref="Instances"/> parameter controls how many pre-created saga instances are
/// transitioned per benchmark invocation, modelling load from multiple concurrent correlations.
/// </para>
/// </remarks>
[MemoryDiagnoser(displayGenColumns: true)]
public class SagaBenchmarks
{
    /// <summary>
    /// Number of saga instances to transition per benchmark invocation.
    /// Models scenarios from single-instance through high-concurrency batch processing.
    /// </summary>
    [Params(1, 10, 100)]
    public int Instances { get; set; }

    private InMemorySagaRepository<BenchmarkSagaState> _repository = null!;
    private StateMachineExecutor<BenchmarkSagaState> _executor = null!;
    private BenchmarkConsumeContext _consumeContext = null!;

    // Pre-allocated correlation IDs and events — created once in GlobalSetup, reused every iteration.
    private Guid[] _correlationIds = null!;
    private BenchmarkOrderCreated[] _createEvents = null!;

    [GlobalSetup]
    public void Setup()
    {
        var stateMachine = new BenchmarkSagaStateMachine();
        var definition = StateMachineDefinition<BenchmarkSagaState>.Build(stateMachine);

        _repository = new InMemorySagaRepository<BenchmarkSagaState>();

        _executor = new StateMachineExecutor<BenchmarkSagaState>(
            definition,
            _repository,
            NullLogger<StateMachineExecutor<BenchmarkSagaState>>.Instance);

        _consumeContext = new BenchmarkConsumeContext(
            messageId: Guid.NewGuid(),
            correlationId: null,
            conversationId: null,
            sourceAddress: null,
            destinationAddress: null,
            sentTime: null,
            headers: new Dictionary<string, string>(),
            contentType: "application/json",
            rawBody: ReadOnlySequence<byte>.Empty,
            publishEndpoint: NoOpPublishEndpoint.Instance,
            sendEndpointProvider: NoOpSendEndpointProvider.Instance);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Re-create the repository and correlation IDs for each iteration so each invocation
        // starts with N fresh saga instances in the "Initial" state.
        _repository = new InMemorySagaRepository<BenchmarkSagaState>();

        var stateMachine = new BenchmarkSagaStateMachine();
        var definition = StateMachineDefinition<BenchmarkSagaState>.Build(stateMachine);
        _executor = new StateMachineExecutor<BenchmarkSagaState>(
            definition,
            _repository,
            NullLogger<StateMachineExecutor<BenchmarkSagaState>>.Instance);

        _correlationIds = new Guid[Instances];
        _createEvents = new BenchmarkOrderCreated[Instances];
        for (int i = 0; i < Instances; i++)
        {
            Guid id = Guid.NewGuid();
            _correlationIds[i] = id;
            _createEvents[i] = new BenchmarkOrderCreated(OrderId: id, Amount: 99.99m);
        }
    }

    /// <summary>
    /// Executes one full state-transition cycle per saga instance: receives an event,
    /// loads the saga (or creates it), applies activities, and persists the new state.
    /// Target: &gt; 100K transitions/s, &lt; 512 B/transition.
    /// </summary>
    [Benchmark]
    public async Task StateTransition_InMemory()
    {
        for (int i = 0; i < Instances; i++)
        {
            await _executor
                .ProcessEventAsync(_createEvents[i], _consumeContext)
                .ConfigureAwait(false);
        }
    }

    // ── No-op infrastructure ──────────────────────────────────────────────────

    /// <summary>
    /// A minimal concrete subclass of <see cref="ConsumeContext"/> used by benchmarks.
    /// All constructor parameters are forwarded to the base class.
    /// </summary>
    private sealed class BenchmarkConsumeContext(
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

    /// <summary>
    /// A no-op publish endpoint used in the benchmark consume context.
    /// SAGA activities that call <c>Publish</c> complete without I/O overhead.
    /// </summary>
    private sealed class NoOpPublishEndpoint : IPublishEndpoint
    {
        internal static readonly NoOpPublishEndpoint Instance = new();

        public Task PublishAsync<T>(T message, CancellationToken cancellationToken = default)
            where T : class
            => Task.CompletedTask;

        public Task PublishRawAsync(ReadOnlyMemory<byte> payload, string contentType,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>
    /// A no-op send endpoint provider used in the benchmark consume context.
    /// </summary>
    private sealed class NoOpSendEndpointProvider : ISendEndpointProvider
    {
        internal static readonly NoOpSendEndpointProvider Instance = new();

        public Task<ISendEndpoint> GetSendEndpoint(Uri address, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Send endpoints are not used in saga benchmarks.");
    }
}

// ── Benchmark saga state machine definition ───────────────────────────────────

/// <summary>
/// A minimal SAGA state type used for state transition benchmarks.
/// Models a simple two-state lifecycle: Initial → Processing.
/// </summary>
public sealed class BenchmarkSagaState : ISagaState
{
    /// <inheritdoc />
    public Guid CorrelationId { get; set; }

    /// <inheritdoc />
    public string CurrentState { get; set; } = "Initial";

    /// <inheritdoc />
    public int Version { get; set; }

    /// <summary>Gets or sets the monetary amount captured from the triggering event.</summary>
    public decimal Amount { get; set; }
}

/// <summary>
/// Event that initiates a new benchmark saga instance, carrying the order identifier and amount.
/// </summary>
public sealed record BenchmarkOrderCreated(Guid OrderId, decimal Amount);

/// <summary>
/// A minimal state machine used by <see cref="SagaBenchmarks"/> to measure the
/// initial-state transition: <c>Initial</c> → <c>Processing</c>.
/// </summary>
public sealed class BenchmarkSagaStateMachine : BareWireStateMachine<BenchmarkSagaState>
{
    /// <summary>Initializes a new instance of <see cref="BenchmarkSagaStateMachine"/>.</summary>
    public BenchmarkSagaStateMachine()
    {
        var orderCreated = Event<BenchmarkOrderCreated>();
        var processing = State("Processing");

        CorrelateBy<BenchmarkOrderCreated>(e => e.OrderId);

        Initially(() =>
        {
            When(orderCreated, b => b
                .Then((saga, evt) =>
                {
                    saga.Amount = evt.Amount;
                    return Task.CompletedTask;
                })
                .TransitionTo(processing.Name));
        });
    }
}
