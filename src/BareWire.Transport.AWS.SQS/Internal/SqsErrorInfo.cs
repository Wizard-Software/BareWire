using Amazon.Runtime;

namespace BareWire.Transport.AWS.SQS.Internal;

/// <summary>
/// The parts of an exception that are safe to log: its type and the AWS error code and HTTP status.
/// The exception message is deliberately excluded because SDK messages can echo receipt handles.
/// </summary>
internal readonly record struct SqsErrorInfo(string ExceptionType, string ErrorCode, int StatusCode)
{
    /// <summary>Extracts the loggable parts of <paramref name="exception"/>.</summary>
    internal static SqsErrorInfo From(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is AmazonServiceException aws
            ? new SqsErrorInfo(exception.GetType().Name, aws.ErrorCode ?? "none", (int)aws.StatusCode)
            : new SqsErrorInfo(exception.GetType().Name, "none", 0);
    }
}
