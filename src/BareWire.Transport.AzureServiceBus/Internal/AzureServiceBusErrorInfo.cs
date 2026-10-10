using Azure.Messaging.ServiceBus;

namespace BareWire.Transport.AzureServiceBus.Internal;

/// <summary>
/// The parts of an exception that are safe to log: its type and, for Service Bus errors, the failure reason.
/// The exception message is deliberately excluded because SDK messages can echo lock tokens.
/// </summary>
internal readonly record struct AzureServiceBusErrorInfo(string ExceptionType, string FailureReason)
{
    /// <inheritdoc />
    public override string ToString() => $"{ExceptionType} (Reason={FailureReason})";

    /// <summary>Extracts the loggable parts of <paramref name="exception"/>.</summary>
    internal static AzureServiceBusErrorInfo From(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is ServiceBusException serviceBusException
            ? new AzureServiceBusErrorInfo(exception.GetType().Name, serviceBusException.Reason.ToString())
            : new AzureServiceBusErrorInfo(exception.GetType().Name, "none");
    }
}
