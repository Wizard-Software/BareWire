using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Observability;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Core.FlowControl;
using BareWire.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace BareWire.Core.Bus;

internal sealed partial class BareWireBus : IBus
{
    private readonly ITransportAdapter _adapter;
    private readonly IMessageSerializer _serializer;
    private readonly MessagePipeline _pipeline;
    private readonly FlowController _flowController;
    private readonly PublishFlowControlOptions _publishFlowControl;
    private readonly ILogger<BareWireBus> _logger;
    private readonly IBareWireInstrumentation _instrumentation;

    private readonly ConcurrentDictionary<Uri, ISendEndpoint> _sendEndpoints = new();
    private readonly Channel<OutboundMessage> _outgoingChannel;
    private readonly CancellationTokenSource _publishCts = new();
    private readonly SemaphoreSlim _bytesSemaphore = new(0, 1);
    private long _pendingBytes;
    private Task _publisherTask = Task.CompletedTask;
    private bool _disposed;

    internal BareWireBus(
        ITransportAdapter adapter,
        IMessageSerializer serializer,
        MessagePipeline pipeline,
        FlowController flowController,
        PublishFlowControlOptions publishFlowControl,
        ILogger<BareWireBus> logger,
        IBareWireInstrumentation instrumentation)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _flowController = flowController ?? throw new ArgumentNullException(nameof(flowController));
        _publishFlowControl = publishFlowControl ?? throw new ArgumentNullException(nameof(publishFlowControl));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _instrumentation = instrumentation ?? throw new ArgumentNullException(nameof(instrumentation));

        BusId = Guid.NewGuid();
        Address = new Uri($"barewire://{adapter.TransportName.ToLowerInvariant()}/bus/{BusId:N}");

        _outgoingChannel = Channel.CreateBounded<OutboundMessage>(
            new BoundedChannelOptions(publishFlowControl.MaxPendingPublishes)
            {
                SingleWriter = false,
                SingleReader = true,
                FullMode = publishFlowControl.FullMode,
            });
    }

    public Guid BusId { get; }
    public Uri Address { get; }

    // Called by BareWireBusControl.StartAsync to begin the background publish loop.
    internal void StartPublishing()
    {
        _publisherTask = RunPublisherLoopAsync(_publishCts.Token);
    }

    // ── IPublishEndpoint ─────────────────────────────────────────────────────

    public async Task PublishAsync<T>(T message, CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string routingKey = typeof(T).FullName ?? typeof(T).Name;
        string messageType = typeof(T).Name;
        Guid messageId = Guid.NewGuid();

        Activity? activity = _instrumentation.StartPublishActivity(messageType, routingKey, messageId);
        try
        {
            Dictionary<string, string> headers = [];
            _instrumentation.InjectTraceContext(activity, headers);

            OutboundMessage outbound = MessagePipeline.ProcessOutboundAsync(
                message,
                _serializer,
                routingKey,
                headers,
                cancellationToken);

            await WaitForByteBudgetAsync(outbound.Body.Length, cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _pendingBytes, outbound.Body.Length);

            _instrumentation.RecordPublishPending(routingKey, +1);
            CheckPublishHealthAlert();

            await _outgoingChannel.Writer.WriteAsync(outbound, cancellationToken).ConfigureAwait(false);
            _instrumentation.RecordPublish(routingKey, messageType, outbound.Body.Length);
        }
        finally
        {
            activity?.Dispose();
        }
    }

    public async Task PublishRawAsync(
        ReadOnlyMemory<byte> payload,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contentType);
        ObjectDisposedException.ThrowIf(_disposed, this);

        const string rawMessageType = "raw";
        const string rawEndpoint = "";

        OutboundMessage outbound = new(
            routingKey: rawEndpoint,
            headers: new Dictionary<string, string>(),
            body: payload,
            contentType: contentType);

        await WaitForByteBudgetAsync(outbound.Body.Length, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _pendingBytes, outbound.Body.Length);

        _instrumentation.RecordPublishPending(rawEndpoint, +1);
        await _outgoingChannel.Writer.WriteAsync(outbound, cancellationToken).ConfigureAwait(false);
        _instrumentation.RecordPublish(rawEndpoint, rawMessageType, outbound.Body.Length);
    }

    // ── ISendEndpointProvider ────────────────────────────────────────────────

    public Task<ISendEndpoint> GetSendEndpoint(Uri address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        ObjectDisposedException.ThrowIf(_disposed, this);

        ISendEndpoint endpoint = _sendEndpoints.GetOrAdd(
            address,
            static (uri, state) => new BareWireSendEndpoint(uri, state._serializer, state._outgoingChannel.Writer),
            (_serializer, _outgoingChannel));

        return Task.FromResult(endpoint);
    }

    // ── IBus ─────────────────────────────────────────────────────────────────

    public IRequestClient<T> CreateRequestClient<T>() where T : class
        => throw new NotSupportedException(
            "Request client requires a transport adapter that supports temporary response queues.");

    public IDisposable ConnectReceiveEndpoint(string queueName, Action<IReceiveEndpointConfigurator> configure)
        => throw new NotSupportedException("Dynamic receive endpoints are not yet supported.");

    // ── IAsyncDisposable / IDisposable ────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        _publishCts.Cancel();
        _outgoingChannel.Writer.TryComplete();
        ReleaseByteSemaphore();

        try
        {
            await _publisherTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during graceful shutdown.
        }
        catch (Exception ex)
        {
            LogPublisherLoopError(_logger, ex);
        }

        _publishCts.Dispose();
        _bytesSemaphore.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // ── Background publisher loop ─────────────────────────────────────────────

    private async Task RunPublisherLoopAsync(CancellationToken ct)
    {
        const int maxBatchSize = 64;
        List<OutboundMessage> batch = new(maxBatchSize);

        try
        {
            await foreach (OutboundMessage msg in _outgoingChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                batch.Add(msg);

                // Drain as many additional items as are available without waiting.
                while (batch.Count < maxBatchSize && _outgoingChannel.Reader.TryRead(out OutboundMessage? additional))
                    batch.Add(additional);

                // Pass a snapshot so the adapter sees a stable collection even after the batch is cleared.
                OutboundMessage[] snapshot = batch.ToArray();
                await _adapter.SendBatchAsync(snapshot, ct).ConfigureAwait(false);
                DecrementPendingBytes(snapshot);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful shutdown — drain remaining items.
            while (_outgoingChannel.Reader.TryRead(out OutboundMessage? remaining))
            {
                batch.Add(remaining);

                if (batch.Count >= maxBatchSize)
                {
                    OutboundMessage[] drainSnapshot = batch.ToArray();
                    try
                    {
                        await _adapter.SendBatchAsync(drainSnapshot, CancellationToken.None).ConfigureAwait(false);
                        DecrementPendingBytes(drainSnapshot);
                    }
                    catch (Exception ex)
                    {
                        LogDrainError(_logger, ex);
                    }

                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                OutboundMessage[] finalSnapshot = batch.ToArray();
                try
                {
                    await _adapter.SendBatchAsync(finalSnapshot, CancellationToken.None).ConfigureAwait(false);
                    DecrementPendingBytes(finalSnapshot);
                }
                catch (Exception ex)
                {
                    LogDrainError(_logger, ex);
                }
            }
        }
    }

    private void CheckPublishHealthAlert()
    {
        // Best-effort capacity read — TryPeekCount is not available on BoundedChannel,
        // so we derive utilisation from the channel reader's Count property.
        if (_outgoingChannel.Reader.CanCount)
        {
            int pending = _outgoingChannel.Reader.Count;
            double utilization = (double)pending / _publishFlowControl.MaxPendingPublishes * 100.0;
            if (utilization >= 90.0)
                LogPublishBackpressureAlert(_logger, utilization, pending, _publishFlowControl.MaxPendingPublishes);
        }

        long pendingBytes = Interlocked.Read(ref _pendingBytes);
        double bytesUtilization = (double)pendingBytes / _publishFlowControl.MaxPendingBytes * 100.0;
        if (bytesUtilization >= 90.0)
            LogPublishBackpressureByteAlert(_logger, bytesUtilization, pendingBytes, _publishFlowControl.MaxPendingBytes);
    }

    // ── Byte-level tracking helpers ───────────────────────────────────────────

    /// <summary>
    /// Waits asynchronously until the byte budget allows the given payload length.
    /// Only applies backpressure when <see cref="BoundedChannelFullMode.Wait"/> is configured.
    /// Uses a semaphore as a signal — released by the publisher loop after each batch is sent.
    /// There is an intentional soft-cap: up to one additional message may slip through per cycle.
    /// </summary>
    private async ValueTask WaitForByteBudgetAsync(long bodyLength, CancellationToken cancellationToken)
    {
        if (_publishFlowControl.FullMode != BoundedChannelFullMode.Wait)
            return;

        while (Interlocked.Read(ref _pendingBytes) + bodyLength > _publishFlowControl.MaxPendingBytes)
        {
            await _bytesSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void DecrementPendingBytes(IReadOnlyList<OutboundMessage> batch)
    {
        long batchTotalBytes = 0L;
        foreach (OutboundMessage msg in batch)
        {
            batchTotalBytes += msg.Body.Length;
            _instrumentation.RecordPublishPending(msg.RoutingKey, -1);
        }

        Interlocked.Add(ref _pendingBytes, -batchTotalBytes);

        // Signal any waiters in WaitForByteBudgetAsync that space may be available.
        ReleaseByteSemaphore();
    }

    private void ReleaseByteSemaphore()
    {
        try
        {
            if (_bytesSemaphore.CurrentCount == 0)
                _bytesSemaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already at max count (1). Another thread beat us — no-op.
        }
        catch (ObjectDisposedException)
        {
            // Bus is disposing — no-op.
        }
    }

    // ── Logger messages ───────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Publish channel back-pressure alert: {Utilization:F1}% capacity used ({Pending}/{Capacity} pending).")]
    private static partial void LogPublishBackpressureAlert(
        ILogger logger, double utilization, int pending, int capacity);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Publish byte back-pressure alert: {BytesUtilization:F1}% byte budget used ({PendingBytes}/{MaxBytes} bytes).")]
    private static partial void LogPublishBackpressureByteAlert(
        ILogger logger, double bytesUtilization, long pendingBytes, long maxBytes);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Publisher loop terminated with unexpected error.")]
    private static partial void LogPublisherLoopError(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Error draining outgoing channel during shutdown.")]
    private static partial void LogDrainError(ILogger logger, Exception ex);

    // ── Inner send-endpoint ───────────────────────────────────────────────────

    private sealed class BareWireSendEndpoint : ISendEndpoint
    {
        private readonly IMessageSerializer _serializer;
        private readonly ChannelWriter<OutboundMessage> _channelWriter;

        internal BareWireSendEndpoint(
            Uri address,
            IMessageSerializer serializer,
            ChannelWriter<OutboundMessage> channelWriter)
        {
            Address = address ?? throw new ArgumentNullException(nameof(address));
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _channelWriter = channelWriter ?? throw new ArgumentNullException(nameof(channelWriter));
        }

        public Uri Address { get; }

        public async Task SendAsync<T>(T message, CancellationToken cancellationToken = default)
            where T : class
        {
            ArgumentNullException.ThrowIfNull(message);

            string routingKey = Address.AbsolutePath.TrimStart('/');
            OutboundMessage outbound = MessagePipeline.ProcessOutboundAsync(
                message,
                _serializer,
                routingKey,
                headers: null,
                cancellationToken);

            await _channelWriter.WriteAsync(outbound, cancellationToken).ConfigureAwait(false);
        }

        public async Task SendRawAsync(
            ReadOnlyMemory<byte> payload,
            string contentType,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(contentType);

            OutboundMessage outbound = new(
                routingKey: Address.AbsolutePath.TrimStart('/'),
                headers: new Dictionary<string, string>(),
                body: payload,
                contentType: contentType);

            await _channelWriter.WriteAsync(outbound, cancellationToken).ConfigureAwait(false);
        }
    }
}
