namespace BareWire.Abstractions.Topology;

/// <summary>
/// Controls what happens when a queue reaches its <c>x-max-length</c> or <c>x-max-length-bytes</c> limit.
/// </summary>
public enum OverflowStrategy
{
    /// <summary>Oldest message is removed (dead-lettered if DLX configured). RabbitMQ default.</summary>
    DropHead,

    /// <summary>New message is rejected (basic.nack). Existing messages stay.</summary>
    RejectPublish,

    /// <summary>New message is rejected AND dead-lettered. Requires RabbitMQ 3.12+.</summary>
    RejectPublishDlx,
}
