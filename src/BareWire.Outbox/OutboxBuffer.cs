using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Transport;

namespace BareWire.Outbox;

/// <summary>
/// Per-consume buffer of outbound messages awaiting a transactional write to the outbox. Thread-safe:
/// a handler may publish from several concurrent tasks. Once <see cref="Seal"/> is called the buffer
/// refuses new messages, so nothing can be appended after the snapshot has been persisted.
/// </summary>
internal sealed class OutboxBuffer
{
    private readonly Lock _gate = new();
    private readonly List<OutboundMessage> _messages = [];
    private readonly int _maxMessages;
    private readonly long _maxBytes;
    private long _bufferedBytes;
    private bool _sealed;

    internal OutboxBuffer(int maxMessages = int.MaxValue, long maxBytes = long.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMessages);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _maxMessages = maxMessages;
        _maxBytes = maxBytes;
    }

    /// <summary>
    /// Adds <paramref name="message"/> to the buffer.
    /// </summary>
    /// <returns><see langword="false"/> when the buffer is sealed and the message was not added.</returns>
    /// <exception cref="BareWireException">The buffer already holds the maximum number of messages.</exception>
    internal bool TryAdd(OutboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (_gate)
        {
            if (_sealed)
                return false;

            if (_messages.Count >= _maxMessages)
            {
                throw new BareWireException(
                    $"The outbox buffer for a single consume operation reached its limit of " +
                    $"{_maxMessages} messages (MaxBufferedMessagesPerConsume). The consume operation fails.");
            }

            if (_bufferedBytes + message.Body.Length > _maxBytes)
            {
                throw new BareWireException(
                    $"The outbox buffer for a single consume operation reached its limit of " +
                    $"{_maxBytes} bytes (MaxBufferedBytesPerConsume). The consume operation fails.");
            }

            _messages.Add(message);
            _bufferedBytes += message.Body.Length;
            return true;
        }
    }

    /// <summary>Adds <paramref name="message"/>; throws when the buffer is sealed.</summary>
    /// <exception cref="InvalidOperationException">The buffer is sealed.</exception>
    internal void Add(OutboundMessage message)
    {
        if (!TryAdd(message))
            throw new InvalidOperationException("The outbox buffer is sealed and no longer accepts messages.");
    }

    /// <summary>Returns a point-in-time snapshot of the buffered messages.</summary>
    internal IReadOnlyList<OutboundMessage> GetMessages()
    {
        lock (_gate)
        {
            return [.. _messages];
        }
    }

    /// <summary>Stops accepting messages; subsequent <see cref="TryAdd"/> calls return <see langword="false"/>.</summary>
    internal void Seal()
    {
        lock (_gate)
        {
            _sealed = true;
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _messages.Clear();
            _bufferedBytes = 0;
        }
    }

    /// <summary>Gets a value indicating whether the buffer no longer accepts messages.</summary>
    internal bool IsSealed
    {
        get
        {
            lock (_gate)
            {
                return _sealed;
            }
        }
    }

    /// <summary>Gets the number of buffered messages.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _messages.Count;
            }
        }
    }

    internal bool IsEmpty
    {
        get
        {
            lock (_gate)
            {
                return _messages.Count == 0;
            }
        }
    }
}
