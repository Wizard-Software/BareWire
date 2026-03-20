using BareWire.Abstractions.Configuration;

namespace BareWire.Abstractions;

/// <summary>
/// The central messaging bus that combines publish, send, and receive endpoint capabilities.
/// Exposes the full API surface available to application code during normal operation.
/// Obtain a running instance via <see cref="IBusControl.StartAsync"/>.
/// Implementations are thread-safe.
/// </summary>
public interface IBus : IPublishEndpoint, ISendEndpointProvider, IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Gets the unique identifier of this bus instance.
    /// Stable for the lifetime of the bus; changes across restarts.
    /// </summary>
    Guid BusId { get; }

    /// <summary>
    /// Gets the primary transport address of this bus instance
    /// (e.g. <c>rabbitmq://localhost/my-service</c>).
    /// </summary>
    Uri Address { get; }

    /// <summary>
    /// Creates a request client bound to <typeparamref name="T"/> for asynchronous request/response messaging.
    /// The returned client uses the bus's default timeout configuration.
    /// </summary>
    /// <typeparam name="T">
    /// The request message type. Must be a reference type (typically a <c>record</c>).
    /// </typeparam>
    /// <param name="cancellationToken">
    /// A token to cancel connection establishment or response-queue declaration.
    /// </param>
    /// <returns>
    /// A <see cref="ValueTask{TResult}"/> that resolves to a new <see cref="IRequestClient{T}"/>
    /// scoped to this bus instance.
    /// </returns>
    ValueTask<IRequestClient<T>> CreateRequestClientAsync<T>(
        CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Dynamically connects a receive endpoint to the bus at runtime without restarting.
    /// Useful for temporary subscriptions or dynamic consumer registration.
    /// </summary>
    /// <param name="queueName">The transport queue name to bind the endpoint to.</param>
    /// <param name="configure">
    /// A delegate that configures the receive endpoint, including consumer registrations
    /// and retry policies.
    /// </param>
    /// <returns>
    /// An <see cref="IDisposable"/> that, when disposed, disconnects and removes the endpoint.
    /// </returns>
    IDisposable ConnectReceiveEndpoint(string queueName, Action<IReceiveEndpointConfigurator> configure);
}
