using System.Buffers;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Routing;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.Testing;

/// <summary>
/// A test harness that wires up a fully functional in-process <see cref="IBus"/> backed by the real
/// in-memory transport, in a private dependency-injection container isolated from any other harness
/// instance. No external broker is required.
/// </summary>
/// <remarks>
/// Obtain an instance via <see cref="CreateAsync(Action{IBusConfigurator}?, IRoutingKeyResolver?, IExchangeResolver?, CancellationToken)"/>.
/// The harness starts the bus automatically and stops it when disposed.
/// <para>
/// Use <see cref="Bus"/> to publish or send messages. Use <see cref="WaitForPublishAsync{T}"/>
/// or <see cref="WaitForSendAsync{T}"/> to observe outbound messages without polling.
/// </para>
/// </remarks>
public sealed class BareWireTestHarness : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly ObservingTransportAdapter _adapter;
    private readonly IBusControl _busControl;
    private readonly IRoutingKeyResolver _routingKeyResolver;

    private BareWireTestHarness(
        ServiceProvider provider,
        ObservingTransportAdapter adapter,
        IBusControl busControl,
        IRoutingKeyResolver routingKeyResolver)
    {
        _provider = provider;
        _adapter = adapter;
        _busControl = busControl;
        _routingKeyResolver = routingKeyResolver;
    }

    /// <summary>
    /// Gets the decorator wrapping the in-memory transport adapter, for direct inspection of
    /// outbound messages (including headers) and of the transport's optional coordination seams.
    /// </summary>
    internal ObservingTransportAdapter Adapter => _adapter;

    /// <summary>
    /// Gets the running <see cref="IBus"/> backed by the in-memory transport.
    /// </summary>
    public IBus Bus => (IBus)_busControl;

    /// <summary>
    /// Creates a new <see cref="BareWireTestHarness"/>, starts the bus, and returns the harness.
    /// </summary>
    /// <param name="configure">
    /// An optional callback that receives an <see cref="IBusConfigurator"/> to apply custom bus
    /// configuration (middleware, receive endpoints, serializer mappings) in the harness's private
    /// container.
    /// </param>
    /// <param name="routingKeyResolver">
    /// An optional <see cref="IRoutingKeyResolver"/> to override the default fallback resolver.
    /// When <see langword="null"/>, a resolver with empty mappings (falls back to <c>typeof(T).FullName</c>) is used.
    /// </param>
    /// <param name="exchangeResolver">
    /// An optional <see cref="IExchangeResolver"/> to override the default no-op resolver.
    /// When <see langword="null"/>, a resolver with empty mappings (returns <see langword="null"/> for all types) is used.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the startup sequence.</param>
    /// <returns>A started <see cref="BareWireTestHarness"/> ready for use in tests.</returns>
    public static Task<BareWireTestHarness> CreateAsync(
        Action<IBusConfigurator>? configure = null,
        IRoutingKeyResolver? routingKeyResolver = null,
        IExchangeResolver? exchangeResolver = null,
        CancellationToken cancellationToken = default)
        => CreateAsync(configure, routingKeyResolver, exchangeResolver, transport: null, cancellationToken);

    /// <summary>
    /// Creates a new <see cref="BareWireTestHarness"/> whose in-memory transport topology is further
    /// configured by <paramref name="transport"/> (for example to declare extra queues needed by an
    /// isolation or transport-level test) before the topology is frozen at bus startup.
    /// </summary>
    internal static async Task<BareWireTestHarness> CreateAsync(
        Action<IBusConfigurator>? configure,
        IRoutingKeyResolver? routingKeyResolver,
        IExchangeResolver? exchangeResolver,
        Action<IInMemoryConfigurator>? transport,
        CancellationToken cancellationToken)
    {
        ServiceCollection services = new();

        // NullLoggerFactory/NullLogger<> so the harness produces no log output by default.
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // Registered via TryAdd into this fresh, private container — a default registration, not a
        // global replacement. Tests typically call PublishAsync with a message that round-trips
        // through the in-memory transport without real serialization; per-type mappings configured
        // via configure (MapSerializer<,>()) still take precedence over this default.
        services.TryAddSingleton<IMessageSerializer>(new NoOpMessageSerializer());
        services.TryAddSingleton<IMessageDeserializer>(new NoOpMessageDeserializer());

        services.AddBareWireWithInMemory(
            t =>
            {
                // Compatibility mode: default exchange "" plus auto-declared endpoint queues, so a
                // plain PublishAsync/SendAsync behaves the same way it did against the previous stub.
                t.DefaultExchange(string.Empty);
                t.AutoDeclareEndpointQueues();
                transport?.Invoke(t);
            },
            configure);

        // AddBareWireWithInMemory replaces IRoutingKeyResolver/IExchangeResolver from configure's own
        // mappings — an override supplied to this method must therefore be applied AFTER the bundle
        // call, or the bundle's own Replace would win instead.
        if (routingKeyResolver is not null)
            services.Replace(ServiceDescriptor.Singleton<IRoutingKeyResolver>(routingKeyResolver));

        if (exchangeResolver is not null)
            services.Replace(ServiceDescriptor.Singleton<IExchangeResolver>(exchangeResolver));

        DecorateTransportAdapter(services);

        ServiceProvider provider = services.BuildServiceProvider();
        bool started = false;
        try
        {
            IBusControl busControl = provider.GetRequiredService<IBusControl>();
            var adapter = (ObservingTransportAdapter)provider.GetRequiredService<ITransportAdapter>();
            IRoutingKeyResolver resolver = provider.GetRequiredService<IRoutingKeyResolver>();

            await busControl.StartAsync(cancellationToken).ConfigureAwait(false);
            started = true;

            return new BareWireTestHarness(provider, adapter, busControl, resolver);
        }
        finally
        {
            if (!started)
                await provider.DisposeAsync().ConfigureAwait(false);
        }
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
        await _provider.DisposeAsync().ConfigureAwait(false);
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

    /// <summary>
    /// Replaces the <see cref="ITransportAdapter"/> descriptor registered by
    /// <c>AddBareWireWithInMemory</c> with one that wraps the same transport in an
    /// <see cref="ObservingTransportAdapter"/>.
    /// </summary>
    private static void DecorateTransportAdapter(ServiceCollection services)
    {
        ServiceDescriptor? original = null;
        for (int i = services.Count - 1; i >= 0; i--)
        {
            ServiceDescriptor candidate = services[i];
            if (candidate.ServiceType == typeof(ITransportAdapter) && !candidate.IsKeyedService)
            {
                original = candidate;
                services.RemoveAt(i);
                break;
            }
        }

        if (original is null)
        {
            throw new InvalidOperationException(
                "No ITransportAdapter descriptor was found to decorate — AddBareWireWithInMemory is " +
                "expected to have registered one before this method runs.");
        }

        services.AddSingleton<ITransportAdapter>(sp => new ObservingTransportAdapter(CreateInner(original, sp)));
    }

    /// <summary>
    /// Builds the inner transport instance described by <paramref name="descriptor"/>. Only a
    /// factory or an implementation-type descriptor is supported — the bundle registers the
    /// in-memory transport via a factory, and an already-constructed instance descriptor cannot be
    /// safely decorated: this decorator would then own and dispose an instance it did not create.
    /// </summary>
    private static ITransportAdapter CreateInner(ServiceDescriptor descriptor, IServiceProvider provider)
    {
        if (descriptor.ImplementationFactory is not null)
            return (ITransportAdapter)descriptor.ImplementationFactory(provider);

        if (descriptor.ImplementationType is not null)
            return (ITransportAdapter)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType);

        throw new InvalidOperationException(
            "The registered ITransportAdapter descriptor must supply a factory or an implementation " +
            "type. An instance descriptor is not supported here.");
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
