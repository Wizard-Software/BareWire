using System.Diagnostics;
using BareWire.Abstractions.Observability;

namespace BareWire.Observability;

internal sealed class BareWireInstrumentation(BareWireMetrics metrics)
    : IBareWireInstrumentation, IDisposable
{
    public Activity? StartPublishActivity(string messageType, string destination, Guid messageId)
        => BareWireActivitySource.StartPublish(messageType, destination, messageId);

    public Activity? StartConsumeActivity(
        string messageType,
        string endpoint,
        Guid messageId,
        IReadOnlyDictionary<string, string> headers)
    {
        DistributedContextPropagator.Current.ExtractTraceIdAndState(
            headers,
            static (carrier, key, out value, out values) =>
            {
                var dict = (IReadOnlyDictionary<string, string>)carrier!;
                if (dict.TryGetValue(key, out var single))
                {
                    value = single;
                    values = null;
                }
                else
                {
                    value = null;
                    values = null;
                }
            },
            out var traceParent,
            out var traceState);

        ActivityContext.TryParse(traceParent, traceState, isRemote: true, out var parentContext);

        return BareWireActivitySource.StartConsume(messageType, endpoint, messageId, parentContext);
    }

    public Activity? StartSagaTransitionActivity(
        string sagaType,
        string stateFrom,
        string stateTo,
        Guid correlationId)
        => BareWireActivitySource.StartSagaTransition(sagaType, stateFrom, stateTo, correlationId);

    public void RecordPublish(string endpoint, string messageType, int messageSize)
        => metrics.RecordPublish(endpoint, messageType, messageSize);

    public void RecordConsume(string endpoint, string messageType, double durationMs, int messageSize)
        => metrics.RecordConsume(endpoint, messageType, durationMs, messageSize);

    public void RecordFailure(string endpoint, string messageType, string errorType)
        => metrics.RecordFailure(endpoint, messageType, errorType);

    public void RecordDeadLetter(string endpoint, string messageType)
        => metrics.RecordDeadLetter(endpoint, messageType);

    public void RecordInflight(string endpoint, string messageType, int delta, int bytesDelta)
        => metrics.RecordInflight(endpoint, messageType, delta, bytesDelta);

    public void RecordPublishPending(string endpoint, int delta)
        => metrics.RecordPublishPending(endpoint, delta);

    public void RecordPublishRejected(string endpoint, string messageType)
        => metrics.RecordPublishRejected(endpoint, messageType);

    public void InjectTraceContext(Activity? activity, IDictionary<string, string> headers)
        => TraceContextPropagator.InjectTraceContext(activity, headers);

    public Activity? ExtractTraceContext(IReadOnlyDictionary<string, string> headers, string operationName)
        => TraceContextPropagator.ExtractTraceContext(headers, operationName);

    public void Dispose() => metrics.Dispose();
}
