using System.Diagnostics.Metrics;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Transport;
using BareWire.InMemory;
using BareWire.Serialization.Json;
using BareWire.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BareWire.IntegrationTests.Transport.InMemoryBus;

/// <summary>
/// Groups every InMemoryBus-level P0 test class into one xUnit collection with
/// parallelization disabled — several scenarios measure elapsed time, rejection counts, or latch
/// state, which would be skewed by another test in the same collection running concurrently.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InMemoryBusIsolation
{
    /// <summary>The collection name every InMemoryBus test class opts into via <c>[Collection(Name)]</c>.</summary>
    public const string Name = "InMemoryBus";
}

/// <summary>
/// Hosts a real <c>BareWireBus</c> on the in-memory transport inside a private, per-test
/// <see cref="ServiceCollection"/> — no broker, no shared or static state. Construct via
/// <see cref="StartAsync"/> and dispose via <c>await using</c>.
/// </summary>
/// <remarks>
/// Registers the bus with the single-call <c>AddBareWireWithInMemory</c> bundle — the same entry point
/// applications use — plus <see cref="Abstractions.Serialization.IMessageSerializer"/> via
/// <c>AddBareWireJsonSerializer()</c>, because neither the bundle nor <c>AddBareWire</c> registers a
/// serializer on its own.
/// </remarks>
internal sealed class InMemoryBusHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private bool _started;
    private bool _disposed;

    private InMemoryBusHost(
        ServiceProvider provider, IBusControl busControl, InMemoryTransportAdapter adapter, InMemoryBusTelemetry telemetry)
    {
        _provider = provider;
        Bus = busControl;
        BusControl = busControl;
        Adapter = adapter;
        Telemetry = telemetry;
    }

    /// <summary>Gets the service provider backing this host — resolve additional per-test services from it.</summary>
    public IServiceProvider Services => _provider;

    /// <summary>Gets the bus's publish/send surface. The same instance as <see cref="BusControl"/>.</summary>
    public IBus Bus { get; }

    /// <summary>Gets the bus's lifecycle-control surface.</summary>
    public IBusControl BusControl { get; }

    /// <summary>Gets the in-memory transport adapter resolved from this host's container.</summary>
    public InMemoryTransportAdapter Adapter { get; }

    /// <summary>Gets the captured logs and "BareWire" meter measurements for this host.</summary>
    public InMemoryBusTelemetry Telemetry { get; }

    /// <summary>
    /// Builds a private container, registers the in-memory transport and the core bus, starts the
    /// bus, and returns the running host.
    /// </summary>
    /// <param name="transport">Configures the in-memory transport. Must not be <see langword="null"/>.</param>
    /// <param name="bus">Optional core bus configuration. When omitted, the core is registered with defaults.</param>
    /// <param name="services">
    /// Optional callback to register additional per-test services (probes, saga repositories, saga
    /// state machines) before the container is built.
    /// </param>
    /// <param name="minimumLogLevel">
    /// The minimum level captured by <see cref="InMemoryBusTelemetry"/>. Defaults to
    /// <see cref="LogLevel.Trace"/>; throughput-sensitive scenarios should raise this to
    /// <see cref="LogLevel.Information"/> so per-message hot-path logging (none exists today, but a
    /// future regression would) cannot skew a timing measurement.
    /// </param>
    /// <param name="cancellationToken">
    /// A long-lived token passed to <c>IBusControl.StartAsync</c>. The bus's consume loops keep
    /// running for as long as this token stays uncancelled — pass a per-test token (e.g.
    /// <c>TestContext.Current.CancellationToken</c>), never a short-lived one bound to just the
    /// startup call, or consumers would be cancelled mid-test.
    /// </param>
    public static async Task<InMemoryBusHost> StartAsync(
        Action<IInMemoryConfigurator> transport,
        Action<IBusConfigurator>? bus = null,
        Action<IServiceCollection>? services = null,
        LogLevel minimumLogLevel = LogLevel.Trace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);

        var telemetry = new InMemoryBusTelemetry();
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging(b => b.AddProvider(telemetry).SetMinimumLevel(minimumLogLevel));
        serviceCollection.AddMetrics();
        serviceCollection.AddBareWireJsonSerializer();

        serviceCollection.AddBareWireWithInMemory(transport, bus);

        services?.Invoke(serviceCollection);

        ServiceProvider provider = serviceCollection.BuildServiceProvider();

        try
        {
            // The listener must be observing BEFORE the adapter (and its instruments) are created by
            // IBusControl.StartAsync below — a MeterListener started after Counter.Add() calls have
            // already happened never sees those earlier measurements.
            telemetry.StartListening(provider.GetRequiredService<IMeterFactory>());

            var busControl = provider.GetRequiredService<IBusControl>();
            var adapter = (InMemoryTransportAdapter)provider.GetRequiredService<ITransportAdapter>();
            var host = new InMemoryBusHost(provider, busControl, adapter, telemetry);

            await busControl.StartAsync(cancellationToken).ConfigureAwait(false);
            host._started = true;
            return host;
        }
        catch
        {
            // Clean up on start failure — nobody else owns this provider/telemetry pair yet.
            telemetry.Dispose();
            await provider.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Stops the bus. A no-op when the bus was never started or is already stopped.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started)
        {
            return;
        }

        await BusControl.StopAsync(cancellationToken).ConfigureAwait(false);
        _started = false;
    }

    /// <summary>Stops the bus (if still running, bounded to 30 seconds) and disposes the container and telemetry.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_started)
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await BusControl.StopAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Best-effort shutdown bound for test teardown — a hung StopAsync must not hang the
                // whole test run. The scenario under test is responsible for asserting StopAsync's
                // own timing; this bound only protects teardown of unrelated tests.
            }
            catch (TimeoutException)
            {
                // Same best-effort bound as above, for adapters that surface it as TimeoutException
                // rather than OperationCanceledException.
            }
        }

        await _provider.DisposeAsync().ConfigureAwait(false);
        Telemetry.Dispose();
    }
}
