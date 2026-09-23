using BareWire.Abstractions;

namespace BareWire.Abstractions.Transport;

/// <summary>
/// Lets a transport adapter contribute its own health (for example queue occupancy) to
/// <see cref="IBusControl.CheckHealth"/>, without the transport referencing any observability
/// package.
/// </summary>
/// <remarks>
/// This is an internal coordination protocol between <c>BareWire</c> (the bus control) and transport
/// adapters, in the same way as <see cref="IGracefulDrainTransport"/>, <see cref="IConsumerChannelManager"/>
/// and <see cref="IDurableParkSettlement"/> — it is not part of the public <see cref="ITransportAdapter"/>
/// surface. Transports backed by a durable broker are not required to implement this interface.
/// <para>
/// <see cref="GetHealth"/> reports the transport's own view of its queues. <see cref="BusHealthStatus.Status"/>
/// should be the worst of the source's own endpoint statuses; by convention a queue becomes
/// <see cref="BusStatus.Degraded"/> once its occupancy reaches at least 90% of its declared capacity.
/// The bus control merges the returned status and endpoints into the aggregate it already builds from
/// flow-control endpoints — it does not reinterpret or reformat them.
/// </para>
/// <para>
/// Every description returned from this member — including <see cref="BusHealthStatus.Description"/>,
/// each <see cref="EndpointHealthStatus.Description"/>, and the message of any exception this member
/// might raise — must contain metadata only: queue name, occupancy, and capacity. It must never contain
/// message body, headers, routing key, or a message identifier.
/// </para>
/// <para>
/// Implementations must be non-blocking, thread-safe, and must not throw. The expected cost is
/// O(number of declared queues): read per-queue counters with <see cref="System.Threading.Volatile"/>
/// or <see cref="System.Threading.Interlocked"/> only — never take a broker-wide or per-queue lock,
/// never read <c>Channel&lt;T&gt;.Reader.Count</c> on a bounded channel, and never enumerate a
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/> through its
/// <c>Keys</c>, <c>Values</c>, or <c>Count</c> members. Implementations must not log on every call —
/// log only when a queue's status changes.
/// </para>
/// </remarks>
internal interface ITransportHealthSource
{
    /// <summary>
    /// Returns the current transport health. Must be non-blocking, thread-safe, and must not throw.
    /// </summary>
    /// <returns>The aggregated health of the transport's own queues, metadata only.</returns>
    BusHealthStatus GetHealth();
}
