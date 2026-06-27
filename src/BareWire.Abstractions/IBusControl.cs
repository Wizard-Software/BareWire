namespace BareWire.Abstractions;

/// <summary>
/// Extends <see cref="IBus"/> with lifecycle control and topology deployment capabilities.
/// Used during application startup and shutdown to manage the bus process.
/// Obtained via the DI container as a singleton — call <see cref="StartAsync"/> in a hosted service.
/// </summary>
public interface IBusControl : IBus
{
    /// <summary>
    /// Starts the bus, establishes transport connections, and begins consuming messages
    /// on all configured receive endpoints.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the startup sequence.</param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> that resolves to a <see cref="BusHandle"/>
    /// representing the running bus. Dispose the handle to initiate shutdown.
    /// </returns>
    Task<BusHandle> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the bus gracefully, waiting for in-flight messages to complete before
    /// closing transport connections.
    /// </summary>
    /// <param name="cancellationToken">A token to force an immediate (non-graceful) stop.</param>
    /// <returns>A <see cref="Task"/> that completes when the bus has fully stopped.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deploys the configured topology (exchanges, queues, and bindings) to the broker
    /// without starting the bus. Safe to call in CI/CD pipelines before application startup.
    /// Topology is never deployed automatically — this method must be called explicitly (manual topology).
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the deployment operation.</param>
    /// <returns>A <see cref="Task"/> that completes when all topology elements have been declared.</returns>
    /// <exception cref="System.Exception">
    /// Throws <c>TopologyDeploymentException</c> when the broker rejects or cannot apply
    /// one or more topology declarations.
    /// </exception>
    Task DeployTopologyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a snapshot of the current health status of the bus and all its receive endpoints.
    /// This is a synchronous, non-blocking operation suitable for health check polling.
    /// </summary>
    /// <returns>
    /// A <see cref="BusHealthStatus"/> describing the bus and each endpoint's current state.
    /// </returns>
    BusHealthStatus CheckHealth();
}
