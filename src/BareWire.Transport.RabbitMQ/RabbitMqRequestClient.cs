using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ.Internal;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace BareWire.Transport.RabbitMQ;

/// <summary>
/// RabbitMQ implementation of <see cref="IRequestClient{TRequest}"/>.
/// Creates an exclusive auto-delete response queue per client instance and correlates responses
/// back to callers via <c>CorrelationId</c> (primary) or envelope <c>requestId</c> (fallback).
/// </summary>
/// <remarks>
/// ADR-004: pending requests are bounded by <see cref="DefaultMaxPendingRequests"/>. When the limit
/// is reached <see cref="GetResponseAsync{TResponse}"/> throws <see cref="BareWireTransportException"/>.
/// </remarks>
internal sealed partial class RabbitMqRequestClient<TRequest> : IRequestClient<TRequest>, IAsyncDisposable
    where TRequest : class
{
    private const int DefaultMaxPendingRequests = 1000;
    private const string TransportName = "RabbitMQ";
    private const string MassTransitContentType = "application/vnd.masstransit+json";

    private readonly IConnection _connection;
    private readonly IMessageSerializer _serializer;
    private readonly IDeserializerResolver _deserializerResolver;
    private readonly ILogger _logger;
    private readonly TimeSpan _timeout;
    private readonly string _targetExchange;
    private readonly string _routingKey;
    private readonly RabbitMqHeaderMapper _headerMapper;
    private readonly Uri _connectionUri;
    private readonly string? _vhost;
    private readonly bool _strict;

    // ADR-004: bounded — limits concurrent in-flight requests.
    private readonly SemaphoreSlim _pendingGate;

    // Keyed by Guid requestId for clean Guid-based correlation (avoids string allocation per lookup).
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<InboundMessage>> _pending = new();

    private IChannel? _responseChannel;
    private string? _responseQueueName;
    private bool _initialized;
    private bool _disposed;

    // D4/PERF-1: computed once per client instance in InitializeAsync — timeout is constant
    // per client, so _expirationMillis never allocates on the hot request path.
    private string? _expirationMillis;

    // Addresses computed once from the connection data + server-assigned queue name.
    // Null until InitializeAsync completes.
    private string? _responseAddress;
    private string? _destinationAddress;

    internal RabbitMqRequestClient(
        IConnection connection,
        IMessageSerializer serializer,
        IDeserializerResolver deserializerResolver,
        ILogger logger,
        string targetExchange,
        string routingKey,
        TimeSpan timeout,
        Uri connectionUri,
        string? vhost,
        bool strict = false,
        int maxPendingRequests = DefaultMaxPendingRequests,
        RabbitMqHeaderMapper? headerMapper = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(deserializerResolver);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(targetExchange);
        ArgumentNullException.ThrowIfNull(routingKey);
        ArgumentNullException.ThrowIfNull(connectionUri);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPendingRequests);

        _connection = connection;
        _serializer = serializer;
        _deserializerResolver = deserializerResolver;
        _logger = logger;
        _targetExchange = targetExchange;
        _routingKey = routingKey;
        _timeout = timeout;
        _connectionUri = connectionUri;
        _vhost = vhost;
        _strict = strict;
        _pendingGate = new SemaphoreSlim(maxPendingRequests, maxPendingRequests);
        _headerMapper = headerMapper ?? new RabbitMqHeaderMapper();
    }

    /// <summary>
    /// Initializes the dedicated response channel and declares the exclusive auto-delete queue.
    /// Must be called once before the first <see cref="GetResponseAsync{TResponse}"/>.
    /// </summary>
    internal async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_initialized)
        {
            return;
        }

        _responseChannel = await _connection
            .CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: false,
                    publisherConfirmationTrackingEnabled: false),
                cancellationToken)
            .ConfigureAwait(false);

        // Declare server-named exclusive auto-delete queue for responses.
        QueueDeclareOk queueOk = await _responseChannel.QueueDeclareAsync(
            queue: string.Empty,   // server assigns the name
            durable: false,
            exclusive: true,
            autoDelete: true,
            arguments: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        _responseQueueName = queueOk.QueueName;

        // D4/PERF-1: hoist AMQP expiration string — timeout is constant per client, so compute
        // once here instead of allocating a new string for every request in SerializeAndPublishAsync.
        _expirationMillis = ((long)_timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

        // Build endpoint addresses once from the connection data.
        // These are constant per client instance and are passed into every request envelope.
        //
        // MT interop: use the amq.rabbitmq.reply-to reply-to address rather than the server-named
        // queue address (rabbitmq://host/amq.gen-xxx?temporary=true).  When MT's ConsumeContext
        // sees a responseAddress ending with "amq.rabbitmq.reply-to" it routes the reply via the
        // default AMQP exchange using the AMQP ReplyTo property (= _responseQueueName, set below
        // in SerializeAndPublishAsync) as the routing key — which delivers the response directly
        // to our exclusive reply queue.  Without this, MT declares a fanout exchange named after
        // the server-assigned queue and silently drops the response (no binding).
        _responseAddress = RabbitMqEndpointAddress.BuildReplyToAddress(_connectionUri, _vhost);

        // Destination address: use the explicit exchange when set, else fall back to the routing key.
        // This is best-effort/diagnostic — real routing uses the targetExchange + routingKey AMQP fields.
        string destinationName = !string.IsNullOrEmpty(_targetExchange)
            ? _targetExchange
            : _routingKey;
        _destinationAddress = RabbitMqEndpointAddress.Build(
            _connectionUri, _vhost, destinationName, temporary: false);

        // Start consuming responses on the dedicated queue.
        var consumer = new AsyncEventingBasicConsumer(_responseChannel);
        consumer.ReceivedAsync += OnResponseReceivedAsync;

        await _responseChannel.BasicConsumeAsync(
            queue: _responseQueueName,
            autoAck: true,      // auto-ack responses — we do not need settlement on the reply queue
            consumerTag: string.Empty,
            noLocal: false,
            exclusive: false,
            arguments: null,
            consumer: consumer,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        _initialized = true;

        LogResponseQueueReady(_responseQueueName);
    }

    /// <inheritdoc/>
    public async Task<Response<TResponse>> GetResponseAsync<TResponse>(
        TRequest request,
        CancellationToken cancellationToken = default)
        where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_initialized)
        {
            throw new InvalidOperationException(
                $"{nameof(RabbitMqRequestClient<TRequest>)} must be initialized via " +
                $"{nameof(InitializeAsync)} before calling {nameof(GetResponseAsync)}.");
        }

        // ADR-004: acquire a slot — throws if the pending request limit has been reached.
        bool acquired = await _pendingGate.WaitAsync(TimeSpan.Zero, cancellationToken)
            .ConfigureAwait(false);

        if (!acquired)
        {
            throw new BareWireTransportException(
                message: $"Request limit exceeded. No more than {_pendingGate.CurrentCount} " +
                         "pending requests are allowed at a time. " +
                         "Consider increasing MaxPendingRequests or reducing request rate.",
                transportName: TransportName,
                endpointAddress: null);
        }

        // Use a single Guid as requestId — the same value keys _pending, goes into the envelope
        // RequestId field, and is set as the AMQP CorrelationId for BareWire↔BareWire correlation.
        Guid requestId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<InboundMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _pending[requestId] = tcs;

        try
        {
            // Create a short-lived publish channel with publisher confirms.
            IChannel publishChannel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true),
                cancellationToken).ConfigureAwait(false);

            try
            {
                // ADR-003: serialize and publish in a single scope — pooled buffer stays alive
                // through the await, then returns to ArrayPool. Zero heap allocation.
                await SerializeAndPublishAsync(request, publishChannel, requestId, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await publishChannel.CloseAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogPublishChannelCloseError(ex);
                }

                await publishChannel.DisposeAsync().ConfigureAwait(false);
            }

            // Await response with combined timeout + caller cancellation.
            using var timeoutCts = new CancellationTokenSource(_timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);

            InboundMessage responseMessage;
            try
            {
                responseMessage = await tcs.Task
                    .WaitAsync(linkedCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new RequestTimeoutException(
                    requestType: typeof(TRequest),
                    timeout: _timeout,
                    destinationAddress: null,
                    transportName: TransportName);
            }

            // Deserialize the response body using the deserializer that matches the response's
            // content-type header — honouring content-type-routed deserializers such as the
            // MassTransit envelope deserializer (issue #13), mirroring the consume path.
            TResponse deserialized = DeserializeResponse<TResponse>(responseMessage, requestId.ToString());

            bool hasMessageId = responseMessage.Headers.TryGetValue("message-id", out string? msgIdStr);
            Guid messageId = hasMessageId && Guid.TryParse(msgIdStr, out Guid parsed)
                ? parsed
                : Guid.NewGuid();

            return new Response<TResponse>(
                MessageId: messageId,
                Headers: responseMessage.Headers,
                Message: deserialized);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
            _pendingGate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Cancel all in-flight TCS so callers are not stuck waiting.
        foreach (KeyValuePair<Guid, TaskCompletionSource<InboundMessage>> entry in _pending)
        {
            entry.Value.TrySetCanceled();
        }

        _pending.Clear();

        if (_responseChannel is not null)
        {
            try
            {
                await _responseChannel.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogResponseChannelCloseError(ex);
            }

            await _responseChannel.DisposeAsync().ConfigureAwait(false);
            _responseChannel = null;
        }

        _pendingGate.Dispose();
    }

    /// <summary>
    /// Resolves the deserializer for the response's <c>content-type</c> header via the
    /// <see cref="IDeserializerResolver"/> and deserializes the body into <typeparamref name="TResponse"/>.
    /// Mirrors the consume path so request clients honour content-type-routed deserializers
    /// (e.g. the MassTransit envelope deserializer) rather than always using the default — issue #13.
    /// </summary>
    /// <typeparam name="TResponse">The expected response message type.</typeparam>
    /// <param name="responseMessage">The inbound response message.</param>
    /// <param name="correlationId">The correlation id, used for diagnostics on failure.</param>
    /// <returns>The deserialized response.</returns>
    /// <exception cref="BareWireTransportException">Thrown when the body cannot be deserialized.</exception>
    internal TResponse DeserializeResponse<TResponse>(InboundMessage responseMessage, string correlationId)
        where TResponse : class
    {
        responseMessage.Headers.TryGetValue("content-type", out string? contentType);
        IMessageDeserializer deserializer = _deserializerResolver.Resolve(contentType);

        TResponse? deserialized = deserializer.Deserialize<TResponse>(responseMessage.Body);

        if (deserialized is null)
        {
            throw new BareWireTransportException(
                message: $"Failed to deserialize response of type '{typeof(TResponse).Name}' " +
                         $"for correlationId '{correlationId}'.",
                transportName: TransportName,
                endpointAddress: null);
        }

        return deserialized;
    }

    /// <summary>
    /// Attempts to resolve a pending <see cref="TaskCompletionSource{T}"/> for an incoming response,
    /// implementing two-stage correlation:
    /// <list type="number">
    ///   <item>Primary: AMQP <c>CorrelationId</c> → parse to <see cref="Guid"/> → look up <c>_pending</c>.</item>
    ///   <item>
    ///     Fallback (only when primary fails and <paramref name="contentType"/> is
    ///     <c>application/vnd.masstransit+json</c>): resolve the deserializer; if it implements
    ///     <see cref="IResponseEnvelopeReader"/>, extract <c>requestId</c> from the envelope and verify
    ///     it exists in <c>_pending</c> — SEC-2: never fabricate an entry from the body.
    ///   </item>
    /// </list>
    /// Exposed as <c>internal</c> so unit tests can exercise correlation logic without a real broker.
    /// </summary>
    /// <param name="amqpCorrelationId">The AMQP <c>CorrelationId</c> property, or <see langword="null"/>.</param>
    /// <param name="contentType">The <c>content-type</c> header of the response, or <see langword="null"/>.</param>
    /// <param name="body">The raw response body (zero-copy).</param>
    /// <param name="tcs">
    /// When this method returns <see langword="true"/>, contains the matched pending TCS.
    /// Otherwise <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if a pending TCS was found and should be completed; otherwise
    /// <see langword="false"/> (caller should discard the message).
    /// </returns>
    internal bool TryResolvePending(
        string? amqpCorrelationId,
        string? contentType,
        ReadOnlySequence<byte> body,
        out TaskCompletionSource<InboundMessage>? tcs)
    {
        // Stage 1 — Primary: AMQP CorrelationId (BareWire↔BareWire and any transport that echoes it).
        if (!string.IsNullOrEmpty(amqpCorrelationId)
            && Guid.TryParse(amqpCorrelationId, out Guid amqpGuid)
            && _pending.TryGetValue(amqpGuid, out tcs))
        {
            return true;
        }

        // Stage 2 — Fallback: envelope requestId (MassTransit response path).
        // Only triggered when: AMQP correlation is absent/unknown AND content-type is MT JSON.
        if (string.Equals(contentType, MassTransitContentType, StringComparison.OrdinalIgnoreCase))
        {
            IMessageDeserializer deserializer = _deserializerResolver.Resolve(contentType);

            if (deserializer is IResponseEnvelopeReader envelopeReader
                && envelopeReader.TryReadRequestId(body, out Guid envelopeRequestId))
            {
                // SEC-2: only look up — never create — an entry from the body-supplied requestId.
                if (_pending.TryGetValue(envelopeRequestId, out tcs))
                {
                    return true;
                }

                // Body supplied a requestId that we never registered — discard.
                LogUnknownCorrelationId(envelopeRequestId.ToString());
                tcs = null;
                return false;
            }
        }

        tcs = null;
        return false;
    }

    /// <summary>
    /// Seeds a pending entry directly into <c>_pending</c> for unit tests that need to exercise
    /// correlation logic without a running broker or a full <see cref="GetResponseAsync{TResponse}"/>
    /// call. Must only be called from test code (internal visibility via <c>InternalsVisibleTo</c>).
    /// </summary>
    internal void SeedPendingForTest(Guid requestId, TaskCompletionSource<InboundMessage> tcs)
        => _pending[requestId] = tcs;

    private Task OnResponseReceivedAsync(object sender, BasicDeliverEventArgs args)
    {
        // CRITICAL: copy body bytes before any async resolution — RabbitMQ.Client frees the memory
        // after the handler returns and we may need the bytes in TryResolvePending's fallback path.
        byte[] bodyCopy = args.Body.ToArray();
        ReadOnlySequence<byte> bodySequence = bodyCopy.Length == 0
            ? ReadOnlySequence<byte>.Empty
            : new ReadOnlySequence<byte>(bodyCopy);

        string? amqpCorrelationId = args.BasicProperties.CorrelationId;

        Dictionary<string, string> headers = _headerMapper.MapInbound(args.BasicProperties);
        headers.TryGetValue("content-type", out string? contentType);

        if (!TryResolvePending(amqpCorrelationId, contentType, bodySequence, out TaskCompletionSource<InboundMessage>? tcs))
        {
            // TryResolvePending already logs specifics for the fallback discard path.
            // Log the primary-path failures here (missing / unknown AMQP correlation without MT fallback).
            if (string.IsNullOrEmpty(amqpCorrelationId)
                && !string.Equals(contentType, MassTransitContentType, StringComparison.OrdinalIgnoreCase))
            {
                LogMissingCorrelationId();
            }

            return Task.CompletedTask;
        }

        string messageId = headers.TryGetValue("message-id", out string? mappedId) && !string.IsNullOrEmpty(mappedId)
            ? mappedId
            : Guid.NewGuid().ToString();

        var inbound = new InboundMessage(
            messageId: messageId,
            headers: headers,
            body: bodySequence,
            deliveryTag: args.DeliveryTag);

        tcs!.TrySetResult(inbound);

        return Task.CompletedTask;
    }

    // ADR-003: serialize into a pooled buffer and publish in a single scope so the rented buffer
    // stays alive through the await. RabbitMQ.Client 7.x copies body bytes synchronously into its
    // own frame buffer (Framing.SerializeToFrames) before the async I/O — so after BasicPublishAsync
    // completes, the rented buffer is safe to return to the pool. Zero heap allocation per request.
    private async ValueTask SerializeAndPublishAsync(
        TRequest request,
        IChannel publishChannel,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        byte[] rentedBuffer;
        int length;

        using (var writer = new SimplePooledWriter(initialCapacity: 4096))
        {
            // Build per-request envelope context. Addresses and _expirationMillis are already cached
            // in InitializeAsync; only ExpirationTime varies per request (absolute deadline).
            if (_serializer is IRequestEnvelopeSerializer envSerializer)
            {
                var ctx = new RequestEnvelopeContext(
                    ResponseAddress: _responseAddress,
                    DestinationAddress: _destinationAddress,
                    FaultAddress: _responseAddress,  // faults come back to the response queue
                    RequestId: requestId,
                    CorrelationId: requestId,         // same Guid for both — BareWire↔BareWire echoes CorrelationId
                    ExpirationTime: DateTimeOffset.UtcNow + _timeout);

                envSerializer.Serialize(request, in ctx, writer);
            }
            else
            {
                _serializer.Serialize(request, writer);
            }

            (rentedBuffer, length) = writer.DetachBuffer();
        }

        try
        {
            var props = new BasicProperties
            {
                // AMQP CorrelationId set to requestId.ToString() so BareWire↔BareWire
                // responders can echo it back for the primary correlation path.
                CorrelationId = requestId.ToString(),
                ReplyTo = _responseQueueName,
                ContentType = _serializer.ContentType,
                // D4/PERF-1: _expirationMillis computed once in InitializeAsync — no alloc per request.
                Expiration = _expirationMillis,
            };

            await publishChannel.BasicPublishAsync<BasicProperties>(
                exchange: _targetExchange,
                routingKey: _routingKey,
                mandatory: _strict,
                basicProperties: props,
                body: new ReadOnlyMemory<byte>(rentedBuffer, 0, length),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (PublishException ex) when (ex.IsReturn)
        {
            // Strict opt-in (14.10, ADR-027 D3): broker returned the message — no responder queue
            // is bound to the fanout exchange. SEC S1: message contains only the exchange name,
            // never correlation-id, body, or headers.
            throw new BareWireTransportException(
                message: $"Publish-style request returned: no responder is bound to exchange '{_targetExchange}'.",
                transportName: TransportName,
                endpointAddress: null,
                innerException: ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "RabbitMQ request client response queue ready: '{QueueName}'.")]
    private partial void LogResponseQueueReady(string queueName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Received response without CorrelationId on request client response queue. Message discarded.")]
    private partial void LogMissingCorrelationId();

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Received response with unknown CorrelationId '{CorrelationId}'. Message discarded (may have timed out).")]
    private partial void LogUnknownCorrelationId(string correlationId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Exception while closing request client publish channel.")]
    private partial void LogPublishChannelCloseError(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Exception while closing request client response channel during dispose.")]
    private partial void LogResponseChannelCloseError(Exception ex);

    /// <summary>
    /// Minimal ADR-003-compliant <see cref="IBufferWriter{T}"/> backed by <see cref="ArrayPool{T}.Shared"/>.
    /// Scoped to serialization of a single request message.
    /// </summary>
    private sealed class SimplePooledWriter : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer;
        private int _position;

        internal SimplePooledWriter(int initialCapacity)
        {
            _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        }

        internal ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _position);

        public void Advance(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

            if (count > _buffer.Length - _position)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "Cannot advance past end of buffer.");
            }

            _position += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_position);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_position);
        }

        /// <summary>
        /// Detaches the rented buffer, transferring ownership to the caller.
        /// After this call, <see cref="Dispose"/> will not return the buffer to the pool.
        /// The caller must return it via <see cref="ArrayPool{T}.Shared"/>.
        /// </summary>
        internal (byte[] Buffer, int Length) DetachBuffer()
        {
            byte[] buf = _buffer;
            int len = _position;
            _buffer = null!;
            _position = 0;
            return (buf, len);
        }

        public void Dispose()
        {
            if (_buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = null!;
            }
        }

        private void EnsureCapacity(int sizeHint)
        {
            int needed = sizeHint <= 0 ? 1 : sizeHint;
            int available = _buffer.Length - _position;

            if (available >= needed)
            {
                return;
            }

            int minimumSize = Math.Max(_buffer.Length * 2, _position + needed);
            byte[] newBuffer = ArrayPool<byte>.Shared.Rent(minimumSize);
            Buffer.BlockCopy(_buffer, 0, newBuffer, 0, _position);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = newBuffer;
        }
    }
}
