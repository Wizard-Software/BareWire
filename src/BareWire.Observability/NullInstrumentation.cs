using System.Diagnostics;
using BareWire.Abstractions.Observability;

namespace BareWire.Observability;

internal sealed class NullInstrumentation : IBareWireInstrumentation
{
    public Activity? StartPublishActivity(string messageType, string destination, Guid messageId)
        => null;

    public Activity? StartConsumeActivity(
        string messageType,
        string endpoint,
        Guid messageId,
        IReadOnlyDictionary<string, string> headers)
        => null;

    public Activity? StartSagaTransitionActivity(
        string sagaType,
        string stateFrom,
        string stateTo,
        Guid correlationId)
        => null;

    public void RecordPublish(string endpoint, string messageType, int messageSize) { }

    public void RecordConsume(string endpoint, string messageType, double durationMs, int messageSize) { }

    public void RecordFailure(string endpoint, string messageType, string errorType) { }

    public void RecordDeadLetter(string endpoint, string messageType) { }

    public void RecordInflight(string endpoint, string messageType, int delta, int bytesDelta) { }

    public void RecordPublishPending(string endpoint, int delta) { }

    public void RecordPublishRejected(string endpoint, string messageType) { }

    public void InjectTraceContext(Activity? activity, IDictionary<string, string> headers) { }

    public Activity? ExtractTraceContext(IReadOnlyDictionary<string, string> headers, string operationName)
        => null;
}
