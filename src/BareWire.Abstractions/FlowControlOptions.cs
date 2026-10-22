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
    /// <remarks>
    /// <para>
    /// Receive endpoints configured through the bus set only <see cref="MaxInFlightMessages"/> (from the
    /// endpoint <c>PrefetchCount</c>); this value and <see cref="FullMode"/> take effect only when
    /// <c>ITransportAdapter.ConsumeAsync</c> is called directly. It must be greater than zero for every
    /// transport, including Kafka and in-memory (which does not otherwise use it); a non-positive value makes
    /// <c>ConsumeAsync</c> throw <see cref="System.ArgumentOutOfRangeException"/>.
    /// </para>
    /// <para>
    /// The RabbitMQ transport raises its internal buffer to at least the prefetch count
    /// (<see cref="MaxInFlightMessages"/>, capped at 65535), so that a full prefetch window always fits.
    /// Its worst-case buffer memory is therefore the prefetch count multiplied by the message body size;
    /// <see cref="MaxInFlightBytes"/> does not bound that buffer.
    /// </para>
    /// <para>
    /// The Amazon SQS and Google Cloud Pub/Sub transports likewise raise the internal buffer to at least
    /// <see cref="MaxInFlightMessages"/>, so a received message always fits. The number of buffered messages
    /// is bounded by the smaller of <see cref="MaxInFlightMessages"/> and the transport's own in-flight limit
    /// (the <c>MaxInFlightMessages</c> transport option, shared by all endpoints of the adapter), so buffer
    /// memory grows with that number multiplied by the message body size; <see cref="MaxInFlightBytes"/>
    /// does not bound it.
    /// </para>
    /// </remarks>
    public int InternalQueueCapacity { get; set; } = 1_000;

    /// <summary>
    /// Gets or sets the behaviour applied when the internal queue is full and a new message arrives.
    /// Defaults to <see cref="BoundedChannelFullMode.Wait"/>, which applies back-pressure
    /// by blocking the transport reader until space becomes available.
    /// </summary>
    /// <remarks>
    /// Every transport (RabbitMQ, Kafka, Amazon SQS, Google Cloud Pub/Sub, Azure Service Bus and in-memory)
    /// accepts only <see cref="BoundedChannelFullMode.Wait"/>; any other value makes
    /// <c>ITransportAdapter.ConsumeAsync</c> throw <see cref="System.ArgumentOutOfRangeException"/>, because a
    /// <c>Drop*</c> mode would discard a received message without returning it to the broker.
    /// </remarks>
    public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.Wait;
}
