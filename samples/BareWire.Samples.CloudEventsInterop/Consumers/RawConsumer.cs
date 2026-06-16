// RawConsumer — demonstruje odczyt po stronie trybu raw (ADR-001).
//
// PublishAsync() publikuje czysty JSON bez nagłówków ce-* i bez koperty CloudEvents.
// GetCloudEvent() zwraca null — brak metadanych CloudEvents jest oczekiwany i poprawny
// dla wiadomości opublikowanych w trybie raw (ADR-001 raw-first).
//
// Ten konsument potwierdza, że fanout dostarcza KAŻDĄ wiadomość (w tym raw JSON)
// do wszystkich podpiętych kolejek — różnica jest widoczna dopiero po stronie ODCZYTU.

using BareWire.Abstractions;
using BareWire.CloudEvents;
using BareWire.Samples.CloudEventsInterop.Messages;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.CloudEventsInterop.Consumers;

/// <summary>
/// Konsument demonstrujący odczyt czystej wiadomości JSON bez metadanych CloudEvents.
/// Potwierdza kontrakt ADR-001: <c>PublishAsync</c> nie dodaje nagłówków <c>ce-*</c>
/// ani koperty — <see cref="CloudEventContextExtensions.GetCloudEvent"/> zwraca <see langword="null"/>.
/// </summary>
public sealed partial class RawConsumer(ILogger<RawConsumer> logger)
    : IConsumer<ShipmentDispatched>
{
    /// <inheritdoc />
    public Task ConsumeAsync(ConsumeContext<ShipmentDispatched> context)
    {
        ShipmentDispatched msg = context.Message;

        // GetCloudEvent() zwraca null — brak nagłówków ce-* w czystym raw JSON (ADR-001).
        bool hasCeMetadata = context.GetCloudEvent() is not null;

        LogRawMessage(logger, msg.ShipmentId, msg.Destination, msg.Carrier, hasCeMetadata);

        return Task.CompletedTask;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "[Raw] czysty JSON (ADR-001): shipmentId={ShipmentId} destination={Destination} carrier={Carrier} | metadane CE={HasCeMetadata} (oczekiwane: false — brak metadanych CloudEvents, raw JSON)")]
    private static partial void LogRawMessage(
        ILogger logger, string shipmentId, string destination, string carrier, bool hasCeMetadata);
}
