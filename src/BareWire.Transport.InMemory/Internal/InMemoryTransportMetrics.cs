using System.Collections.Immutable;
using System.Diagnostics.Metrics;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// The single owner of every in-memory transport instrument, all reported on one externally supplied
/// <see cref="Meter"/>: three observable gauges (per-queue occupancy, capacity, and latch state) plus two
/// counters (rejected messages/copies, tagged with a reason, and latch episodes). Every instrument is
/// created once, here, from the topology's declared queues — no other type in this package creates an
/// instrument of its own. Constructed with a <see langword="null"/> meter creates no instrument at all:
/// every <c>Record*</c> method below is then a no-op, matching this package's existing opt-in,
/// zero-cost-when-unused pattern for observability.
/// </summary>
/// <remarks>
/// <b>Cardinality rule (MUST).</b> The only tag keys used by any instrument this type owns are
/// <see cref="QueueTag"/>, <see cref="ExchangeTag"/>, and <see cref="ReasonTag"/>. A <see cref="QueueTag"/>
/// or <see cref="ExchangeTag"/> value comes exclusively from the sealed topology this adapter was built
/// from — a declared queue or exchange name — never a publisher-supplied routing key, a message's
/// <c>MessageId</c>, its body, or its headers, all of which are unbounded, caller-controlled cardinality.
/// A rejection with no meaningful queue or exchange name (a batch never processed because the adapter was
/// closed, or because the call was cancelled) is recorded with the <see cref="ReasonTag"/> alone.
/// </remarks>
internal sealed class InMemoryTransportMetrics
{
    /// <summary>
    /// The shared meter name every in-memory transport instrument is created on — the same one the
    /// Observability package registers, so <c>AddMeter("BareWire")</c> in an OpenTelemetry pipeline
    /// covers this transport's instruments without any extra configuration.
    /// </summary>
    internal const string MeterName = "BareWire";

    /// <summary>The name of the per-queue occupancy gauge.</summary>
    internal const string OccupancyGaugeName = "barewire.inmemory.queue.occupancy";

    /// <summary>The name of the per-queue capacity gauge.</summary>
    internal const string CapacityGaugeName = "barewire.inmemory.queue.capacity";

    /// <summary>The name of the per-queue "full" latch state gauge (0 or 1).</summary>
    internal const string LatchedGaugeName = "barewire.inmemory.queue.latched";

    /// <summary>The name of the per-queue latch-episode counter.</summary>
    internal const string LatchEpisodesCounterName = "barewire.inmemory.queue.latch_episodes";

    /// <summary>The name of the single, shared rejected-messages/copies counter.</summary>
    internal const string RejectedCounterName = "barewire.inmemory.messages.rejected";

    /// <summary>The tag key carrying the low-cardinality rejection reason. Present on every measurement.</summary>
    internal const string ReasonTag = "reason";

    /// <summary>The tag key carrying a declared queue name. Never a publisher-supplied routing key.</summary>
    internal const string QueueTag = "queue";

    /// <summary>The tag key carrying a declared exchange name. Never a publisher-supplied exchange name.</summary>
    internal const string ExchangeTag = "exchange";

    private readonly Counter<long>? _rejectedCounter;
    private readonly Counter<long>? _latchEpisodesCounter;

    /// <param name="meter">
    /// The meter every instrument is created on. <see langword="null"/> creates no instrument at all —
    /// every <c>Record*</c> method on the resulting instance is then a no-op.
    /// </param>
    /// <param name="queues">
    /// The broker's queues, read once here to build the gauges' observation callbacks and their
    /// preallocated, per-queue tag arrays. Must be read AFTER the broker's topology registry has been
    /// attached (<c>InMemoryBroker.AttachRegistry</c>), so every declared queue is already present.
    /// </param>
    internal InMemoryTransportMetrics(Meter? meter, ImmutableArray<InMemoryQueue> queues)
    {
        if (meter is null)
        {
            return;
        }

        // One tag array per queue, preallocated here so the gauge callbacks below — invoked only when an
        // observer collects, never per message — allocate nothing beyond the Measurement<int>[] each
        // callback returns.
        var queueTags = new KeyValuePair<string, object?>[queues.Length][];
        for (int i = 0; i < queues.Length; i++)
        {
            queueTags[i] = [new KeyValuePair<string, object?>(QueueTag, queues[i].Name)];
        }

        meter.CreateObservableGauge(
            OccupancyGaugeName,
            () => Observe(queues, queueTags, static q => q.Occupancy),
            unit: "{message}",
            description: "The number of slots currently occupied on this in-memory queue. Tagged with the " +
                "declared queue name only.");

        meter.CreateObservableGauge(
            CapacityGaugeName,
            () => Observe(queues, queueTags, static q => q.Capacity),
            unit: "{message}",
            description: "This in-memory queue's configured capacity. Tagged with the declared queue name only.");

        meter.CreateObservableGauge(
            LatchedGaugeName,
            () => Observe(queues, queueTags, static q => q.IsLatched ? 1 : 0),
            unit: "{latch}",
            description: "Whether this in-memory queue's \"full\" latch is currently set (1) or clear (0). " +
                "Tagged with the declared queue name only.");

        _latchEpisodesCounter = meter.CreateCounter<long>(
            LatchEpisodesCounterName,
            unit: "{episode}",
            description: "The number of times this in-memory queue's \"full\" latch was set. Tagged with " +
                "the declared queue name only.");

        _rejectedCounter = meter.CreateCounter<long>(
            RejectedCounterName,
            unit: "{message}",
            description: "The number of in-memory messages, or fan-out copies of one message, rejected " +
                "before or during admission, dropped during settlement, or dropped on shutdown. Tagged " +
                "with 'reason' always, and at most one of 'queue' or 'exchange' — never a publisher-" +
                "supplied routing key or message id.");
    }

    /// <summary>
    /// Records that <paramref name="count"/> in-memory messages, or fan-out copies of one message, were
    /// rejected for <paramref name="reason"/> against a specific, declared <paramref name="queueName"/> —
    /// a full or latched target queue during send, or a drop during settlement, drain, or shutdown.
    /// </summary>
    internal void RecordQueueRejected(string reason, string queueName, long count = 1) =>
        _rejectedCounter?.Add(
            count,
            new KeyValuePair<string, object?>(ReasonTag, reason),
            new KeyValuePair<string, object?>(QueueTag, queueName));

    /// <summary>
    /// Records that <paramref name="count"/> in-memory messages were rejected for <paramref name="reason"/>
    /// while being validated or routed against an exchange. <paramref name="declaredExchange"/> becomes
    /// the measurement's <see cref="ExchangeTag"/> only when the exchange is declared in the topology —
    /// <see langword="null"/> records the reason alone, never a publisher-supplied, unbounded-cardinality
    /// exchange name.
    /// </summary>
    internal void RecordExchangeRejected(string reason, string? declaredExchange, long count = 1)
    {
        if (declaredExchange is not null)
        {
            _rejectedCounter?.Add(
                count,
                new KeyValuePair<string, object?>(ReasonTag, reason),
                new KeyValuePair<string, object?>(ExchangeTag, declaredExchange));
        }
        else
        {
            _rejectedCounter?.Add(count, new KeyValuePair<string, object?>(ReasonTag, reason));
        }
    }

    /// <summary>
    /// Records that <paramref name="count"/> in-memory messages were rejected for <paramref name="reason"/>
    /// with no queue or exchange name that has any meaning for the batch — messages never processed
    /// because the adapter was closed, or because the call was cancelled.
    /// </summary>
    internal void RecordRejected(string reason, long count = 1) =>
        _rejectedCounter?.Add(count, new KeyValuePair<string, object?>(ReasonTag, reason));

    /// <summary>Records that <paramref name="queueName"/>'s "full" latch was set — one episode.</summary>
    internal void RecordLatchEpisode(string queueName) =>
        _latchEpisodesCounter?.Add(1, new KeyValuePair<string, object?>(QueueTag, queueName));

    private static Measurement<int>[] Observe(
        ImmutableArray<InMemoryQueue> queues,
        KeyValuePair<string, object?>[][] queueTags,
        Func<InMemoryQueue, int> select)
    {
        var measurements = new Measurement<int>[queues.Length];
        for (int i = 0; i < queues.Length; i++)
        {
            measurements[i] = new Measurement<int>(select(queues[i]), queueTags[i]);
        }

        return measurements;
    }
}
