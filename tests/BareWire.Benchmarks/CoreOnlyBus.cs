using System.Buffers;
using BareWire.Abstractions;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.Benchmarks;

/// <summary>
/// Starts a Core-only <see cref="IBus"/> in a private dependency-injection container: the real
/// <c>AddBareWire</c> core engine wired directly to a <see cref="CoreOnlyTransportAdapter"/> sink, with
/// no transport engine underneath it. Used by the publish-side benchmarks to measure the core pipeline
/// (serialization boundary, outbound channel, flow control) without any broker or in-memory transport
/// cost mixed in.
/// </summary>
internal sealed class CoreOnlyBus : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IBusControl _busControl;

    private CoreOnlyBus(ServiceProvider provider, IBusControl busControl, CoreOnlyTransportAdapter adapter)
    {
        _provider = provider;
        _busControl = busControl;
        Adapter = adapter;
    }

    /// <summary>Gets the running Core-only bus.</summary>
    internal IBus Bus => _busControl;

    /// <summary>Gets the sink adapter this bus was started against.</summary>
    internal CoreOnlyTransportAdapter Adapter { get; }

    /// <summary>
    /// Builds a private container wiring the real core engine (<c>AddBareWire</c>) to a fresh
    /// <see cref="CoreOnlyTransportAdapter"/> and a <see cref="FixedPayloadMessageSerializer"/>, and
    /// starts the resulting bus.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the startup sequence.</param>
    /// <returns>A started <see cref="CoreOnlyBus"/> ready for publish benchmarks.</returns>
    internal static async Task<CoreOnlyBus> StartAsync(CancellationToken cancellationToken = default)
    {
        var adapter = new CoreOnlyTransportAdapter();

        ServiceCollection services = new();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.TryAddSingleton<IMessageSerializer>(new FixedPayloadMessageSerializer());
        services.TryAddSingleton<IMessageDeserializer>(new NoOpMessageDeserializer());
        services.AddSingleton<ITransportAdapter>(adapter);
        services.AddBareWire(_ => { });

        ServiceProvider provider = services.BuildServiceProvider();
        bool started = false;
        try
        {
            IBusControl busControl = provider.GetRequiredService<IBusControl>();
            await busControl.StartAsync(cancellationToken).ConfigureAwait(false);
            started = true;

            return new CoreOnlyBus(provider, busControl, adapter);
        }
        finally
        {
            if (!started)
            {
                await provider.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _busControl.StopAsync().ConfigureAwait(false);
        await _provider.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Serializes any message to a fixed, pre-built ~56 B JSON payload written once at construction and
/// reused for every call — no per-message allocation. Shared by <see cref="CoreOnlyBus"/> and the
/// in-memory transport benchmarks so both baselines carry an identical, representative payload instead
/// of an empty body.
/// </summary>
internal sealed class FixedPayloadMessageSerializer : IMessageSerializer
{
    /// <summary>The pre-built payload bytes every <see cref="Serialize{T}"/> call writes verbatim.</summary>
    internal static readonly ReadOnlyMemory<byte> PayloadBytes =
        System.Text.Encoding.UTF8.GetBytes("""{"Id":"order-bench-001","Amount":99.99,"Currency":"USD"}""");

    /// <inheritdoc />
    public string ContentType => "application/json";

    /// <inheritdoc />
    public void Serialize<T>(T message, IBufferWriter<byte> output) where T : class
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Write(PayloadBytes.Span);
    }
}

/// <summary>
/// A no-op deserializer: the Core-only and single-binding in-memory benchmarks never deserialize —
/// consumption is measured at the raw <see cref="InboundMessage"/> level.
/// </summary>
internal sealed class NoOpMessageDeserializer : IMessageDeserializer
{
    /// <inheritdoc />
    public string ContentType => "application/octet-stream";

    /// <inheritdoc />
    public T? Deserialize<T>(ReadOnlySequence<byte> data) where T : class => null;
}
