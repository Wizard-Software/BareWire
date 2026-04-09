using System.Buffers;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Routing;
using BareWire.Abstractions.Serialization;
using BareWire.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.Configuration;
using BareWire.FlowControl;
using BareWire;
using BareWire.Pipeline;
using BareWire.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace BareWire.Testing;

/// <summary>
/// A test harness that wires up a fully functional in-process <see cref="IBus"/> backed by
/// <see cref="InMemoryTransportAdapter"/>. No external broker is required.
/// </summary>
/// <remarks>
/// Obtain an instance via <see cref="CreateAsync"/>. The harness starts the bus automatically
/// and stops it when disposed.
/// <para>
/// Use <see cref="Bus"/> to publish or send messages. Use <see cref="WaitForPublishAsync{T}"/>
/// or <see cref="WaitForSendAsync{T}"/> to observe outbound messages without polling.
/// </para>
/// </remarks>
public sealed class BareWireTestHarness : IAsyncDisposable
{
    private readonly InMemoryTransportAdapter _adapter;
    private readonly BareWireBusControl _busControl;
    private readonly IRoutingKeyResolver _routingKeyResolver;

    private BareWireTestHarness(InMemoryTransportAdapter adapter, BareWireBusControl busControl, IRoutingKeyResolver routingKeyResolver)
    {
        _adapter = adapter;
        _busControl = busControl;
        _routingKeyResolver = routingKeyResolver;
    }

    /// <summary>
    /// Gets the running <see cref="IBus"/> backed by the in-memory transport.
    /// </summary>
    public IBus Bus => _busControl;

    /// <summary>
    /// Creates a new <see cref="BareWireTestHarness"/>, starts the bus, and returns the harness.
    /// </summary>
    /// <param name="configure">
    /// An optional callback that receives an <see cref="IBusConfigurator"/> to apply
    /// custom configuration. Currently a placeholder — full DI-based wiring is completed in Phase 1.15.
    /// </param>
    /// <param name="routingKeyResolver">
    /// An optional <see cref="IRoutingKeyResolver"/> to override the default fallback resolver.
    /// When <see langword="null"/>, a resolver with empty mappings (falls back to <c>typeof(T).FullName</c>) is used.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the startup sequence.</param>
    /// <returns>A started <see cref="BareWireTestHarness"/> ready for use in tests.</returns>
    public static async Task<BareWireTestHarness> CreateAsync(
        Action<IBusConfigurator>? configure = null,
        IRoutingKeyResolver? routingKeyResolver = null,
        CancellationToken cancellationToken = default)
    {
        InMemoryTransportAdapter adapter = new();

        // Minimal no-op serializer and deserializer for testing — tests typically call PublishAsync
        // with a message that round-trips through the in-memory transport without real serialization.
        IMessageSerializer serializer = new NoOpMessageSerializer();
        IMessageDeserializer deserializer = new NoOpMessageDeserializer();
        IDeserializerResolver deserializerResolver = new SingleDeserializerResolver(deserializer);

        // Use NullLoggerFactory so the harness produces no log output by default.
        ILoggerFactory loggerFactory = NullLoggerFactory.Instance;

        FlowController flowController = new(loggerFactory.CreateLogger<FlowController>());
        MiddlewareChain middlewareChain = new([]);

        MessagePipeline pipeline = new(
            middlewareChain: middlewareChain,
            deserializerResolver: deserializerResolver,
            logger: loggerFactory.CreateLogger<MessagePipeline>(),
            instrumentation: new NullInstrumentation());

        PublishFlowControlOptions publishFlowControl = new();
        IRoutingKeyResolver resolver = routingKeyResolver ?? new RoutingKeyResolver();

        ISerializerResolver serializerResolver = new DefaultSerializerResolver(serializer);
        BareWireBus bus = new(
            adapter: adapter,
            serializerResolver: serializerResolver,
            pipeline: pipeline,
            flowController: flowController,
            publishFlowControl: publishFlowControl,
            logger: loggerFactory.CreateLogger<BareWireBus>(),
            instrumentation: new NullInstrumentation(),
            routingKeyResolver: resolver);

        BusConfigurator configurator = new() { HasInMemoryTransport = true };
        configure?.Invoke(configurator);

        BareWireBusControl busControl = new(
            bus: bus,
            adapter: adapter,
            flowController: flowController,
            configurator: configurator,
            logger: loggerFactory.CreateLogger<BareWireBusControl>(),
            topology: null,
            endpointBindings: [],
            deserializerResolver: deserializerResolver,
            scopeFactory: new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            instrumentation: new NullInstrumentation(),
            loggerFactory: loggerFactory,
            sagaDispatchers: []);

        await busControl.StartAsync(cancellationToken).ConfigureAwait(false);

        return new BareWireTestHarness(adapter, busControl, resolver);
    }

    /// <summary>
    /// Waits until a message whose routing key matches <typeparamref name="T"/> is published
    /// through the in-memory transport, or until <paramref name="timeout"/> elapses.
    /// </summary>
    /// <typeparam name="T">The expected outbound message type.</typeparam>
    /// <param name="timeout">Maximum time to wait before throwing <see cref="TimeoutException"/>.</param>
    /// <returns>The first matching <see cref="OutboundMessage"/> observed on the transport.</returns>
    /// <exception cref="TimeoutException">
    /// Thrown when no matching message is observed within <paramref name="timeout"/>.
    /// </exception>
    public Task<OutboundMessage> WaitForPublishAsync<T>(TimeSpan timeout) where T : class
        => WaitForMessageAsync<T>(timeout);

    /// <summary>
    /// Waits until a message whose routing key matches <typeparamref name="T"/> is sent to an endpoint
    /// through the in-memory transport, or until <paramref name="timeout"/> elapses.
    /// </summary>
    /// <typeparam name="T">The expected outbound message type.</typeparam>
    /// <param name="timeout">Maximum time to wait before throwing <see cref="TimeoutException"/>.</param>
    /// <returns>The first matching <see cref="OutboundMessage"/> observed on the transport.</returns>
    /// <exception cref="TimeoutException">
    /// Thrown when no matching message is observed within <paramref name="timeout"/>.
    /// </exception>
    public Task<OutboundMessage> WaitForSendAsync<T>(TimeSpan timeout) where T : class
        => WaitForMessageAsync<T>(timeout);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _busControl.StopAsync().ConfigureAwait(false);
        await _adapter.DisposeAsync().ConfigureAwait(false);
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private Task<OutboundMessage> WaitForMessageAsync<T>(TimeSpan timeout) where T : class
    {
        string expectedRoutingKey = _routingKeyResolver.Resolve<T>();

        TaskCompletionSource<OutboundMessage> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource timeoutCts = new();
        timeoutCts.CancelAfter(timeout);

        CancellationTokenRegistration registration = timeoutCts.Token.Register(
            static state =>
            {
                (TaskCompletionSource<OutboundMessage> source, string key) = ((TaskCompletionSource<OutboundMessage>, string))state!;
                source.TrySetException(new TimeoutException(
                    $"No message matching routing key '{key}' was observed within the timeout."));
            },
            (tcs, expectedRoutingKey));

        void OnMessageSent(OutboundMessage msg)
        {
            if (msg.RoutingKey == expectedRoutingKey)
                tcs.TrySetResult(msg);
        }

        _adapter.MessageSent += OnMessageSent;

        // Detach subscription and dispose CTS when the TCS resolves (success, cancel, or fault).
        _ = tcs.Task.ContinueWith(
            _ =>
            {
                _adapter.MessageSent -= OnMessageSent;
                registration.Dispose();
                timeoutCts.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return tcs.Task;
    }

    // ── No-op serializer / deserializer ───────────────────────────────────────

    private sealed class NoOpMessageSerializer : IMessageSerializer
    {
        public string ContentType => "application/octet-stream";

        public void Serialize<T>(T message, IBufferWriter<byte> output) where T : class
        {
            // No-op: in-memory harness tests route by type name only;
            // payload content is not inspected by the harness infrastructure.
        }
    }

    private sealed class NoOpMessageDeserializer : IMessageDeserializer
    {
        public string ContentType => "application/octet-stream";

        public T? Deserialize<T>(ReadOnlySequence<byte> data) where T : class => null;
    }
}
