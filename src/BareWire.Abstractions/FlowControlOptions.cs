using System.Threading.Channels;

namespace BareWire.Abstractions;

/// <summary>
/// Configures credit-based flow control for inbound message consumption.
/// All limits are bounded to prevent unbounded memory growth and enable predictable back-pressure.
/// </summary>
public sealed class FlowControlOptions
{
    /// <summary>
    /// Gets or sets the maximum number of messages that can be in-flight (dispatched but not yet settled)
    /// concurrently across all consumers on a receive endpoint.
    /// Defaults to <c>100</c>.
    /// </summary>
    public int MaxInFlightMessages { get; set; } = 100;

    /// <summary>
    /// Gets or sets the maximum total byte size of all in-flight message bodies allowed concurrently.
    /// When exceeded, the consumer will stop requesting additional messages until capacity is freed.
    /// Defaults to <c>67,108,864</c> bytes (64 MiB).
    /// </summary>
    public long MaxInFlightBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the bounded capacity of the internal dispatch queue that buffers messages
    /// received from the transport before they are handed off to consumer handlers.
    /// Defaults to <c>1,000</c>.
    /// </summary>
    public int InternalQueueCapacity { get; set; } = 1_000;

    /// <summary>
    /// Gets or sets the behaviour applied when the internal queue is full and a new message arrives.
    /// Defaults to <see cref="BoundedChannelFullMode.Wait"/>, which applies back-pressure
    /// by blocking the transport reader until space becomes available.
    /// </summary>
    public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.Wait;
}
