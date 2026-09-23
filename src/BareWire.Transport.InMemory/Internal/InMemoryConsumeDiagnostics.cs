using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// Logging and metrics for the in-memory consume path: reports deliveries that were handed to a
/// consumer, never settled, and dropped because the transport shut down.
/// </summary>
internal sealed partial class InMemoryConsumeDiagnostics
{
    /// <summary>The name of the counter of deliveries dropped on shutdown.</summary>
    internal const string DroppedOnShutdownCounterName = "barewire.inmemory.deliveries.dropped_on_shutdown";

    private readonly ILogger _logger;

    // Opt-in counter — created only when an external Meter is supplied by the composition root.
    private readonly Counter<long>? _droppedOnShutdownCounter;
    private long _droppedOnShutdownCount;
    private long _disposedUnsettledCount;

    /// <param name="logger">The logger to report dropped deliveries to. Must not be <see langword="null"/>.</param>
    /// <param name="meter">An optional meter; when supplied, dropped deliveries are also counted on it.</param>
    internal InMemoryConsumeDiagnostics(ILogger logger, Meter? meter = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _droppedOnShutdownCounter = meter?.CreateCounter<long>(
            DroppedOnShutdownCounterName,
            unit: "{delivery}",
            description: "Number of in-memory deliveries handed to a consumer, never settled, and dropped " +
                "because the transport shut down. Tagged with the queue name.");
    }

    /// <summary>Gets the total number of deliveries dropped on shutdown so far.</summary>
    internal long DroppedOnShutdownCount => Interlocked.Read(ref _droppedOnShutdownCount);

    /// <summary>
    /// Records that <paramref name="count"/> unsettled deliveries of <paramref name="queueName"/> were
    /// dropped because the transport shut down.
    /// </summary>
    internal void DeliveriesDroppedOnShutdown(string queueName, int count)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref _droppedOnShutdownCount, count);
        _droppedOnShutdownCounter?.Add(count, new KeyValuePair<string, object?>("queue", queueName));
        LogDroppedOnShutdown(_logger, count, queueName);
    }

    /// <summary>Gets the total number of deliveries dropped because their message was disposed unsettled.</summary>
    internal long DisposedUnsettledCount => Interlocked.Read(ref _disposedUnsettledCount);

    /// <summary>
    /// Records that <paramref name="count"/> deliveries of <paramref name="queueName"/> could not be requeued
    /// because the consumer disposed their messages without settling them, and were dropped.
    /// </summary>
    internal void DeliveriesDisposedUnsettled(string queueName, int count)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref _disposedUnsettledCount, count);
        LogDisposedUnsettled(_logger, count, queueName);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "{Count} in-memory delivery(ies) on queue '{QueueName}' were disposed by the consumer without being " +
        "settled; their bodies were already released, so they were dropped instead of being requeued.")]
    private static partial void LogDisposedUnsettled(ILogger logger, int count, string queueName);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "In-memory transport shut down with {Count} unsettled delivery(ies) on queue '{QueueName}'; they were " +
        "dropped and their queue slots released. In-memory delivery is not durable across shutdown.")]
    private static partial void LogDroppedOnShutdown(ILogger logger, int count, string queueName);
}
