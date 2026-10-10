using Grpc.Core;

namespace BareWire.Transport.Google.PubSub.Internal;

/// <summary>
/// The parts of an exception that are safe to log: its type and, for gRPC failures, the status code.
/// The exception message is deliberately excluded because SDK messages can echo ack ids.
/// </summary>
internal readonly record struct PubSubErrorInfo(string ExceptionType, string StatusCode)
{
    /// <summary>Extracts the loggable parts of <paramref name="exception"/>.</summary>
    internal static PubSubErrorInfo From(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is RpcException rpc
            ? new PubSubErrorInfo(exception.GetType().Name, rpc.StatusCode.ToString())
            : new PubSubErrorInfo(exception.GetType().Name, "none");
    }
}
