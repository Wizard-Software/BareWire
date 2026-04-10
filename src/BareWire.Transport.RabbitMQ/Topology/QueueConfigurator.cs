using BareWire.Abstractions.Topology;

namespace BareWire.Transport.RabbitMQ.Topology;

/// <summary>
/// Accumulates queue arguments via the fluent <see cref="IQueueConfigurator"/> API
/// and produces an immutable dictionary for <see cref="QueueDeclaration"/>.
/// </summary>
internal sealed class QueueConfigurator : IQueueConfigurator
{
    private readonly Dictionary<string, object> _arguments = [];

    /// <inheritdoc />
    public IQueueConfigurator DeadLetterExchange(string exchangeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(exchangeName);
        _arguments["x-dead-letter-exchange"] = exchangeName;
        return this;
    }

    /// <inheritdoc />
    public IQueueConfigurator DeadLetterRoutingKey(string routingKey)
    {
        ArgumentNullException.ThrowIfNull(routingKey);
        _arguments["x-dead-letter-routing-key"] = routingKey;
        return this;
    }

    /// <inheritdoc />
    public IQueueConfigurator MessageTtl(TimeSpan ttl)
    {
        long totalMs = (long)ttl.TotalMilliseconds;

        if (totalMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), ttl,
                "Message TTL must be positive.");
        }

        if (totalMs > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), ttl,
                $"Message TTL must not exceed {int.MaxValue} milliseconds (~24.85 days).");
        }

        _arguments["x-message-ttl"] = totalMs;
        return this;
    }

    /// <inheritdoc />
    public IQueueConfigurator MaxLength(long maxMessages)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMessages);
        _arguments["x-max-length"] = maxMessages;
        return this;
    }

    /// <inheritdoc />
    public IQueueConfigurator MaxLengthBytes(long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _arguments["x-max-length-bytes"] = maxBytes;
        return this;
    }

    /// <inheritdoc />
    public IQueueConfigurator SetQueueType(QueueType type)
    {
        _arguments["x-queue-type"] = type switch
        {
            QueueType.Classic => "classic",
            QueueType.Quorum => "quorum",
            QueueType.Stream => "stream",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };
        return this;
    }

    /// <inheritdoc />
    public IQueueConfigurator Overflow(OverflowStrategy strategy)
    {
        _arguments["x-overflow"] = strategy switch
        {
            OverflowStrategy.DropHead => "drop-head",
            OverflowStrategy.RejectPublish => "reject-publish",
            OverflowStrategy.RejectPublishDlx => "reject-publish-dlx",
            _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, null),
        };
        return this;
    }

    /// <inheritdoc />
    public IQueueConfigurator Argument(string key, object value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        _arguments[key] = value;
        return this;
    }

    /// <summary>
    /// Builds an immutable dictionary of the accumulated queue arguments.
    /// Returns <see langword="null"/> if no arguments were configured.
    /// </summary>
    internal IReadOnlyDictionary<string, object>? Build() =>
        _arguments.Count > 0
            ? new Dictionary<string, object>(_arguments)
            : null;
}
