using BareWire.Abstractions;
using BareWire.Samples.MassTransitToBareWire.Messages;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.MassTransitToBareWire.Consumers;

/// <summary>
/// BareWire consumer that handles <see cref="CheckInventory"/> requests from MassTransit.
///
/// BareWire's <c>ConsumerInvokerFactory</c> detects the inbound content-type
/// (<c>application/vnd.masstransit+json</c>) and calls <c>TryReadRequestEnvelope</c>
/// on the deserializer to extract <c>requestId</c> and <c>responseAddress</c> from the
/// MT envelope body. These are placed on <c>ConsumeContext.InboundRequestContext</c>.
///
/// <c>RespondAsync</c> then:
/// <list type="number">
/// <item>Finds no AMQP <c>ReplyTo</c> header (MT does not set it for server-named reply queues).</item>
/// <item>Reads <c>responseAddress</c> from the envelope, sanitises it (SEC-1: last path segment only),
///       and sends a MT-format response envelope (echoing <c>requestId</c>) directly to
///       <c>queue://localhost/&lt;replyQueueName&gt;</c> via the AMQP default exchange.</item>
/// </list>
/// </summary>
internal sealed partial class InventoryConsumer : IConsumer<CheckInventory>
{
    private readonly ILogger<InventoryConsumer> _logger;

    public InventoryConsumer(ILogger<InventoryConsumer> logger)
    {
        _logger = logger;
    }

    public async Task ConsumeAsync(ConsumeContext<CheckInventory> context)
    {
        string sku = context.Message.Sku;
        Log.ReceivedRequest(_logger, sku);

        // Simulated inventory lookup — a real consumer would query a database here.
        int available = sku.Equals("SKU-001", StringComparison.OrdinalIgnoreCase) ? 42 : 0;

        await context.RespondAsync(
            new InventoryLevel(Sku: sku, Available: available, ProcessedBy: "BareWire/InventoryConsumer"),
            context.CancellationToken);

        Log.Responded(_logger, sku, available);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information,
            Message = "BareWire received inventory request for SKU={Sku}")]
        internal static partial void ReceivedRequest(ILogger logger, string sku);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "BareWire responded: SKU={Sku}, Available={Available}")]
        internal static partial void Responded(ILogger logger, string sku, int available);
    }
}
