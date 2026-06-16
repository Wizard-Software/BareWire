using System.Collections.Concurrent;

namespace BareWire.Samples.CloudEventsInterop.Services;

/// <summary>
/// Pojedynczy wpis potwierdzenia odbioru wiadomości przez konkretnego konsumenta.
/// Pola CE (<see cref="CeId"/>, <see cref="CeSource"/>, <see cref="CeType"/>) są wypełniane
/// wyłącznie dla wiadomości niosących nagłówki <c>ce-*</c> (tryb binarny CloudEvents) —
/// dla trybów structured i raw pozostają <see langword="null"/>.
/// </summary>
public sealed record ShipmentReceipt(
    string ShipmentId,
    string Consumer,
    bool HasCloudEventAttributes,
    string? CeId,
    string? CeSource,
    string? CeType,
    DateTimeOffset ReceivedAt);

/// <summary>
/// Wątkowo-bezpieczny rejestr potwierdzeń odbioru — zasilany przez trzy konsumenty
/// (<c>BinaryAware</c>, <c>Structured</c>, <c>Raw</c>) i odczytywany przez endpoint
/// <c>GET /shipments/processed</c>. Służy do weryfikacji E2E: jeden broadcast (fanout)
/// trafia do wszystkich trzech kolejek, a różnica trybów jest widoczna po stronie odczytu.
/// Rejestrowany jako singleton w DI; konsumenty (transient) wstrzykują go bez współdzielonego stanu statycznego.
/// </summary>
public sealed class ShipmentReceiptStore
{
    private readonly ConcurrentQueue<ShipmentReceipt> _receipts = [];

    public void Add(ShipmentReceipt receipt) => _receipts.Enqueue(receipt);

    public IReadOnlyList<ShipmentReceipt> GetAll() => [.. _receipts];
}
