using System.Threading.Channels;

namespace BareWire.Abstractions;

/// <summary>
/// Configures publish-side back-pressure for outbound message publishing.
/// Bounds the number and total byte size of messages that can be queued for sending before
/// the publisher is asked to wait, preventing unbounded memory growth under high publish rates.
/// </summary>
public sealed class PublishFlowControlOptions
{
    /// <summary>
    /// Gets or sets the maximum number of publish operations that can be pending in the outgoing channel
    /// awaiting transport confirmation or transmission.
    /// Defaults to <c>10,000</c>.
    /// </summary>
    public int MaxPendingPublishes { get; set; } = 10_000;

    /// <summary>
    /// Gets or sets the maximum total byte size of all pending publish payloads allowed in the outgoing channel.
    /// When exceeded, <see cref="FullMode"/> determines whether the caller waits or the operation fails.
    /// Defaults to <c>268,435,456</c> bytes (256 MiB).
    /// </summary>
    public long MaxPendingBytes { get; set; } = 256 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the behaviour applied when the outgoing publish channel is at capacity.
    /// Defaults to <see cref="BoundedChannelFullMode.Wait"/>, which applies back-pressure
    /// by blocking the caller of <c>PublishAsync</c> until space becomes available.
    /// </summary>
    public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.Wait;
}
