using System.Diagnostics;
using System.Reflection;

namespace BareWire.Observability;

internal sealed class BareWireActivitySource
{
    internal static readonly ActivitySource Source = new(
        "BareWire",
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0");

    internal static Activity? StartPublish(string messageType, string destination, Guid messageId)
    {
        var activity = Source.StartActivity(
            $"{messageType} publish",
            ActivityKind.Producer);

        activity?.SetTag("messaging.system", "barewire");
        activity?.SetTag("messaging.destination", destination);
        activity?.SetTag("messaging.message.id", messageId.ToString());

        return activity;
    }

    internal static Activity? StartConsume(
        string messageType,
        string endpoint,
        Guid messageId,
        ActivityContext parentContext)
    {
        var activity = Source.StartActivity(
            $"{messageType} process",
            ActivityKind.Consumer,
            parentContext);

        activity?.SetTag("messaging.system", "barewire");
        activity?.SetTag("messaging.destination", endpoint);
        activity?.SetTag("messaging.consumer.id", endpoint);
        activity?.SetTag("messaging.message.id", messageId.ToString());

        return activity;
    }

    internal static Activity? StartSagaTransition(
        string sagaType,
        string stateFrom,
        string stateTo,
        Guid correlationId)
    {
        var activity = Source.StartActivity(
            $"{sagaType}.{stateTo} transition",
            ActivityKind.Internal);

        activity?.SetTag("messaging.system", "barewire");
        activity?.SetTag("saga.correlation_id", correlationId.ToString());
        activity?.SetTag("saga.state_from", stateFrom);
        activity?.SetTag("saga.state_to", stateTo);

        return activity;
    }

    internal static Activity? StartSettle(string endpoint)
    {
        var activity = Source.StartActivity(
            $"{endpoint} settle",
            ActivityKind.Internal);

        activity?.SetTag("messaging.system", "barewire");
        activity?.SetTag("messaging.destination", endpoint);
        activity?.SetTag("messaging.settlement.action", "settle");

        return activity;
    }
}
