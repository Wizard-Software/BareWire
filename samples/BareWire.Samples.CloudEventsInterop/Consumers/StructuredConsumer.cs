// StructuredConsumer — demonstruje odczyt po stronie trybu structured CloudEvents.
//
// AddCloudEventsEnvelope() dekoruje IDeserializerResolver routerem Content-Type.
// Dla wiadomości z Content-Type: application/cloudevents+json router kieruje je do
// CloudEventsEnvelopeDeserializer, który rozpakowuje kopertę i deserializuje pole
// "data" do ShipmentDispatched — zanim wiadomość dotrze do ConsumeAsync.
//
// WAŻNE: w trybie structured atrybuty CE są wewnątrz koperty JSON ("id", "source" itp.),
// a NIE w nagłówkach transportowych ce-*. Dlatego GetCloudEvent() zwraca tu null —
// to jest oczekiwane i prawidłowe zachowanie dla wiadomości structured-mode.

using BareWire.Abstractions;
using BareWire.CloudEvents;
using BareWire.Samples.CloudEventsInterop.Messages;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.CloudEventsInterop.Consumers;

/// <summary>
/// Konsument demonstrujący odczyt wiadomości rozpakowanej z koperty CloudEvents structured-mode.
/// Router Content-Type (zarejestrowany przez <c>AddCloudEventsEnvelope()</c>) wypakowuje pole
/// <c>data</c> przed przekazaniem wiadomości do konsumenta — <see cref="ConsumeContext{T}.Message"/>
/// zawiera gotowy obiekt <see cref="ShipmentDispatched"/>.
/// </summary>
public sealed partial class StructuredConsumer(ILogger<StructuredConsumer> logger)
    : IConsumer<ShipmentDispatched>
{
    /// <inheritdoc />
    public Task ConsumeAsync(ConsumeContext<ShipmentDispatched> context)
    {
        ShipmentDispatched msg = context.Message;

        // Wiadomość jest już wypakowana z koperty application/cloudevents+json przez router.
        // W trybie structured atrybuty CE (id, source, type) żyją w kopercie JSON,
        // nie w nagłówkach transportowych ce-*. GetCloudEvent() czyta właśnie nagłówki ce-*,
        // więc poprawnie zwróci null — to oczekiwane zachowanie structured-mode.
        bool hasCeHeaders = context.GetCloudEvent() is not null;

        LogStructuredMessage(logger, msg.ShipmentId, msg.Destination, msg.Carrier, hasCeHeaders);

        return Task.CompletedTask;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "[Structured] koperta rozpakowana: shipmentId={ShipmentId} destination={Destination} carrier={Carrier} | ce-* w nagłówkach={HasCeHeaders} (oczekiwane: false — atrybuty CE są w kopercie, nie w nagłówkach)")]
    private static partial void LogStructuredMessage(
        ILogger logger, string shipmentId, string destination, string carrier, bool hasCeHeaders);
}
