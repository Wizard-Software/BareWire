namespace BareWire.Abstractions;

/// <summary>
/// Creates <see cref="IRequestClient{T}"/> instances for request/response messaging.
/// Transport adapters that support temporary response queues implement this interface
/// and register it with the dependency injection container so that
/// <see cref="IBus.CreateRequestClientAsync{T}(CancellationToken)"/> can delegate to a
/// transport-specific client implementation.
/// </summary>
/// <remarks>
/// Implementors are expected to be registered as singletons. Each call to
/// <see cref="CreateRequestClientAsync{T}(CancellationToken)"/> may perform asynchronous
/// initialization (e.g. declaring a response queue) and therefore returns an
/// already-initialized client. <see cref="System.Threading.Tasks.ValueTask{TResult}"/> is
/// used so that callers with a cached connection incur no heap allocation on the fast path.
/// </remarks>
public interface IRequestClientFactory
{
    /// <summary>
    /// Creates and initializes a transport-specific <see cref="IRequestClient{T}"/>
    /// for the given request type <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">
    /// The request message type. Must be a reference type (typically a <c>record</c>).
    /// </typeparam>
    /// <param name="cancellationToken">
    /// A token to cancel connection establishment or response-queue declaration.
    /// </param>
    /// <returns>
    /// A <see cref="System.Threading.Tasks.ValueTask{TResult}"/> that resolves to an initialized
    /// <see cref="IRequestClient{T}"/> ready to send requests and receive responses.
    /// </returns>
    ValueTask<IRequestClient<T>> CreateRequestClientAsync<T>(
        CancellationToken cancellationToken = default) where T : class;
}
