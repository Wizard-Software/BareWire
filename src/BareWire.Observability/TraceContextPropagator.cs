using System.Diagnostics;

namespace BareWire.Observability;

internal sealed class TraceContextPropagator
{
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

    internal static Activity? ExtractTraceContext(
        IReadOnlyDictionary<string, string> headers,
        string operationName)
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

        return BareWireActivitySource.StartConsume(
            operationName,
            operationName,
            Guid.Empty,
            parentContext);
    }
}
