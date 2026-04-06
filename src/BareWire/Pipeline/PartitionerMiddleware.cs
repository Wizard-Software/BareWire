using BareWire.Abstractions.Pipeline;

namespace BareWire.Pipeline;

/// <summary>
/// A middleware that serializes message processing per partition key, ensuring that messages
/// with the same key are never processed concurrently. Messages with different keys may be
/// processed in parallel (up to <c>partitionCount</c> concurrent messages).
/// </summary>
/// <remarks>
/// Partitioning is achieved by mapping the key selector result to a fixed-size array of
/// <see cref="SemaphoreSlim"/> instances with initial count 1. The partition index is computed
/// as <c>Math.Abs(key.GetHashCode()) % partitionCount</c>.
///
/// The default key selector reads the <c>CorrelationId</c> header (parsed as a <see cref="Guid"/>)
/// and falls back to <see cref="MessageContext.MessageId"/> when the header is absent or unparseable.
/// </remarks>
internal sealed class PartitionerMiddleware : IMessageMiddleware, IAsyncDisposable
{
    private readonly int _partitionCount;
    private readonly Func<MessageContext, Guid> _keySelector;
    private readonly SemaphoreSlim[] _partitions;

    internal PartitionerMiddleware(int partitionCount, Func<MessageContext, Guid>? keySelector = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partitionCount, 1);

        _partitionCount = partitionCount;
        _keySelector = keySelector ?? DefaultKeySelector;
        _partitions = new SemaphoreSlim[partitionCount];

        for (int i = 0; i < partitionCount; i++)
            _partitions[i] = new SemaphoreSlim(1, 1);
    }

    /// <inheritdoc />
    public async Task InvokeAsync(MessageContext context, NextMiddleware nextMiddleware)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(nextMiddleware);

        Guid key = _keySelector(context);
        int partitionIndex = Math.Abs(key.GetHashCode()) % _partitionCount;
        SemaphoreSlim semaphore = _partitions[partitionIndex];

        await semaphore.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            await nextMiddleware(context).ConfigureAwait(false);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (SemaphoreSlim semaphore in _partitions)
            semaphore.Dispose();

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    private static Guid DefaultKeySelector(MessageContext context)
    {
        if (context.Headers.TryGetValue("CorrelationId", out string? value))
            return GuidHelper.ParseOrHash(value);

        return context.MessageId;
    }
}
