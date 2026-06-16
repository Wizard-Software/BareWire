using System.Buffers;
using System.Net.Mime;
using System.Text.Json;

using AwesomeAssertions;

using BareWire.CloudEvents;

using CloudNative.CloudEvents;
using CloudNative.CloudEvents.SystemTextJson;

namespace BareWire.UnitTests.CloudEvents;

/// <summary>
/// Testy zgodności (conformance) implementacji CloudEvents BareWire z oficjalnym SDK CNCF
/// <c>CloudNative.CloudEvents</c> 2.8.0 (oracle test-only — ADR-007 / NetArchTest 13.14
/// strzeże, że SDK nie trafia do assemblies produkcyjnych).
/// </summary>
/// <remarks>
/// <para>
/// Zadanie 13.15 realizuje konsekwencję ADR-007 §Weryfikacja: własna implementacja
/// koperty structured (<see cref="CloudEventsEnvelopeSerializer"/>, <see cref="CloudEventsEnvelopeDeserializer"/>)
/// i mappera nagłówków binary (<see cref="CloudEventBinaryHeaderMapper"/>) musi być
/// czytelna przez niezależny, zgodny ze specyfikacją CE 1.0.2 konsument.
/// </para>
/// <para>
/// Strategia oracle per tryb:
/// <list type="bullet">
/// <item><description>
/// <b>Structured forward</b> — nasza koperta → <c>JsonEventFormatter.DecodeStructuredModeMessage</c> SDK.
/// </description></item>
/// <item><description>
/// <b>Structured backward</b> — koperta SDK → nasz <c>CloudEventsEnvelopeDeserializer</c> (payload <c>data</c>).
/// </description></item>
/// <item><description>
/// <b>Binary round-trip</b> — <c>ToHeaders</c> → <c>TryFromHeaders</c>; asercja równości atrybutów.
/// </description></item>
/// <item><description>
/// <b>Binary konformacja nazw</b> — klucze <c>ce-*</c> zgodne z konwencją binary-mode CE 1.0.
/// </description></item>
/// </list>
/// </para>
/// <para>
/// Uwaga (OQ-3 / GAP-1): <see cref="CloudEventsEnvelopeDeserializer"/> czyta pole <c>data</c>
/// z <c>JsonSerializerDefaults.Web</c> (camelCase). Dlatego w teście backward obiekt <c>Data</c>
/// przekazywany do SDK jest anonimowym obiektem z polami camelCase (<c>name</c>/<c>value</c>),
/// aby uniknąć fałszywej porażki asercji wynikającej z różnicy konwencji nazewnictwa JSON
/// (nie jest to niezgodność CE 1.0.2).
/// </para>
/// <para>
/// Uwaga dot. etykiety <c>[integration]</c>: testy korzystają z SDK jako oracle w pamięci
/// (in-memory interop) — nie wymagają infrastruktury zewnętrznej (Docker/RabbitMQ/Aspire).
/// </para>
/// </remarks>
public sealed class CloudEventsConformanceTests
{
    // -------------------------------------------------------------------------
    // Typ payloadu testowego (wzorzec z CloudEventsEnvelopeRoundtripTests, 13.13)
    // -------------------------------------------------------------------------

    private sealed record TestMessage(string Name, int Value);

    // -------------------------------------------------------------------------
    // Dane testowe — czas i atrybuty CE
    // -------------------------------------------------------------------------

    private static readonly DateTimeOffset SampleTime =
        new(2024, 3, 15, 10, 30, 0, TimeSpan.FromHours(2));

    /// <summary>Zwraca minimalne (wymagane) atrybuty CE 1.0.</summary>
    private static CloudEventContext MandatoryAttributes() => new(
        id: "conformance-id-001",
        source: new Uri("https://example.com/barewire"),
        type: "com.example.order.created");

    /// <summary>Zwraca pełny zestaw atrybutów CE 1.0 (wymagane + opcjonalne).</summary>
    private static CloudEventContext FullAttributes() => new(
        id: "conformance-full-002",
        source: new Uri("https://example.com/barewire"),
        type: "com.example.order.created",
        specVersion: "1.0",
        subject: "order/99",
        time: SampleTime,
        dataContentType: "application/json",
        dataSchema: new Uri("https://schemas.example.com/v1/order.json"));

    // -------------------------------------------------------------------------
    // Pomocnicy serializacji / deserializacji BareWire
    // -------------------------------------------------------------------------

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
    // Test 1: structured forward — nasza koperta czytelna przez SDK CNCF
    // -------------------------------------------------------------------------

    /// <summary>
    /// Weryfikuje, że koperta structured wyprodukowana przez <see cref="CloudEventsEnvelopeSerializer"/>
    /// jest poprawnie odczytywana przez <c>JsonEventFormatter</c> SDK CNCF (oracle CE 1.0.2).
    /// Asercje obejmują wszystkie atrybuty CE oraz pole <c>data</c>.
    /// </summary>
    [Fact]
    public void Structured_EnvelopeProducedByBareWire_IsReadableByCloudNativeSdk()
    {
        // Arrange
        CloudEventContext attributes = FullAttributes();
        var message = new TestMessage("Widget", 42);
        byte[] envelopeBytes = SerializeEnvelope(attributes, message);

        // Act — oracle SDK dekoduje koperty wyprodukowane przez BareWire
        var formatter = new JsonEventFormatter();
        CloudEvent ce = formatter.DecodeStructuredModeMessage(
            new ReadOnlyMemory<byte>(envelopeBytes),
            new ContentType("application/cloudevents+json"),
            extensionAttributes: null);

        // Assert — atrybuty CE zgodne z oryginałem
        ce.Should().NotBeNull();
        ce.SpecVersion.Should().Be(CloudEventsSpecVersion.V1_0);
        ce.Id.Should().Be(attributes.Id);
        ce.Source.Should().Be(attributes.Source);
        ce.Type.Should().Be(attributes.Type);
        ce.Subject.Should().Be(attributes.Subject);

        // Czas porównujemy w UTC — unikamy kruchości na reprezentację strefy czasowej (plan §5 krok 4).
        ce.Time.Should().NotBeNull();
        ce.Time!.Value.ToUniversalTime().Should().Be(SampleTime.ToUniversalTime());

        ce.DataContentType.Should().Be(attributes.DataContentType);
        ce.DataSchema.Should().Be(attributes.DataSchema);

        // Assert — pole data zawiera payload camelCase (JsonSerializerDefaults.Web)
        ce.Data.Should().NotBeNull();
        JsonElement dataElement = (JsonElement)ce.Data!;
        dataElement.ValueKind.Should().Be(JsonValueKind.Object);
        dataElement.GetProperty("name").GetString().Should().Be("Widget");
        dataElement.GetProperty("value").GetInt32().Should().Be(42);
    }

    // -------------------------------------------------------------------------
    // Test 2: structured backward — koperta SDK czytelna przez nasz deserializer
    // -------------------------------------------------------------------------

    /// <summary>
    /// Weryfikuje, że koperta structured zakodowana przez SDK CNCF (<c>EncodeStructuredModeMessage</c>)
    /// jest poprawnie odczytywana przez <see cref="CloudEventsEnvelopeDeserializer"/> BareWire.
    /// Asercje dotyczą payloadu <c>data</c> (GAP-2: deserializer celowo nie zwraca kontekstu).
    /// </summary>
    /// <remarks>
    /// OQ-3 / GAP-1: obiekt <c>Data</c> przekazywany do SDK ma pola camelCase (<c>name</c>/<c>value</c>)
    /// jako anonimowy obiekt, dzięki czemu <c>JsonSerializerDefaults.Web</c> naszego deserializera
    /// poprawnie mapuje je na rekord <see cref="TestMessage"/>. Jest to różnica konwencji nazewnictwa JSON,
    /// nie niezgodność CE 1.0.2.
    /// </remarks>
    [Fact]
    public void Structured_EnvelopeProducedByCloudNativeSdk_IsReadableByBareWire()
    {
        // Arrange — budujemy CloudEvent SDK z obiektem Data o polach camelCase (OQ-3)
        var sdkEvent = new CloudEvent(CloudEventsSpecVersion.V1_0)
        {
            Id = "sdk-id-003",
            Source = new Uri("https://sdk.example.com/producer"),
            Type = "com.example.sdk.test",
            Subject = "sdk-subject/1",
            Time = SampleTime,
            DataContentType = "application/json",
            // Dane camelCase — zgodne z JsonSerializerDefaults.Web naszego deserializera (OQ-3 / GAP-1)
            Data = new { name = "Bolt", value = 7 },
        };

        var formatter = new JsonEventFormatter();
        ReadOnlyMemory<byte> sdkBytes = formatter.EncodeStructuredModeMessage(sdkEvent, out _);

        // Act — nasz deserializer czyta koperty zakodowaną przez SDK
        TestMessage? result = DeserializeData(sdkBytes.ToArray());

        // Assert — payload odczytany poprawnie
        result.Should().NotBeNull();
        result!.Name.Should().Be("Bolt");
        result.Value.Should().Be(7);
    }

    // -------------------------------------------------------------------------
    // Test 3: binary round-trip — mapper zachowuje wszystkie atrybuty
    // -------------------------------------------------------------------------

    /// <summary>
    /// Weryfikuje, że round-trip nagłówków <c>ce-*</c> przez
    /// <see cref="CloudEventBinaryHeaderMapper.ToHeaders"/> →
    /// <see cref="CloudEventBinaryHeaderMapper.TryFromHeaders"/> zachowuje wszystkie atrybuty CE.
    /// </summary>
    [Fact]
    public void Binary_HeadersRoundtripThroughMapper_PreservesAllAttributes()
    {
        // Arrange
        CloudEventContext original = FullAttributes();

        // Act
        IDictionary<string, string> headers = CloudEventBinaryHeaderMapper.ToHeaders(original);
        bool parsed = CloudEventBinaryHeaderMapper.TryFromHeaders(
            (IReadOnlyDictionary<string, string>)headers,
            out ICloudEventAttributes? result);

        // Assert
        parsed.Should().BeTrue();
        result.Should().NotBeNull();
        result!.Id.Should().Be(original.Id);
        result.Source.Should().Be(original.Source);
        result.Type.Should().Be(original.Type);
        result.SpecVersion.Should().Be(original.SpecVersion);
        result.Subject.Should().Be(original.Subject);

        // Czas porównujemy w UTC — unikamy kruchości na offset (wzorzec z 13.13)
        result.Time.Should().NotBeNull();
        result.Time!.Value.ToUniversalTime().Should().Be(SampleTime.ToUniversalTime());

        result.DataContentType.Should().Be(original.DataContentType);
        result.DataSchema.Should().Be(original.DataSchema);
    }

    // -------------------------------------------------------------------------
    // Test 4: binary konformacja nazw — klucze ce-* zgodne z CE 1.0 binary-mode
    // -------------------------------------------------------------------------

    /// <summary>
    /// Weryfikuje, że nazwy nagłówków produkowane przez <see cref="CloudEventBinaryHeaderMapper.ToHeaders"/>
    /// są dokładnie kanonicznymi nazwami binary-mode CE 1.0 z prefiksem <c>ce-</c> małymi literami.
    /// Dowód czytelności dla HTTP-binding konsumenta CE (np. Knative).
    /// </summary>
    [Fact]
    public void Binary_HeaderNames_MatchCloudEventsBinaryModeConvention()
    {
        // Arrange & Act
        IDictionary<string, string> mandatoryHeaders =
            CloudEventBinaryHeaderMapper.ToHeaders(MandatoryAttributes());

        IDictionary<string, string> fullHeaders =
            CloudEventBinaryHeaderMapper.ToHeaders(FullAttributes());

        // Assert — mandatory: dokładnie 4 kanoniczne klucze ce-*
        mandatoryHeaders.Keys.Should().BeEquivalentTo(new[]
        {
            CloudEventBinaryHeaderMapper.HeaderId,         // "ce-id"
            CloudEventBinaryHeaderMapper.HeaderSource,     // "ce-source"
            CloudEventBinaryHeaderMapper.HeaderSpecVersion,// "ce-specversion"
            CloudEventBinaryHeaderMapper.HeaderType,       // "ce-type"
        });

        // Assert — full: zawiera dodatkowo opcjonalne atrybuty
        fullHeaders.Keys.Should().Contain(CloudEventBinaryHeaderMapper.HeaderSubject);          // "ce-subject"
        fullHeaders.Keys.Should().Contain(CloudEventBinaryHeaderMapper.HeaderTime);             // "ce-time"
        fullHeaders.Keys.Should().Contain(CloudEventBinaryHeaderMapper.HeaderDataContentType);  // "ce-datacontenttype"
        fullHeaders.Keys.Should().Contain(CloudEventBinaryHeaderMapper.HeaderDataSchema);       // "ce-dataschema"

        // Assert — wszystkie klucze zaczynają się prefiksem "ce-" małymi literami
        foreach (string key in fullHeaders.Keys)
        {
            key.Should().StartWith(CloudEventBinaryHeaderMapper.HeaderPrefix,
                because: $"klucz nagłówka '{key}' musi stosować prefiks ce- binary-mode CE 1.0");
        }
    }
}
