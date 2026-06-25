using BareWire.Abstractions;
using BareWire.Samples.OrderedConsumers.Data;
using BareWire.Samples.OrderedConsumers.Messages;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.OrderedConsumers.Consumers;

/// <summary>
/// Consumes <see cref="InventoryAdjusted"/> messages on the SAC cross-instance endpoint
/// (<c>ordered-processing</c> queue). Persists a <see cref="ProcessedRecord"/> to PostgreSQL
/// for offline ordering verification.
/// </summary>
public sealed partial class InventoryAdjustedConsumer(
    ILogger<InventoryAdjustedConsumer> logger,
    OrderedConsumersDbContext dbContext) : IConsumer<InventoryAdjusted>
{
    private static readonly string InstanceId =
        $"{Environment.MachineName}-{Environment.ProcessId}";

    private const string EndpointName = "ordered-processing";

    /// <inheritdoc />
    public async Task ConsumeAsync(ConsumeContext<InventoryAdjusted> context)
    {
        InventoryAdjusted message = context.Message;

        LogInventoryAdjustedReceived(logger, message.AdjustmentId, message.Sequence, InstanceId);

        string runId = context.Headers.TryGetValue("run-id", out string? rid) ? rid : string.Empty;

        dbContext.ProcessedRecords.Add(new ProcessedRecord
        {
            Key = message.OrderingKey,
            Sequence = message.Sequence,
            MessageType = nameof(InventoryAdjusted),
            ProcessedAtTicks = DateTime.UtcNow.Ticks,
            InstanceId = InstanceId,
            RunId = runId,
            EndpointName = EndpointName,
        });

        await dbContext.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "InventoryAdjusted received: AdjustmentId={AdjustmentId} Seq={Sequence} Instance={InstanceId}")]
    private static partial void LogInventoryAdjustedReceived(
        ILogger logger, string adjustmentId, int sequence, string instanceId);
}
