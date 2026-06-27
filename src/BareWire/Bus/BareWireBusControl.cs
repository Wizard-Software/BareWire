using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
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

    private readonly object _stateLock = new();
    private readonly List<Task> _consumeTasks = [];
    private CancellationTokenSource? _consumeCts;
    private bool _started;

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
        IReadOnlyList<ISagaMessageDispatcher> sagaDispatchers)
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
        UntypedTrustBoundaryDiagnostic.Run(_configurator, _logger);

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
                _loggerFactory);

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

        List<EndpointHealthStatus> endpointStatuses = new(endpointNames.Count);
        BusStatus worstStatus = BusStatus.Healthy;

        foreach (string endpointName in endpointNames)
        {
            BusStatus status = _flowController.CheckHealth(endpointName);

            if (status > worstStatus)
                worstStatus = status;

            endpointStatuses.Add(new EndpointHealthStatus(endpointName, status, Description: null));
        }

        string description = worstStatus switch
        {
            BusStatus.Healthy => "All endpoints are operating normally.",
            BusStatus.Degraded => "One or more endpoints are approaching capacity.",
            BusStatus.Unhealthy => "One or more endpoints are at capacity.",
            _ => "Unknown status.",
        };

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
}
