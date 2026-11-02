using System.Diagnostics;

namespace BareWire.Observability;

internal sealed class TraceContextPropagator
{
    internal const string TraceParentHeader = "traceparent";
    internal const string MassTransitActivityIdHeader = "MT-Activity-Id";

    /// <summary>
    /// Injects the W3C trace context (<c>traceparent</c>, <c>tracestate</c>) and any <c>baggage</c>
    /// of <paramref name="activity"/> into <paramref name="headers"/>. Does nothing for a <see langword="null"/> activity.
    /// </summary>
    internal static void InjectTraceContext(Activity? activity, IDictionary<string, string> headers)
    {
        if (activity is null)
        {
            return;
        }

        DistributedContextPropagator.Current.Inject(
            activity,
            headers,
            static (carrier, key, value) => ((IDictionary<string, string>)carrier!)[key] = value);
    }

    /// <summary>
    /// Extracts the remote parent context from inbound headers. <c>traceparent</c> always wins when present
    /// (even if its value is invalid, in which case the result is a new root); the MassTransit
    /// <c>MT-Activity-Id</c> header is consulted only when <c>traceparent</c> is absent.
    /// </summary>
    /// <returns>The parsed parent context, or <see langword="default"/> when none can be determined.</returns>
    internal static ActivityContext ExtractParentContext(IReadOnlyDictionary<string, string> headers)
    {
        DistributedContextPropagator.Current.ExtractTraceIdAndState(
            headers,
            static (carrier, key, out value, out values) =>
            {
                var dict = (IReadOnlyDictionary<string, string>)carrier!;
                if (dict.TryGetValue(key, out var single)
                    || (key == TraceParentHeader && dict.TryGetValue(MassTransitActivityIdHeader, out single)))
                {
                    value = single;
                }
                else
                {
                    value = null;
                }

                values = null;
            },
            out var traceParent,
            out var traceState);

        ActivityContext.TryParse(traceParent, traceState, isRemote: true, out var parentContext);
        return parentContext;
    }

    internal static Activity? ExtractTraceContext(
        IReadOnlyDictionary<string, string> headers,
        string operationName)
        => BareWireActivitySource.StartConsume(
            operationName,
            operationName,
            Guid.Empty,
            ExtractParentContext(headers));
}
