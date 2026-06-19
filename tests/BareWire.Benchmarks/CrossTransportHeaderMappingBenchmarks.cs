using Amazon.SQS.Model;
using Azure.Messaging.ServiceBus;
using BareWire.Transport.AzureServiceBus;
using BareWire.Transport.AWS.SQS;
using BareWire.Transport.Google.PubSub;
using BareWire.Transport.Kafka;
using BareWire.Transport.RabbitMQ;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Confluent.Kafka;

namespace BareWire.Benchmarks;

/// <summary>
/// Porównuje narzut adaptera BareWire na deterministycznej powierzchni
/// <c>*HeaderMapper.MapOutbound</c> dla wszystkich pięciu transportów
/// (zero I/O — brak połączeń z brokerami).
///
/// <para>
/// <b>Metryka główna:</b> kolumna <c>Allocated</c> (B/op) z
/// <see cref="MemoryDiagnoserAttribute"/>. Kolumna <c>Mean</c> (ns/op)
/// pełni rolę wskaźnika przepustowości (proxy throughput, R-2).
/// </para>
///
/// <para>
/// <b>Baseline:</b> <see cref="MapOutbound_RabbitMq"/> alokuje krotkę + dwie
/// kolekcje (<c>BasicProperties</c> + <c>Dictionary</c>), więc <c>Ratio &lt; 1.0</c>
/// dla lżejszych transportów odzwierciedla różnice modelu obiektowego SDK,
/// nie nieefektywność adaptera (R-1). Kolumna <c>Allocated</c> jest metryką
/// nadrzędną nad <c>Ratio</c>.
/// </para>
///
/// <para>
/// <b>Azure Service Bus</b> raportowany jest osobno w kategorii
/// <c>AsbMutateInPlace</c> — patrz <see cref="MapOutbound_AzureServiceBus"/> (D-2).
/// </para>
///
/// <para>
/// Benchmark jest narzędziem diagnostycznym — CI nie bramkuje merge'a
/// na podstawie jego wyników (konwencja identyczna jak
/// <see cref="CloudEventsBinaryBenchmarks"/>).
/// </para>
/// </summary>
[MemoryDiagnoser(displayGenColumns: true)]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class CrossTransportHeaderMappingBenchmarks
{
    /// <summary>Wspólny, read-only słownik nagłówków kanonicznych BareWire
    /// współdzielony przez wszystkie metody benchmarku. Budowany raz
    /// w <see cref="GlobalSetup"/> — nie wlicza się do mierzonej alokacji.</summary>
    private IReadOnlyDictionary<string, string> _headers = null!;

    /// <summary>Instancja mappera RabbitMQ (jedyna klasa niebędąca statyczną).</summary>
    private RabbitMqHeaderMapper _rabbitMapper = null!;

    /// <summary>Wspólna wiadomość ASB reużywana między iteracjami benchmarku (D-2).</summary>
    private ServiceBusMessage _asbMessage = null!;

    /// <summary>
    /// Buduje wspólny zestaw wejściowych nagłówków kanonicznych BareWire
    /// oraz inicjalizuje stan specyficzny dla transportu.
    /// Wywoływana raz przed całym zestawem pomiarów.
    ///
    /// <para>Liczba nagłówków wynosi 9 — bezpieczna dla limitu 10 atrybutów SQS
    /// (4 nagłówki passthrough + 5 kanonicznych; R-7).</para>
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        _headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["message-id"]    = "550e8400-e29b-41d4-a716-446655440000",
            ["correlation-id"] = "7c9e6679-7425-40de-944b-e07fc1f90ae7",
            ["content-type"]  = "application/json",
            ["BW-MessageType"] = "OrderCreated",
            ["traceparent"]   = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            ["x-tenant-id"]   = "acme-corp",
            ["x-environment"] = "production",
            ["x-region"]      = "eu-west-1",
            ["x-version"]     = "2",
        };

        _rabbitMapper = new RabbitMqHeaderMapper(config: null);
        _asbMessage   = new ServiceBusMessage();
    }

    /// <summary>
    /// Mapowanie nagłówków BareWire do właściwości AMQP RabbitMQ
    /// (<see cref="RabbitMQ.Client.BasicProperties"/> + słownik nagłówków AMQP).
    /// Punkt odniesienia (baseline) dla tabeli <c>Ratio</c> — transporty w kategorii
    /// <c>CrossTransport</c> (R-1).
    /// </summary>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("CrossTransport")]
    public (RabbitMQ.Client.BasicProperties Props, Dictionary<string, object?> Headers) MapOutbound_RabbitMq() =>
        _rabbitMapper.MapOutbound(_headers);

    /// <summary>
    /// Mapowanie nagłówków BareWire do <see cref="Confluent.Kafka.Headers"/> Kafka.
    /// Wszystkie wartości kodowane jako UTF-8.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("CrossTransport")]
    public Headers MapOutbound_Kafka() =>
        KafkaHeaderMapper.MapOutbound(_headers);

    /// <summary>
    /// Mapowanie nagłówków BareWire do słownika
    /// <see cref="MessageAttributeValue"/> SQS.
    /// Liczba nagłówków nieprzekraczająca 10 — brak wyjątku BareWireTransportException.
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("CrossTransport")]
    public Dictionary<string, MessageAttributeValue> MapOutbound_Sqs() =>
        SqsHeaderMapper.MapOutbound(_headers);

    /// <summary>
    /// Mapowanie nagłówków BareWire do słownika atrybutów Google Pub/Sub
    /// (<c>PubsubMessage.Attributes</c>).
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("CrossTransport")]
    public Dictionary<string, string> MapOutbound_PubSub() =>
        PubSubHeaderMapper.MapOutbound(_headers);

    /// <summary>
    /// Mapowanie nagłówków BareWire do <see cref="ServiceBusMessage.ApplicationProperties"/>
    /// przez mutację in-place istniejącej wiadomości (<c>static void</c>).
    ///
    /// <para>
    /// <b>Steady-state mutate-in-place (D-2):</b> Po pierwszej iteracji klucze już istnieją
    /// w słowniku <c>ApplicationProperties</c>; kolejne iteracje nadpisują istniejące sloty
    /// (string = typ referencyjny, brak boxingu, brak resize) — alokacja dąży do zera
    /// w steady-state. Z tego powodu ten benchmark jest alokacyjnie nieporównywalny
    /// z transportami zwracającymi świeży obiekt i raportowany jest <b>osobno</b>,
    /// poza kolumną <c>Ratio</c> grupy <c>CrossTransport</c> (D-2/R-3).
    /// </para>
    ///
    /// <para>
    /// Wiadomość <c>_asbMessage</c> NIE jest resetowana w regionie timowanym — patrz D-2.
    /// </para>
    /// </summary>
    [Benchmark]
    [BenchmarkCategory("AsbMutateInPlace")]
    public void MapOutbound_AzureServiceBus() =>
        AzureServiceBusHeaderMapper.MapOutbound(_headers, _asbMessage);
}
