using System.Collections.Concurrent;
using System.Globalization;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.InMemory;

/// <summary>
/// In-memory implementation of <see cref="IRequestClient{TRequest}"/>. Registers a private reply address with
/// the <see cref="InMemoryReplyRegistry"/>, sends each request through the transport adapter with that address
/// as <c>ReplyTo</c>, and correlates the reply by <c>correlation-id</c>.
/// </summary>
/// <remarks>
/// <para>
/// Pending requests are bounded by <see cref="DefaultMaxPendingRequests"/>; beyond that
/// <see cref="GetResponseAsync{TResponse}"/> throws <see cref="BareWireTransportException"/>.
/// </para>
/// <para>
/// Neither logs nor exception messages contain correlation values, the reply address or message bodies.
/// </para>
/// </remarks>
internal sealed partial class InMemoryRequestClient<TRequest> : IRequestClient<TRequest>, IInMemoryReplySink
    where TRequest : class
{
    internal const int DefaultMaxPendingRequests = 1000;

    private const string TransportName = "InMemory";
    private const string ReplyToHeader = "ReplyTo";
    private const string CorrelationIdHeader = "correlation-id";

    private readonly ITransportAdapter _adapter;
    private readonly InMemoryReplyRegistry _replies;
    private readonly IMessageSerializer _serializer;
    private readonly IDeserializerResolver _deserializerResolver;
    private readonly ILogger _logger;
    private readonly string _targetExchange;
    private readonly string _routingKey;
    private readonly TimeSpan _timeout;
    private readonly int _maxPending;

    // ADR-004: bounded — limits concurrent in-flight requests. Never disposed (see DisposeAsync).
    private readonly SemaphoreSlim _pendingGate;
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<InboundMessage>> _pending = new();

    private int _disposed;

    internal InMemoryRequestClient(
        ITransportAdapter adapter,
        InMemoryReplyRegistry replies,
        IMessageSerializer serializer,
        IDeserializerResolver deserializerResolver,
        ILogger logger,
        string targetExchange,
        string routingKey,
        TimeSpan timeout,
        int maxPendingRequests = DefaultMaxPendingRequests)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(replies);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(deserializerResolver);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(targetExchange);
        ArgumentNullException.ThrowIfNull(routingKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPendingRequests);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        _adapter = adapter;
        _replies = replies;
        _serializer = serializer;
        _deserializerResolver = deserializerResolver;
        _logger = logger;
        _targetExchange = targetExchange;
        _routingKey = routingKey;
        _timeout = timeout;
        _maxPending = maxPendingRequests;
        _pendingGate = new SemaphoreSlim(maxPendingRequests, maxPendingRequests);

        ReplyAddress = replies.Register(this);
    }

    /// <summary>Gets the reply address registered for this client.</summary>
    internal string ReplyAddress { get; }

    /// <summary>Gets the number of requests currently waiting for a response (test hook).</summary>
    internal int PendingCount => _pending.Count;

    /// <inheritdoc />
    public async Task<Response<TResponse>> GetResponseAsync<TResponse>(
        TRequest request,
        CancellationToken cancellationToken = default)
        where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        bool acquired = await _pendingGate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        if (!acquired)
        {
            throw new BareWireTransportException(
                message: $"Request limit exceeded. No more than {_maxPending} pending requests are allowed at a " +
                         "time. Consider reducing the request rate or awaiting outstanding requests.",
                transportName: TransportName,
                endpointAddress: null);
        }

        Guid requestId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<InboundMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;
        bool consumed = false;

        try
        {
            // DisposeAsync may have swept _pending just before the entry above was inserted; without this
            // re-check the request would wait out the full timeout for a reply that can never arrive.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            OutboundMessage outbound = BuildRequest(request, requestId);

            IReadOnlyList<SendResult> results = await _adapter
                .SendBatchAsync([outbound], cancellationToken)
                .ConfigureAwait(false);

            if (results.Count != 1 || !results[0].IsConfirmed)
            {
                LogRequestNotAccepted(typeof(TRequest).Name);
                throw new BareWireTransportException(
                    message: $"The in-memory transport did not accept the request of type '{typeof(TRequest).Name}'. " +
                             "Check that the target exchange is declared, that the routing key is bound to a queue " +
                             "(with GuaranteedRouting an unroutable request is rejected), and that the bus is running.",
                    transportName: TransportName,
                    endpointAddress: null);
            }

            InboundMessage responseMessage;
            try
            {
                responseMessage = await tcs.Task.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                LogRequestTimedOut(typeof(TRequest).Name);
                throw new RequestTimeoutException(
                    requestType: typeof(TRequest),
                    timeout: _timeout,
                    destinationAddress: null,
                    transportName: TransportName);
            }

            consumed = true;
            using (responseMessage)
            {
                TResponse deserialized = DeserializeResponse<TResponse>(responseMessage);

                Guid messageId = responseMessage.Headers.TryGetValue(InMemoryHeaderNames.MessageId, out string? id)
                    && Guid.TryParse(id, out Guid parsed)
                    ? parsed
                    : Guid.NewGuid();

                return new Response<TResponse>(messageId, responseMessage.Headers, deserialized);
            }
        }
        finally
        {
            _pending.TryRemove(requestId, out _);

            // Race reply/timeout/cancel/dispose: whichever side loses must not leak the pooled reply buffer.
            tcs.TrySetCanceled(CancellationToken.None);
            if (!consumed && tcs.Task.IsCompletedSuccessfully)
            {
                tcs.Task.Result.Dispose();
            }

            _pendingGate.Release();
        }
    }

    /// <inheritdoc />
    bool IInMemoryReplySink.CanAccept(IReadOnlyDictionary<string, string> headers) =>
        TryGetPending(headers, out _);

    /// <inheritdoc />
    bool IInMemoryReplySink.TryAccept(InboundMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return TryGetPending(response.Headers, out TaskCompletionSource<InboundMessage>? tcs)
            && tcs.TrySetResult(response);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        // Unregister first: a reply arriving from here on finds no address and is dropped by the registry.
        _replies.Unregister(ReplyAddress);

        foreach (KeyValuePair<Guid, TaskCompletionSource<InboundMessage>> entry in _pending)
        {
            entry.Value.TrySetCanceled();
        }

        _pending.Clear();

        // _pendingGate is intentionally NOT disposed: callers still waiting call Release() in their finally.
        return ValueTask.CompletedTask;
    }

    private bool TryGetPending(
        IReadOnlyDictionary<string, string> headers,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TaskCompletionSource<InboundMessage>? tcs)
    {
        tcs = null;
        return headers.TryGetValue(CorrelationIdHeader, out string? correlationId)
            && Guid.TryParse(correlationId, out Guid requestId)
            && _pending.TryGetValue(requestId, out tcs);
    }

    // The serialized request is copied out of the pooled writer (ToArray) and the writer is returned to the
    // pool right away, so the message owns its bytes: a decorating adapter (the test harness) may keep
    // OutboundMessage.Body after SendBatchAsync returns, and no pooled buffer is held while waiting for the
    // reply. This is the one per-request allocation, on the cold request/response path.
    private OutboundMessage BuildRequest(TRequest request, Guid requestId)
    {
        byte[] body;
        using (var writer = new InMemoryPooledWriter())
        {
            _serializer.Serialize(request, writer);
            body = writer.WrittenMemory.ToArray();
        }

        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [InMemoryHeaderNames.Exchange] = _targetExchange,
            [ReplyToHeader] = ReplyAddress,
            [CorrelationIdHeader] = requestId.ToString("D", CultureInfo.InvariantCulture),

            // A separate Guid: the correlation value must not travel into the consumer's message-id (and logs).
            [InMemoryHeaderNames.MessageId] = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
        };

        return new OutboundMessage(_routingKey, headers, body, _serializer.ContentType);
    }

    private TResponse DeserializeResponse<TResponse>(InboundMessage responseMessage)
        where TResponse : class
    {
        responseMessage.Headers.TryGetValue(InMemoryHeaderNames.ContentType, out string? contentType);
        IMessageDeserializer deserializer = _deserializerResolver.Resolve(contentType);

        TResponse? deserialized = deserializer.Deserialize<TResponse>(responseMessage.Body);
        return deserialized ?? throw new BareWireTransportException(
            message: $"Failed to deserialize the response of type '{typeof(TResponse).Name}' " +
                     $"for the request of type '{typeof(TRequest).Name}'.",
            transportName: TransportName,
            endpointAddress: null);
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "The in-memory transport did not accept a request of type '{RequestType}'.")]
    private partial void LogRequestNotAccepted(string requestType);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "A request of type '{RequestType}' timed out waiting for a reply.")]
    private partial void LogRequestTimedOut(string requestType);
}
