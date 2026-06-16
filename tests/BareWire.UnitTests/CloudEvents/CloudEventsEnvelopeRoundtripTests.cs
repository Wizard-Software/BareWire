using System.Buffers;
using System.Text.Json;

using AwesomeAssertions;

using BareWire.CloudEvents;

namespace BareWire.UnitTests.CloudEvents;

/// <summary>
/// Test integracyjny (jednostkowy) weryfikujący dwukierunkową kompatybilność formatu
/// koperty CloudEvents structured między <see cref="CloudEventsEnvelopeSerializer"/> (13.8)
/// i <see cref="CloudEventsEnvelopeDeserializer"/> (13.9): serializacja wiadomości z kompletem
/// atrybutów CE → odczyt → asercje na <c>data</c> oraz wszystkich atrybutach kontekstu
/// (<c>id</c>, <c>source</c>, <c>type</c>, <c>subject</c>, <c>time</c>).
/// </summary>
/// <remarks>
/// <para>
/// Ustalenie projektowe (load-bearing): <see cref="CloudEventsEnvelopeDeserializer"/> zwraca
/// WYŁĄCZNIE payload <c>data</c> jako <c>T?</c> i celowo NIE udostępnia odczytanego
/// <see cref="CloudEventContext"/> (GAP-2 — budowa kontekstu należy do 13.11). Dlatego asercje
/// na atrybutach kontekstu odczytujemy bezpośrednio z wyprodukowanej koperty JSON przez
/// <see cref="JsonDocument"/> (kierunek forward: serializer → JSON), a payload <c>data</c>
/// weryfikujemy przez SUT deserializer (kierunek backward: JSON → obiekt).
/// </para>
/// </remarks>
public sealed class CloudEventsEnvelopeRoundtripTests
{
    // -------------------------------------------------------------------------
    // Test message payload type (wzorzec współdzielony z testami serializacji 13.8/13.9)
    // -------------------------------------------------------------------------

    private sealed record TestMessage(string Name, int Value);

    // -------------------------------------------------------------------------
    // Helpers — komplet i minimum atrybutów CE (wzorzec z 13.8/13.9)
    // -------------------------------------------------------------------------

    private static readonly DateTimeOffset SampleTime =
        new(2024, 3, 15, 10, 30, 0, TimeSpan.FromHours(2));

    private static CloudEventContext MandatoryAttributes() => new(
        id: "test-id-001",
        source: new Uri("https://example.com/myapp"),
        type: "com.example.order.created");

    private static CloudEventContext FullAttributes() => new(
        id: "full-id-002",
        source: new Uri("https://example.com/myapp"),
        type: "com.example.order.created",
        specVersion: "1.0",
        subject: "order/42",
        time: SampleTime,
        dataContentType: "application/json",
        dataSchema: new Uri("https://schemas.example.com/v1/order.json"),
        extensions: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["traceparent"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            ["partitionkey"] = "tenant-42",
        });

    /// <summary>
    /// Serializuje <paramref name="message"/> z atrybutami <paramref name="attributes"/> przez
    /// <see cref="CloudEventsEnvelopeSerializer"/> i zwraca surowe bajty koperty.
    /// </summary>
    private static byte[] SerializeEnvelope(ICloudEventAttributes attributes, TestMessage message)
    {
        var serializer = new CloudEventsEnvelopeSerializer(attributes);
        var buffer = new ArrayBufferWriter<byte>();
        serializer.Serialize(message, buffer);
        return buffer.WrittenMemory.ToArray();
    }

    private static TestMessage? DeserializeData(byte[] envelopeBytes)
    {
        var deserializer = new CloudEventsEnvelopeDeserializer();
        return deserializer.Deserialize<TestMessage>(new ReadOnlySequence<byte>(envelopeBytes));
    }

    // -------------------------------------------------------------------------
    // Test 1: pełny roundtrip — data + WSZYSTKIE atrybuty kontekstu zachowane
    // -------------------------------------------------------------------------

    [Fact]
    public void Roundtrip_FullAttributes_PreservesDataAndAllContextAttributes()
    {
        CloudEventContext attributes = FullAttributes();
        var message = new TestMessage("Widget", 42);

        byte[] envelope = SerializeEnvelope(attributes, message);

        // --- backward: JSON koperty → payload przez SUT deserializer ---
        TestMessage? data = DeserializeData(envelope);

        data.Should().NotBeNull();
        data!.Name.Should().Be("Widget");
        data.Value.Should().Be(42);

        // --- forward: serializer → JSON koperty; asercje na atrybutach kontekstu ---
        using JsonDocument doc = JsonDocument.Parse(envelope);
        JsonElement root = doc.RootElement;

        root.GetProperty("specversion").GetString().Should().Be(attributes.SpecVersion);
        root.GetProperty("id").GetString().Should().Be(attributes.Id);
        root.GetProperty("source").GetString().Should().Be(attributes.Source.ToString());
        root.GetProperty("type").GetString().Should().Be(attributes.Type);
        root.GetProperty("subject").GetString().Should().Be(attributes.Subject);

        // time porównujemy jako DateTimeOffset (R1: unikamy kruchości na formatowanie/strefę).
        string? timeStr = root.GetProperty("time").GetString();
        timeStr.Should().NotBeNullOrEmpty();
        DateTimeOffset.TryParse(timeStr, out DateTimeOffset roundtrippedTime).Should().BeTrue();
        roundtrippedTime.ToUniversalTime().Should().Be(SampleTime.ToUniversalTime());

        root.GetProperty("datacontenttype").GetString().Should().Be(attributes.DataContentType);
        root.GetProperty("dataschema").GetString().Should().Be(attributes.DataSchema!.ToString());

        // extensions na poziomie root koperty
        root.GetProperty("traceparent").GetString().Should().Be(attributes.Extensions["traceparent"]);
        root.GetProperty("partitionkey").GetString().Should().Be(attributes.Extensions["partitionkey"]);

        // data jest zagnieżdżonym obiektem JSON (nie string/base64), camelCase (JsonSerializerDefaults.Web).
        JsonElement dataElement = root.GetProperty("data");
        dataElement.ValueKind.Should().Be(JsonValueKind.Object);
        dataElement.GetProperty("name").GetString().Should().Be("Widget");
        dataElement.GetProperty("value").GetInt32().Should().Be(42);
    }

    // -------------------------------------------------------------------------
    // Test 2: roundtrip minimum obowiązkowy — opcjonalne atrybuty NIEOBECNE, data zachowane
    // -------------------------------------------------------------------------

    [Fact]
    public void Roundtrip_MandatoryOnly_PreservesDataAndOmitsOptionalAttributes()
    {
        CloudEventContext attributes = MandatoryAttributes();
        var message = new TestMessage("Bolt", 7);

        byte[] envelope = SerializeEnvelope(attributes, message);

        // backward: payload odzyskany poprawnie
        TestMessage? data = DeserializeData(envelope);

        data.Should().NotBeNull();
        data!.Name.Should().Be("Bolt");
        data.Value.Should().Be(7);

        // forward: atrybuty obowiązkowe obecne
        using JsonDocument doc = JsonDocument.Parse(envelope);
        JsonElement root = doc.RootElement;

        root.GetProperty("specversion").GetString().Should().Be("1.0");
        root.GetProperty("id").GetString().Should().Be(attributes.Id);
        root.GetProperty("source").GetString().Should().Be(attributes.Source.ToString());
        root.GetProperty("type").GetString().Should().Be(attributes.Type);

        // opcjonalne atrybuty muszą być NIEOBECNE w kopercie
        root.TryGetProperty("subject", out _).Should().BeFalse();
        root.TryGetProperty("time", out _).Should().BeFalse();
        root.TryGetProperty("datacontenttype", out _).Should().BeFalse();
        root.TryGetProperty("dataschema", out _).Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // Test 3: symetria payloadu data przez parę serializer → deserializer (record equality)
    // -------------------------------------------------------------------------

    [Fact]
    public void Roundtrip_DataPayload_SymmetricThroughSerializerAndDeserializer()
    {
        var original = new TestMessage("Sprocket", 99);

        byte[] envelope = SerializeEnvelope(FullAttributes(), original);
        TestMessage? roundtripped = DeserializeData(envelope);

        roundtripped.Should().NotBeNull();
        // record equality — payload odtworzony 1:1 (Name + Value) po pełnym roundtrip.
        roundtripped.Should().Be(original);
    }
}
