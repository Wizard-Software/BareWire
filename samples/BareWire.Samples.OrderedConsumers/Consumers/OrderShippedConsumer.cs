using BareWire.Abstractions;
using BareWire.Samples.OrderedConsumers.Data;
using BareWire.Samples.OrderedConsumers.Messages;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.OrderedConsumers.Consumers;

/// <summary>
/// Consumes <see cref="OrderShipped"/> messages on the SAC cross-instance endpoint
/// (<c>ordered-processing</c> queue). Persists a <see cref="ProcessedRecord"/> to PostgreSQL
/// for offline ordering verification.
/// </summary>
/// <remarks>
/// <para>
/// Poison-head contract (C3): the producer stamps the header <c>poison-head-demo: true</c> on
/// the seq=0 message of the poison key. This consumer checks for that header and throws, triggering
/// <c>MaxDeliveryAttempts</c>. After the head is parked via DLX, subsequent messages (seq &gt; 0)
/// have no such header and are processed normally — demonstrating key release after parking.
/// Using a transport header instead of a per-instance singleton means the poison detection works
/// correctly across ALL competing replicas without shared in-process state.
/// </para>
/// <para>
/// SEC (S1): the exception message is a constant; it never includes <c>OrderingKey</c> or any
/// value from <see cref="ConsumeContext{T}"/>. exception string is a constant;
/// never include OrderingKey / context.Message.
/// </para>
/// </remarks>
public sealed partial class OrderShippedConsumer(
    ILogger<OrderShippedConsumer> logger,
    OrderedConsumersDbContext dbContext) : IConsumer<OrderShipped>
{
    private static readonly string InstanceId =
        $"{Environment.MachineName}-{Environment.ProcessId}";

    private const string SacEndpointName = "ordered-processing";

    /// <inheritdoc />
    public async Task ConsumeAsync(ConsumeContext<OrderShipped> context)
    {
        OrderShipped message = context.Message;

        // Poison-head gate: the producer stamps "poison-head-demo: true" on seq=0 of the poison key.
        // This check is transport-header-based, so it works on ALL replicas without shared state.
        // SEC (S1): exception string is a constant; never include OrderingKey / context.Message.
        if (context.Headers.TryGetValue("poison-head-demo", out string? poisonFlag)
            && poisonFlag == "true")
        {
            const string poisonHeadMessage =
                "Simulated poison-head failure (seq=0, poison-head-demo header present). " +
                "Ordering-key value is omitted from this message (SEC / S1).";
            LogPoisonHeadThrow(logger, InstanceId);
            throw new InvalidOperationException(poisonHeadMessage);
        }

        LogOrderShippedReceived(logger, message.ShipmentId, message.Sequence, InstanceId);

        string runId = context.Headers.TryGetValue("run-id", out string? rid) ? rid : string.Empty;

        dbContext.ProcessedRecords.Add(new ProcessedRecord
        {
            Key = message.OrderingKey,
            Sequence = message.Sequence,
            MessageType = nameof(OrderShipped),
            ProcessedAtTicks = DateTime.UtcNow.Ticks,
            InstanceId = InstanceId,
            RunId = runId,
            EndpointName = SacEndpointName,
        });

        await dbContext.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Poison-head detected (poison-head-demo header); throwing to trigger MaxDeliveryAttempts. Instance={InstanceId}")]
    private static partial void LogPoisonHeadThrow(ILogger logger, string instanceId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "SAC OrderShipped received: ShipmentId={ShipmentId} Seq={Sequence} Instance={InstanceId}")]
    private static partial void LogOrderShippedReceived(
        ILogger logger, string shipmentId, int sequence, string instanceId);
}

/// <summary>
/// Consumes <see cref="OrderShipped"/> messages on the LocalPartitioned endpoint
/// (<c>local-partitioned-processing</c> queue). Persists a <see cref="ProcessedRecord"/> to
/// PostgreSQL. Records from this consumer are tagged with endpoint "local-partitioned-processing"
/// to distinguish them from the SAC endpoint records in the smoke test.
/// </summary>
/// <remarks>
/// M3 caveat: this consumer uses a typed selector (<c>m => m.AccountId</c>) which is cross-instance-safe
/// only under <see cref="BareWire.Abstractions.Configuration.ConsumerOrderingStrategy.LocalPartitioned"/>
/// or when the selector equals the routing key. Do NOT use a typed selector with
/// <see cref="BareWire.Abstractions.Configuration.ConsumerOrderingStrategy.TransportNative"/> (SAC)
/// across multiple instances.
/// </remarks>
public sealed partial class LocalPartitionedOrderShippedConsumer(
    ILogger<LocalPartitionedOrderShippedConsumer> logger,
    OrderedConsumersDbContext dbContext) : IConsumer<OrderShipped>
{
    private static readonly string InstanceId =
        $"{Environment.MachineName}-{Environment.ProcessId}";

    private const string LpEndpointName = "local-partitioned-processing";

    /// <inheritdoc />
    public async Task ConsumeAsync(ConsumeContext<OrderShipped> context)
    {
        OrderShipped message = context.Message;

        LogOrderShippedReceived(logger, message.ShipmentId, message.Sequence, InstanceId);

        string runId = context.Headers.TryGetValue("run-id", out string? rid) ? rid : string.Empty;

        dbContext.ProcessedRecords.Add(new ProcessedRecord
        {
            Key = message.OrderingKey,
            Sequence = message.Sequence,
            MessageType = nameof(OrderShipped),
            ProcessedAtTicks = DateTime.UtcNow.Ticks,
            InstanceId = InstanceId,
            RunId = runId,
            EndpointName = LpEndpointName,
        });

        await dbContext.SaveChangesAsync(context.CancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "LP OrderShipped received: ShipmentId={ShipmentId} Seq={Sequence} Instance={InstanceId}")]
    private static partial void LogOrderShippedReceived(
        ILogger logger, string shipmentId, int sequence, string instanceId);
}
