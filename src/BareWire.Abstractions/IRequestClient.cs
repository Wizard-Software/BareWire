namespace BareWire.Abstractions;

/// <summary>
/// Provides request/response messaging for a specific request type <typeparamref name="TRequest"/>.
/// Sends a request message and waits for a correlated response, applying a configurable timeout.
/// Obtain instances via <see cref="IBus.CreateRequestClientAsync{T}(CancellationToken)"/>.
/// </summary>
/// <typeparam name="TRequest">
/// The request message type. Must be a reference type (typically a <c>record</c>).
/// </typeparam>
public interface IRequestClient<TRequest> where TRequest : class
{
    /// <summary>
    /// Sends <paramref name="request"/> and asynchronously waits for a correlated response
    /// of type <typeparamref name="TResponse"/>.
    /// </summary>
    /// <typeparam name="TResponse">
    /// The expected response message type. Must be a reference type (typically a <c>record</c>).
    /// </typeparam>
    /// <param name="request">The request message to send. Must not be null.</param>
    /// <param name="cancellationToken">
    /// A token to cancel waiting for the response. Also used to enforce a timeout
    /// by wrapping with <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>.
    /// </param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> that resolves to a <see cref="Response{TResponse}"/>
    /// containing the response message and its metadata.
    /// </returns>
    /// <exception cref="System.TimeoutException">
    /// Throws <c>RequestTimeoutException</c> when no response is received within the configured timeout period.
    /// </exception>
    Task<Response<TResponse>> GetResponseAsync<TResponse>(
        TRequest request,
        CancellationToken cancellationToken = default)
        where TResponse : class;
}
