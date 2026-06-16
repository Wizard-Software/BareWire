// BinaryAwareConsumer — demonstruje odczyt po stronie binarnego trybu CloudEvents.
//
// Fanout exchange dostarcza KAŻDĄ wiadomość do tej kolejki — niezależnie od trybu publikacji
// (binary / structured / raw). Konsument korzysta z GetCloudEvent() jako bezpiecznego guardu:
// gdy nagłówki ce-* są obecne (publikacja binary), odczytuje atrybuty CloudEvents 1.0
// przez GetCloudEventOrThrow(); gdy ich brak (structured lub raw), loguje informację
// o braku metadanych CE bez rzucania wyjątku (kontrakt „null, nigdy throw").

using BareWire.Abstractions;
using BareWire.CloudEvents;
using BareWire.Samples.CloudEventsInterop.Messages;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.CloudEventsInterop.Consumers;

/// <summary>
/// Konsument demonstrujący odczyt atrybutów CloudEvents 1.0 zakodowanych w nagłówkach ce-*.
/// Widoczny efekt trybu binarnego: <see cref="CloudEventContextExtensions.GetCloudEvent"/>
/// zwraca niepuste atrybuty tylko dla wiadomości opublikowanych przez
/// <c>PublishCloudEventAsync</c> (tryb binary).
/// </summary>
public sealed partial class BinaryAwareConsumer(ILogger<BinaryAwareConsumer> logger)
    : IConsumer<ShipmentDispatched>
{
    /// <inheritdoc />
    public Task ConsumeAsync(ConsumeContext<ShipmentDispatched> context)
    {
        // GetCloudEvent() jest bezpiecznym guardem: zwraca null, nigdy nie rzuca.
        // Chroni konsumenta przed wiadomościami innych trybów dostarczonymi przez fanout.
        ICloudEventAttributes? ce = context.GetCloudEvent();

        if (ce is not null)
        {
            // Nagłówki ce-* obecne — wiadomość opublikowana w trybie binarnym.
            // GetCloudEventOrThrow() egzekwuje pełną zgodność z CE 1.0 (specVersion == "1.0").
            ICloudEventAttributes validated = context.GetCloudEventOrThrow();
            LogBinaryCloudEvent(
                logger,
                validated.Id,
                validated.Source,
                validated.Type,
                context.Message.ShipmentId);
        }
        else
        {
            // Brak nagłówków ce-* — wiadomość opublikowana w trybie structured lub raw
            // i dostarczona przez fanout do tej kolejki.
            LogNoCeHeaders(logger, context.Message.ShipmentId);
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "[BinaryAware] CE binary: id={CeId} source={CeSource} type={CeType} shipmentId={ShipmentId}")]
    private static partial void LogBinaryCloudEvent(
        ILogger logger, string ceId, Uri ceSource, string ceType, string shipmentId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "[BinaryAware] brak atrybutów CE — wiadomość innego trybu dostarczona przez fanout; shipmentId={ShipmentId}")]
    private static partial void LogNoCeHeaders(ILogger logger, string shipmentId);
}
