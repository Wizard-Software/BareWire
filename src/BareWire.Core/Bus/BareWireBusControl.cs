using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Saga;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Core.Configuration;
using BareWire.Core.FlowControl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BareWire.Core.Bus;

internal sealed partial class BareWireBusControl : IBusControl
{
    private readonly BareWireBus _bus;
    private readonly ITransportAdapter _adapter;
    private readonly FlowController _flowController;
    private readonly BusConfigurator _configurator;
    private readonly ILogger<BareWireBusControl> _logger;
    private readonly TopologyDeclaration? _topology;
    private readonly IReadOnlyList<EndpointBinding> _endpointBindings;
    private readonly IMessageDeserializer _deserializer;
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
        ITransportAdapter adapter,
        FlowController flowController,
        BusConfigurator configurator,
        ILogger<BareWireBusControl> logger,
        TopologyDeclaration? topology,
        IReadOnlyList<EndpointBinding> endpointBindings,
        IMessageDeserializer deserializer,
        IServiceScopeFactory scopeFactory,
        Abstractions.Observability.IBareWireInstrumentation instrumentation,
        ILoggerFactory loggerFactory,
        IReadOnlyList<ISagaMessageDispatcher> sagaDispatchers)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _flowController = flowController ?? throw new ArgumentNullException(nameof(flowController));
        _configurator = configurator ?? throw new ArgumentNullException(nameof(configurator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _topology = topology;
        _endpointBindings = endpointBindings ?? [];
        _deserializer = deserializer ?? throw new ArgumentNullException(nameof(deserializer));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _instrumentation = instrumentation ?? throw new ArgumentNullException(nameof(instrumentation));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _sagaDispatchers = sagaDispatchers ?? [];
    }

    // ── IBusControl ───────────────────────────────────────────────────────────

    public async Task<BusHandle> StartAsync(CancellationToken cancellationToken = default)
    {
        // Fail fast: validate configuration before attempting to start the bus.
        ConfigurationValidator.Validate(_configurator);

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
            await _adapter.DeployTopologyAsync(_topology, cancellationToken).ConfigureAwait(false);
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

            var runner = new ReceiveEndpointRunner(
                binding,
                _adapter,
                _deserializer,
                _bus, // IPublishEndpoint
                _bus, // ISendEndpointProvider
                _scopeFactory,
                _flowController,
                _instrumentation,
                _loggerFactory.CreateLogger<ReceiveEndpointRunner>(),
                _sagaDispatchers);

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
        TopologyDeclaration topology = _topology ?? new TopologyDeclaration();
        await _adapter.DeployTopologyAsync(topology, cancellationToken).ConfigureAwait(false);
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
