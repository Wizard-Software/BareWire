using System.Diagnostics;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Saga;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Configuration;
using BareWire.FlowControl;
using BareWire.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BareWire.Bus;

internal sealed partial class BareWireBusControl : IBusControl
{
    private readonly BareWireBus _bus;
    private readonly ITransportAdapter? _adapter;
    private readonly FlowController _flowController;
    private readonly BusConfigurator _configurator;
    private readonly ILogger<BareWireBusControl> _logger;
    private readonly TopologyDeclaration? _topology;
    private readonly IReadOnlyList<EndpointBinding> _endpointBindings;
    private readonly IDeserializerResolver _deserializerResolver;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Abstractions.Observability.IBareWireInstrumentation _instrumentation;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IReadOnlyList<ISagaMessageDispatcher> _sagaDispatchers;
    private readonly TimeSpan _drainTimeout;

    private readonly object _stateLock = new();
    private readonly List<Task> _consumeTasks = [];
    private CancellationTokenSource? _consumeCts;
    private bool _started;

    // How often the graceful-drain quiescence check re-observes the publish loop and the
    // transport's own accepted-message counters while waiting for them to settle. Applies only
    // during shutdown, bounded by _drainTimeout — never on any hot path.
    private static readonly TimeSpan QuiescencePollInterval = TimeSpan.FromMilliseconds(5);

    internal BareWireBusControl(
        BareWireBus bus,
        ITransportAdapter? adapter,
        FlowController flowController,
        BusConfigurator configurator,
        ILogger<BareWireBusControl> logger,
        TopologyDeclaration? topology,
        IReadOnlyList<EndpointBinding> endpointBindings,
        IDeserializerResolver deserializerResolver,
        IServiceScopeFactory scopeFactory,
        Abstractions.Observability.IBareWireInstrumentation instrumentation,
        ILoggerFactory loggerFactory,
        IReadOnlyList<ISagaMessageDispatcher> sagaDispatchers,
        BusShutdownOptions? shutdownOptions = null)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));

        // The adapter is intentionally nullable (15.3 / C1): a missing transport must produce the
        // friendly BareWireConfigurationException from StartAsync, not a raw ArgumentNullException
        // (or InvalidOperationException from GetRequiredService) during DI graph construction.
        _adapter = adapter;
        _flowController = flowController ?? throw new ArgumentNullException(nameof(flowController));
        _configurator = configurator ?? throw new ArgumentNullException(nameof(configurator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _topology = topology;
        _endpointBindings = endpointBindings ?? [];
        _deserializerResolver = deserializerResolver ?? throw new ArgumentNullException(nameof(deserializerResolver));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _instrumentation = instrumentation ?? throw new ArgumentNullException(nameof(instrumentation));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _sagaDispatchers = sagaDispatchers ?? [];
        _drainTimeout = (shutdownOptions ?? new BusShutdownOptions()).DrainTimeout;
    }

    // ── IBusControl ───────────────────────────────────────────────────────────

    public async Task<BusHandle> StartAsync(CancellationToken cancellationToken = default)
    {
        // Fail fast: validate configuration before attempting to start the bus.
        // Transport presence is determined by the FACT that an ITransportAdapter was resolved into
        // this control (D5 / ADR-028). Since 15.3 the adapter is resolved via GetService (nullable),
        // so a missing transport leaves _adapter null and the validator raises the friendly
        // BareWireConfigurationException FIRST — before any raw DI/NRE error can leak out (C1 / E6).
        bool transportRegistered = _adapter is not null;
        ConfigurationValidator.Validate(_configurator, transportRegistered);

        // Advisory diagnostic (SEC-13 / ADR-030 §Security): an endpoint that declares AcceptUntyped()
        // without a registered schema-validation middleware exposes a type-less foreign-input trust
        // boundary. Emit a warning naming the endpoint (no raw routing key — none exists at startup).
        // The bus only goes live here in StartAsync, so this is the right gate; DeployTopologyAsync
        // starts no consumers and is intentionally not covered.
        // Task 19.11: read from the MATERIALIZED _endpointBindings/_topology (definition-merged
        // ConsumerDefinition<T> flags are invisible to _configurator.ReceiveEndpoints).
        UntypedTrustBoundaryDiagnostic.Run(_endpointBindings, _topology, _configurator, _logger);

        // After validation succeeds, the adapter is guaranteed non-null (the validator throws
        // otherwise). Capture it in a non-null local so the remainder of StartAsync stays
        // warning-free under TreatWarningsAsErrors (R4).
        ITransportAdapter adapter = _adapter!;

        lock (_stateLock)
        {
            if (_started)
                throw new InvalidOperationException("Bus is already started. Call StopAsync before starting again.");

            _started = true;
        }

        LogBusStarting(_logger, _bus.BusId);

        // Deploy topology (exchanges, queues, bindings) to the broker.
        if (_topology is not null)
        {
            await adapter.DeployTopologyAsync(_topology, cancellationToken).ConfigureAwait(false);
            LogTopologyDeployed(_logger, _topology.Exchanges.Count, _topology.Queues.Count);
        }

        // Start the publish loop.
        _bus.StartPublishing();

        // Start a consume loop for each configured receive endpoint.
        _consumeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken consumeToken = _consumeCts.Token;

        // Saga dispatchers are injected via constructor — shared across all endpoints.
        // Each endpoint's ReceiveEndpointRunner filters to only the dispatchers relevant to it.
        foreach (EndpointBinding binding in _endpointBindings)
        {
            if (binding.Consumers.Count == 0
                && binding.RawConsumers.Count == 0
                && binding.SagaTypes.Count == 0)
                continue;

            // Resolve per-endpoint deserializer override if configured (task 11.8).
            IDeserializerResolver endpointResolver = _deserializerResolver;
            if (binding.DeserializerOverrideType is not null)
            {
                await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
                IMessageDeserializer perEndpointDeserializer =
                    (IMessageDeserializer)scope.ServiceProvider.GetRequiredService(binding.DeserializerOverrideType);
                endpointResolver = new SingleDeserializerResolver(perEndpointDeserializer);
            }

            // Per-consumer MassTransit envelope opt-in (task 18.5, D4 precedence): if any consumer
            // on this endpoint opts into MT envelope deserialization, resolve the MT deserializer
            // from the GLOBAL chain (not from endpointResolver — per-consumer must shadow per-endpoint).
            // Fail-fast when AddMassTransitEnvelopeDeserializer() has not been called so the bus never
            // starts accepting messages with a misconfigured deserializer chain (fail-closed / SEC).
            IMessageDeserializer? mtDeserializer = null;
            if (binding.Consumers.Any(c => c.UseMassTransitEnvelope))
            {
                IMessageDeserializer mt = _deserializerResolver.Resolve("application/vnd.masstransit+json");
                if (mt.ContentType != "application/vnd.masstransit+json")
                {
                    throw new BareWireConfigurationException(
                        optionName: $"UseMassTransitEnvelope (endpoint '{binding.EndpointName}')",
                        optionValue: mt.ContentType,
                        expectedValue: "Call services.AddMassTransitEnvelopeDeserializer() before starting the bus.");
                }

                mtDeserializer = mt;
            }

            // Validate consumer ordering configuration at startup (fail-fast, R8.11).
            // Throws BareWireConfigurationException when no guaranteed ordering path is declared.
            // Resolver is validate-only — the returned struct is discarded (OQ2: runner reads
            // binding.Ordering directly; ResolvedConsumerOrdering is not threaded into the runner).
            if (binding.Ordering is not null)
            {
                ConsumerOrderingStrategyResolver.Resolve(
                    binding.Ordering,
                    adapter.Capabilities,
                    adapter.TransportName,
                    binding.EndpointName);
            }

            var runner = new ReceiveEndpointRunner(
                binding,
                adapter,
                endpointResolver,
                _bus, // IPublishEndpoint
                _bus, // ISendEndpointProvider
                _scopeFactory,
                _flowController,
                _instrumentation,
                _loggerFactory.CreateLogger<ReceiveEndpointRunner>(),
                _sagaDispatchers,
                _loggerFactory,
                mtDeserializer);

            _consumeTasks.Add(Task.Run(() => runner.RunAsync(consumeToken), CancellationToken.None));
        }

        LogBusStarted(_logger, _bus.BusId);

        return await Task.FromResult(new BusHandle(_bus.BusId)).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (!_started)
                return;

            _started = false;
        }

        LogBusStopping(_logger, _bus.BusId);

        // When the transport adapter buffers accepted messages in process (for example the
        // in-memory transport), cancelling consumer loops immediately would silently drop those
        // messages. Let consumers and the publish loop keep running until a stable quiescence
        // check passes, the drain time limit elapses, or the caller's own cancellation token is
        // cancelled — whichever happens first. Adapters that do not implement this coordination
        // protocol (broker-backed transports) are unaffected: the shutdown path below then runs
        // exactly as it did before this drain step existed.
        if (_adapter is IGracefulDrainTransport drainTransport && _consumeCts is not null)
        {
            await DrainBeforeCancellingConsumersAsync(drainTransport, cancellationToken).ConfigureAwait(false);
        }

        // Cancel consume loops.
        if (_consumeCts is not null)
        {
            await _consumeCts.CancelAsync().ConfigureAwait(false);

            try
            {
                await Task.WhenAll(_consumeTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during graceful shutdown.
            }
            catch (Exception ex)
            {
                LogConsumeShutdownError(_logger, ex);
            }

            _consumeCts.Dispose();
            _consumeCts = null;
            _consumeTasks.Clear();
        }

        await _bus.DisposeAsync().ConfigureAwait(false);

        LogBusStopped(_logger, _bus.BusId);
    }

    /// <summary>
    /// Waits for a stable quiescence signal — the transport's accepted-message counters and the
    /// publish loop both idle, checked twice in the same pass with no batch passing through in
    /// between — before <see cref="StopAsync"/> cancels consumer loops, so in-flight work
    /// (including a follow-up message published from a handler right before it acknowledges its
    /// own message) has a chance to be accepted by the transport first. Bounded by
    /// <see cref="_drainTimeout"/> or by <paramref name="cancellationToken"/>, whichever elapses
    /// first. Any failure while draining is logged and swallowed — shutdown always proceeds to
    /// cancelling consumer loops, it must never be blocked by a transport error.
    /// </summary>
    private async Task DrainBeforeCancellingConsumersAsync(
        IGracefulDrainTransport drainTransport, CancellationToken cancellationToken)
    {
        long startTimestamp = Stopwatch.GetTimestamp();
        LogDrainStarting(_logger, _bus.BusId, _drainTimeout.TotalMilliseconds);

        CancellationTokenSource? budgetCts = null;
        try
        {
            // The transport's own "timeout" argument is only a hint an adapter may or may not
            // honour — this token is what actually enforces the limit, whether it is reached
            // because DrainTimeout elapsed or because the caller cancelled cancellationToken.
            // Every DrainAsync call is additionally awaited through WaitAsync(budget), so an
            // adapter that ignores both its timeout and its token cannot stall shutdown.
            budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budgetCts.CancelAfter(_drainTimeout);

            while (true)
            {
                // (1) Wait for the transport's own accepted-message counters to reach zero.
                await drainTransport.DrainAsync(RemainingBudget(), budgetCts.Token)
                    .WaitAsync(budgetCts.Token).ConfigureAwait(false);
                await ThrowIfBudgetExhaustedAsync(budgetCts).ConfigureAwait(false);

                // (2) Snapshot the publish loop's idleness and batch epoch.
                long epochBeforeRecheck = _bus.PublishBatchEpoch;
                bool idle = _bus.IsPublishIdle;

                if (idle)
                {
                    // (3) Re-check the transport's counters — a message accepted between (1) and
                    // (2) must be drained again before quiescence can be declared.
                    await drainTransport.DrainAsync(RemainingBudget(), budgetCts.Token)
                        .WaitAsync(budgetCts.Token).ConfigureAwait(false);
                    await ThrowIfBudgetExhaustedAsync(budgetCts).ConfigureAwait(false);

                    // (4) Quiescence requires the publish loop to still be idle with no batch
                    // having passed through it since (2) — closing the window where a follow-up
                    // message could have been published and already sent between the two checks.
                    if (_bus.IsPublishIdle && _bus.PublishBatchEpoch == epochBeforeRecheck)
                        break;
                }

                await Task.Delay(QuiescencePollInterval, budgetCts.Token).ConfigureAwait(false);
            }

            double completedElapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            LogDrainCompleted(_logger, _bus.BusId, completedElapsedMs);
        }
        catch (OperationCanceledException) when (budgetCts is not null && budgetCts.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                double cancelledElapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                LogDrainCancelled(_logger, _bus.BusId, cancelledElapsedMs);
            }
            else
            {
                LogDrainTimedOut(_logger, _bus.BusId, _drainTimeout.TotalMilliseconds);
            }
        }
        catch (Exception ex)
        {
            LogDrainError(_logger, _bus.BusId, ex);
        }
        finally
        {
            budgetCts?.Dispose();
        }

        TimeSpan RemainingBudget()
        {
            TimeSpan elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            TimeSpan remaining = _drainTimeout - elapsed;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        // A DrainAsync call that returned because its own timeout elapsed says nothing about the
        // transport's counters, so an exhausted budget is treated as a timeout, never as quiescence.
        async Task ThrowIfBudgetExhaustedAsync(CancellationTokenSource budget)
        {
            if (RemainingBudget() == TimeSpan.Zero)
                await budget.CancelAsync().ConfigureAwait(false);

            budget.Token.ThrowIfCancellationRequested();
        }
    }

    public async Task DeployTopologyAsync(CancellationToken cancellationToken = default)
    {
        // Validate transport presence (the adapter is nullable since 15.3 / C1) so a missing
        // transport surfaces the friendly BareWireConfigurationException rather than a raw NRE,
        // even when topology is deployed independently of StartAsync.
        ConfigurationValidator.Validate(_configurator, transportRegistered: _adapter is not null);
        ITransportAdapter adapter = _adapter!;

        TopologyDeclaration topology = _topology ?? new TopologyDeclaration();
        await adapter.DeployTopologyAsync(topology, cancellationToken).ConfigureAwait(false);
    }

    public BusHealthStatus CheckHealth()
    {
        IReadOnlyCollection<string> endpointNames = _flowController.GetAllEndpointNames();

        BusHealthStatus? transportHealth = _adapter is ITransportHealthSource healthSource
            ? healthSource.GetHealth()
            : null;

        List<EndpointHealthStatus> endpointStatuses = new(endpointNames.Count + (transportHealth?.Endpoints.Count ?? 0));
        BusStatus worstStatus = BusStatus.Healthy;

        foreach (string endpointName in endpointNames)
        {
            BusStatus status = _flowController.CheckHealth(endpointName);

            if (status > worstStatus)
                worstStatus = status;

            endpointStatuses.Add(new EndpointHealthStatus(endpointName, status, Description: null));
        }

        // Worst status contributed by the transport itself (aggregate and per-queue), tracked separately
        // so that its description is only appended when the transport is the one reporting trouble.
        BusStatus transportWorstStatus = BusStatus.Healthy;

        if (transportHealth is not null)
        {
            endpointStatuses.AddRange(transportHealth.Endpoints);

            transportWorstStatus = transportHealth.Status;

            foreach (EndpointHealthStatus endpoint in transportHealth.Endpoints)
            {
                if (endpoint.Status > transportWorstStatus)
                    transportWorstStatus = endpoint.Status;
            }

            if (transportWorstStatus > worstStatus)
                worstStatus = transportWorstStatus;
        }

        string description = worstStatus switch
        {
            BusStatus.Healthy => "All endpoints are operating normally.",
            BusStatus.Degraded => "One or more endpoints are approaching capacity.",
            BusStatus.Unhealthy => "One or more endpoints are at capacity.",
            _ => "Unknown status.",
        };

        if (transportHealth is not null
            && transportWorstStatus != BusStatus.Healthy
            && !string.IsNullOrWhiteSpace(transportHealth.Description))
        {
            description = $"{description} {transportHealth.Description}";
        }

        return new BusHealthStatus(worstStatus, description, endpointStatuses);
    }

    // ── IBus delegation ───────────────────────────────────────────────────────

    public Guid BusId => _bus.BusId;
    public Uri Address => _bus.Address;

    public Task PublishAsync<T>(T message, CancellationToken cancellationToken = default) where T : class
        => _bus.PublishAsync(message, cancellationToken);

    public Task PublishAsync<T>(T message, IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken = default) where T : class
        => _bus.PublishAsync(message, headers, cancellationToken);

    public Task PublishRawAsync(ReadOnlyMemory<byte> payload, string contentType, CancellationToken cancellationToken = default)
        => _bus.PublishRawAsync(payload, contentType, cancellationToken);

    public Task<ISendEndpoint> GetSendEndpoint(Uri address, CancellationToken cancellationToken = default)
        => _bus.GetSendEndpoint(address, cancellationToken);

    public ValueTask<IRequestClient<T>> CreateRequestClientAsync<T>(
        CancellationToken cancellationToken = default) where T : class
        => _bus.CreateRequestClientAsync<T>(cancellationToken);

    public IDisposable ConnectReceiveEndpoint(string queueName, Action<IReceiveEndpointConfigurator> configure)
        => _bus.ConnectReceiveEndpoint(queueName, configure);

    // ── IAsyncDisposable / IDisposable ────────────────────────────────────────

    public ValueTask DisposeAsync() => _bus.DisposeAsync();
    public void Dispose() => _bus.Dispose();

    // ── Logger messages ───────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "BareWire bus {BusId} starting.")]
    private static partial void LogBusStarting(ILogger logger, Guid busId);

    [LoggerMessage(Level = LogLevel.Information, Message = "BareWire bus {BusId} started.")]
    private static partial void LogBusStarted(ILogger logger, Guid busId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Topology deployed: {ExchangeCount} exchange(s), {QueueCount} queue(s).")]
    private static partial void LogTopologyDeployed(ILogger logger, int exchangeCount, int queueCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "BareWire bus {BusId} stopping.")]
    private static partial void LogBusStopping(ILogger logger, Guid busId);

    [LoggerMessage(Level = LogLevel.Information, Message = "BareWire bus {BusId} stopped.")]
    private static partial void LogBusStopped(ILogger logger, Guid busId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error during consume loop shutdown.")]
    private static partial void LogConsumeShutdownError(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "BareWire bus {BusId} draining in-flight messages before stopping consumers (limit {DrainTimeoutMs} ms).")]
    private static partial void LogDrainStarting(ILogger logger, Guid busId, double drainTimeoutMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "BareWire bus {BusId} drained in {ElapsedMs} ms.")]
    private static partial void LogDrainCompleted(ILogger logger, Guid busId, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "BareWire bus {BusId} drain did not complete within {DrainTimeoutMs} ms; remaining messages are not delivered.")]
    private static partial void LogDrainTimedOut(ILogger logger, Guid busId, double drainTimeoutMs);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "BareWire bus {BusId} drain was cancelled by the stop token after {ElapsedMs} ms.")]
    private static partial void LogDrainCancelled(ILogger logger, Guid busId, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "BareWire bus {BusId} drain failed; continuing shutdown.")]
    private static partial void LogDrainError(ILogger logger, Guid busId, Exception ex);
}
