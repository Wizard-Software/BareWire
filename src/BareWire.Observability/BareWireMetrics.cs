using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BareWire.Observability;

internal sealed class BareWireMetrics : IDisposable
{
    private readonly Meter _meter;

    private readonly Counter<long> _published;
    private readonly Counter<long> _consumed;
    private readonly Counter<long> _failed;
    private readonly Counter<long> _deadLettered;
    private readonly Counter<long> _publishRejected;

    private readonly Histogram<double> _duration;
    private readonly Histogram<long> _size;

    private readonly UpDownCounter<long> _inflight;
    private readonly UpDownCounter<long> _inflightBytes;
    private readonly UpDownCounter<long> _publishPending;

    internal BareWireMetrics(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create("BareWire");

        _published = _meter.CreateCounter<long>(
            "barewire.messages.published",
            unit: null,
            description: "Number of messages published");

        _consumed = _meter.CreateCounter<long>(
            "barewire.messages.consumed",
            unit: null,
            description: "Number of messages consumed");

        _failed = _meter.CreateCounter<long>(
            "barewire.messages.failed",
            unit: null,
            description: "Number of message processing failures");

        _deadLettered = _meter.CreateCounter<long>(
            "barewire.messages.dead_lettered",
            unit: null,
            description: "Number of messages moved to dead-letter queue");

        _publishRejected = _meter.CreateCounter<long>(
            "barewire.publish.rejected",
            unit: null,
            description: "Number of publish operations rejected due to full outgoing channel");

        _duration = _meter.CreateHistogram<double>(
            "barewire.message.duration",
            unit: "ms",
            description: "Message handler processing duration");

        _size = _meter.CreateHistogram<long>(
            "barewire.message.size",
            unit: "By",
            description: "Serialized message payload size");

        _inflight = _meter.CreateUpDownCounter<long>(
            "barewire.messages.inflight",
            unit: null,
            description: "Number of messages currently in-flight per endpoint");

        _inflightBytes = _meter.CreateUpDownCounter<long>(
            "barewire.messages.inflight.bytes",
            unit: "By",
            description: "Total bytes currently in-flight per endpoint");

        _publishPending = _meter.CreateUpDownCounter<long>(
            "barewire.publish.pending",
            unit: null,
            description: "Number of messages pending dispatch in the bounded outgoing channel");
    }

    internal void RecordPublish(string endpoint, string messageType, int messageSize)
    {
        var tags = new TagList
        {
            { "endpoint", endpoint },
            { "message_type", messageType }
        };

        _published.Add(1, tags);
        _size.Record(messageSize, tags);
    }

    internal void RecordConsume(string endpoint, string messageType, double durationMs, int messageSize)
    {
        var tags = new TagList
        {
            { "endpoint", endpoint },
            { "message_type", messageType }
        };

        _consumed.Add(1, tags);
        _duration.Record(durationMs, tags);
        _size.Record(messageSize, tags);
    }

    internal void RecordFailure(string endpoint, string messageType, string errorType)
    {
        var tags = new TagList
        {
            { "endpoint", endpoint },
            { "message_type", messageType },
            { "error_type", errorType }
        };

        _failed.Add(1, tags);
    }

    internal void RecordDeadLetter(string endpoint, string messageType)
    {
        var tags = new TagList
        {
            { "endpoint", endpoint },
            { "message_type", messageType }
        };

        _deadLettered.Add(1, tags);
    }

    internal void RecordInflight(string endpoint, string messageType, int delta, int bytesDelta)
    {
        var messageTags = new TagList
        {
            { "endpoint", endpoint },
            { "message_type", messageType }
        };

        var endpointTags = new TagList
        {
            { "endpoint", endpoint }
        };

        _inflight.Add(delta, messageTags);
        _inflightBytes.Add(bytesDelta, endpointTags);
    }

    internal void RecordPublishPending(string endpoint, int delta)
    {
        var tags = new TagList
        {
            { "endpoint", endpoint }
        };

        _publishPending.Add(delta, tags);
    }

    internal void RecordPublishRejected(string endpoint, string messageType)
    {
        var tags = new TagList
        {
            { "endpoint", endpoint },
            { "message_type", messageType }
        };

        _publishRejected.Add(1, tags);
    }

    public void Dispose() => _meter.Dispose();
}
