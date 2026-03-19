using System.Buffers;
using System.Collections.Concurrent;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace BareWire.Transport.RabbitMQ;

/// <summary>
/// RabbitMQ implementation of <see cref="IRequestClient{TRequest}"/>.
/// Creates an exclusive auto-delete response queue per client instance and correlates responses
/// back to callers via <c>CorrelationId</c>.
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

    private readonly IConnection _connection;
    private readonly IMessageSerializer _serializer;
    private readonly IMessageDeserializer _deserializer;
    private readonly ILogger _logger;
    private readonly TimeSpan _timeout;
    private readonly string _targetExchange;
    private readonly string _routingKey;
    private readonly RabbitMqHeaderMapper _headerMapper;

    // ADR-004: bounded — limits concurrent in-flight requests.
    private readonly SemaphoreSlim _pendingGate;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<InboundMessage>> _pending
        = new(StringComparer.Ordinal);

    private IChannel? _responseChannel;
    private string? _responseQueueName;
    private bool _initialized;
    private bool _disposed;

    internal RabbitMqRequestClient(
        IConnection connection,
        IMessageSerializer serializer,
        IMessageDeserializer deserializer,
        ILogger logger,
        string targetExchange,
        string routingKey,
        TimeSpan timeout,
        int maxPendingRequests = DefaultMaxPendingRequests,
        RabbitMqHeaderMapper? headerMapper = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(deserializer);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(targetExchange);
        ArgumentNullException.ThrowIfNull(routingKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPendingRequests);

        _connection = connection;
        _serializer = serializer;
        _deserializer = deserializer;
        _logger = logger;
        _targetExchange = targetExchange;
        _routingKey = routingKey;
        _timeout = timeout;
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

        string correlationId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<InboundMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _pending[correlationId] = tcs;

        try
        {
            // Serialize the request using zero-copy ADR-003 pattern.
            ReadOnlyMemory<byte> requestBody = SerializeRequest(request);

            // Build AMQP properties: CorrelationId + ReplyTo.
            var props = new BasicProperties
            {
                CorrelationId = correlationId,
                ReplyTo = _responseQueueName,
                ContentType = _serializer.ContentType,
            };

            // Create a short-lived publish channel with publisher confirms.
            IChannel publishChannel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true),
                cancellationToken).ConfigureAwait(false);

            try
            {
                await publishChannel.BasicPublishAsync<BasicProperties>(
                    exchange: _targetExchange,
                    routingKey: _routingKey,
                    mandatory: false,
                    basicProperties: props,
                    body: requestBody,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
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

            // Deserialize the response body.
            TResponse? deserialized = _deserializer.Deserialize<TResponse>(responseMessage.Body);

            if (deserialized is null)
            {
                throw new BareWireTransportException(
                    message: $"Failed to deserialize response of type '{typeof(TResponse).Name}' " +
                             $"for correlationId '{correlationId}'.",
                    transportName: TransportName,
                    endpointAddress: null);
            }

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
            _pending.TryRemove(correlationId, out _);
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
        foreach (KeyValuePair<string, TaskCompletionSource<InboundMessage>> entry in _pending)
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

    private Task OnResponseReceivedAsync(object sender, BasicDeliverEventArgs args)
    {
        string? correlationId = args.BasicProperties.CorrelationId;

        if (string.IsNullOrEmpty(correlationId))
        {
            LogMissingCorrelationId();
            return Task.CompletedTask;
        }

        if (!_pending.TryGetValue(correlationId, out TaskCompletionSource<InboundMessage>? tcs))
        {
            LogUnknownCorrelationId(correlationId);
            return Task.CompletedTask;
        }

        // CRITICAL: copy body bytes — RabbitMQ.Client frees memory after the handler returns.
        byte[] bodyCopy = args.Body.ToArray();
        ReadOnlySequence<byte> bodySequence = bodyCopy.Length == 0
            ? ReadOnlySequence<byte>.Empty
            : new ReadOnlySequence<byte>(bodyCopy);

        Dictionary<string, string> headers = _headerMapper.MapInbound(args.BasicProperties);

        string messageId = headers.TryGetValue("message-id", out string? mappedId) && !string.IsNullOrEmpty(mappedId)
            ? mappedId
            : Guid.NewGuid().ToString();

        var inbound = new InboundMessage(
            messageId: messageId,
            headers: headers,
            body: bodySequence,
            deliveryTag: args.DeliveryTag);

        tcs.TrySetResult(inbound);

        return Task.CompletedTask;
    }

    // ADR-003: serialize into a pooled buffer, return only the written portion as ReadOnlyMemory<byte>.
    // The caller uses this for a single publish then discards it; the array is returned to the pool
    // after BasicPublishAsync completes (we pass ReadOnlyMemory<byte> to the publish API).
    // NOTE: The underlying array lives until the publish completes because BasicPublishAsync
    // makes a copy internally before the Task completes for publisher confirms.
    private ReadOnlyMemory<byte> SerializeRequest(TRequest request)
    {
        // Rent a buffer from the pool, grow if needed via the writer.
        using var writer = new SimplePooledWriter(initialCapacity: 4096);
        _serializer.Serialize(request, writer);
        // Must copy written bytes to a new heap array here — we cannot return a slice of the rented buffer
        // because RabbitMQ.Client 7.x copies the body internally, but we must keep the memory valid
        // until after the await. Returning WrittenMemory here would give us a slice of the rented buffer
        // which gets returned in Dispose(). Instead, we rent, copy, and return.
        // This is a SINGLE allocation per request (not per byte / per message field) — acceptable per spec.
        return writer.WrittenMemory.ToArray();
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

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null!;
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
