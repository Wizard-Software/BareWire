// [modern-csharp loaded] — applied net10.0 / C# 14 conventions
using BareWire.Transport.Google.PubSub;
using BareWire.Transport.Google.PubSub.Configuration;
using Google.Cloud.PubSub.V1;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BareWire.IntegrationTests.Transport;

/// <summary>
/// Helper bramkujący testy integracyjne Google Cloud Pub/Sub za zmienną środowiskową
/// <c>BAREWIRE_PUBSUB_EMULATOR_HOST</c>. Gdy zmienna jest nieobecna, test jest pomijany
/// przez <see cref="Assert.SkipUnless"/> (raportuje status „Skipped", nigdy cicho zielony).
/// Wzorzec analogiczny do <c>SqsTestEnvironment</c> i <c>AzureServiceBusTestEnvironment</c>.
/// </summary>
/// <remarks>
/// SEC-1: klasa ta nigdy nie loguje, nie interpoluje ani nie zapisuje wartości <see cref="EmulatorHost"/>
/// do żadnych komunikatów skip ani wyjść. Komunikaty skip echo'ują wyłącznie NAZWĘ zmiennej.
/// <para>
/// Uwaga: <see cref="EmulatorHost"/> jest endpointem lokalnego emulatora (nie sekretem), więc
/// skip name-only jest ściślejszy niż ściśle konieczne — stosowany dla spójności z ASB/SQS (SEC-1).
/// </para>
/// </remarks>
internal static class PubSubTestEnvironment
{
    internal const string EmulatorHostEnvVar = "BAREWIRE_PUBSUB_EMULATOR_HOST";

    /// <summary>
    /// Zwraca endpoint emulatora Pub/Sub lub <see langword="null"/>, gdy zmienna nie jest ustawiona.
    /// </summary>
    internal static string? EmulatorHost =>
        Environment.GetEnvironmentVariable(EmulatorHostEnvVar);

    /// <summary>
    /// <see langword="true"/> gdy niepusty endpoint emulatora jest dostępny w środowisku.
    /// Deleguje do czystej funkcji <see cref="IsAvailableFor"/> (determinizm w CI — OQ-1).
    /// </summary>
    internal static bool IsAvailable => IsAvailableFor(EmulatorHost);

    /// <summary>
    /// Czysta funkcja logiki bramki — weryfikuje, czy podany endpoint jest niepusty.
    /// Używana w testach broker-free do weryfikacji logiki helpera niezależnie od globalnego stanu
    /// środowiska (OQ-1: determinizm w CI ustawiającym <see cref="EmulatorHostEnvVar"/>).
    /// </summary>
    /// <param name="host">Wartość endpointu do sprawdzenia.</param>
    /// <returns>
    /// <see langword="true"/> gdy <paramref name="host"/> jest niepustym ciągiem niebędącym
    /// białą spacją; <see langword="false"/> w przeciwnym razie.
    /// </returns>
    internal static bool IsAvailableFor(string? host) => !string.IsNullOrWhiteSpace(host);

    /// <summary>
    /// Pomija wywołujący test, gdy endpoint emulatora Pub/Sub nie jest skonfigurowany.
    /// Musi być pierwszą instrukcją każdego testu bramkowanego przez brokera.
    /// </summary>
    internal static void SkipIfUnavailable() =>
        Assert.SkipUnless(
            IsAvailable,
            $"Pominięto: brak zmiennej {EmulatorHostEnvVar} (brak dostępnego emulatora Google Pub/Sub).");

    /// <summary>
    /// Buduje <see cref="PubSubTransportAdapter"/> wskazujący na emulator Pub/Sub wskazany przez
    /// <see cref="EmulatorHost"/>. Opcjonalne wywołanie zwrotne <paramref name="configure"/> może
    /// dalej dostosować konfigurator przed wywołaniem <c>Build()</c>.
    /// </summary>
    /// <param name="projectId">Identyfikator projektu GCP (emulator akceptuje dowolną wartość).</param>
    /// <param name="configure">Opcjonalna dodatkowa konfiguracja stosowana po ustawieniu domyślnych.</param>
    /// <returns>Skonfigurowany, możliwy do usunięcia <see cref="PubSubTransportAdapter"/>.</returns>
    internal static PubSubTransportAdapter CreateAdapter(
        string projectId,
        Action<IPubSubConfigurator>? configure = null)
    {
        var cfg = new PubSubConfigurator();
        cfg.ProjectId(projectId);
        cfg.UseEmulator(EmulatorHost!);
        configure?.Invoke(cfg);
        PubSubTransportOptions options = cfg.Build();
        return new PubSubTransportAdapter(options, NullLogger<PubSubTransportAdapter>.Instance);
    }

    /// <summary>
    /// Usuwa temat Pub/Sub, połykając błędy „not found" (teardown jest no-op, gdy zasób
    /// nigdy nie istniał). SEC: nigdy nie loguje wartości endpointu.
    /// </summary>
    /// <param name="projectId">Identyfikator projektu GCP.</param>
    /// <param name="topicName">Nazwa tematu do usunięcia.</param>
    /// <param name="ct">Token anulowania.</param>
    internal static async Task TryDeleteTopicAsync(
        string projectId,
        string topicName,
        CancellationToken ct)
    {
        var client = new PublisherServiceApiClientBuilder
        {
            Endpoint = EmulatorHost,
            ChannelCredentials = ChannelCredentials.Insecure,
        }.Build();

        try
        {
            await client.DeleteTopicAsync(
                TopicName.FromProjectTopic(projectId, topicName),
                ct).ConfigureAwait(false);
        }
        catch (RpcException rpc) when (rpc.StatusCode == StatusCode.NotFound)
        {
            // Temat nie istniał — teardown jest no-op.
        }
    }

    /// <summary>
    /// Usuwa subskrypcję Pub/Sub, połykając błędy „not found" (teardown jest no-op, gdy zasób
    /// nigdy nie istniał). SEC: nigdy nie loguje wartości endpointu.
    /// </summary>
    /// <param name="projectId">Identyfikator projektu GCP.</param>
    /// <param name="subscriptionName">Nazwa subskrypcji do usunięcia.</param>
    /// <param name="ct">Token anulowania.</param>
    internal static async Task TryDeleteSubscriptionAsync(
        string projectId,
        string subscriptionName,
        CancellationToken ct)
    {
        var client = new SubscriberServiceApiClientBuilder
        {
            Endpoint = EmulatorHost,
            ChannelCredentials = ChannelCredentials.Insecure,
        }.Build();

        try
        {
            await client.DeleteSubscriptionAsync(
                SubscriptionName.FromProjectSubscription(projectId, subscriptionName),
                ct).ConfigureAwait(false);
        }
        catch (RpcException rpc) when (rpc.StatusCode == StatusCode.NotFound)
        {
            // Subskrypcja nie istniała — teardown jest no-op.
        }
    }
}
