using BareWire.Abstractions;
using BareWire.Samples.BackpressureDemo.Messages;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.BackpressureDemo.Consumers;

/// <summary>
/// Deliberately slow consumer that processes <see cref="LoadTestMessage"/> with a 100 ms delay.
/// This artificial latency causes the consumer to fall behind a high-rate publisher, which
/// triggers ADR-004 credit-based flow control: the in-flight counter approaches
/// <c>FlowControlOptions.MaxInFlightMessages</c> and the broker stops prefetching new messages.
/// </summary>
/// <remarks>
/// Resolved from DI as transient (per-message dispatch). Keep stateless.
/// </remarks>
public sealed partial class SlowConsumer(ILogger<SlowConsumer> logger) : IConsumer<LoadTestMessage>
{
    /// <inheritdoc />
    public async Task ConsumeAsync(ConsumeContext<LoadTestMessage> context)
    {
        // Simulate slow processing — 100 ms per message deliberately saturates
        // the consumer and demonstrates back-pressure on the publisher side.
        await Task.Delay(100, context.CancellationToken).ConfigureAwait(false);

        if (context.Message.SequenceNumber % 100 == 0)
        {
            LogMessageProcessed(logger, context.Message.SequenceNumber, context.Message.CreatedAt);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Processed message #{SequenceNumber} (created at {CreatedAt:O})")]
    private static partial void LogMessageProcessed(
        ILogger logger, int sequenceNumber, DateTime createdAt);
}
