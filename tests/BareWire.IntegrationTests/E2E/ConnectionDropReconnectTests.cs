using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.IntegrationTests.E2E;

/// <summary>Reprezentuje wiadomość używaną w testach reconnect.</summary>
public sealed record ReconnectProbeMessage(string ProbeId, string Phase);

/// <summary>
/// Testy E2E scenariusza zerwania połączenia z brokerem i automatycznego reconnect.
///
/// <para>
/// Weryfikuje, że adapter <see cref="RabbitMqTransportAdapter"/> z włączonym
/// <c>AutomaticRecoveryEnabled = true</c> wznawia konsumpcję nowo opublikowanych wiadomości
/// po wymuszonym zamknięciu wszystkich połączeń po stronie serwera.
/// </para>
///
/// <para>
/// Mechanizm wymuszenia dropu: polecenie <c>rabbitmqctl close_all_connections</c> wykonane
/// przez <c>docker exec</c> w kontenerze RabbitMQ. Jeśli Docker lub kontener są niedostępne,
/// test zostaje pominięty deterministycznie (<see cref="Assert.Skip"/>), nigdy cicho-zielony.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
public sealed class ConnectionDropReconnectTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    // ── Helpery ───────────────────────────────────────────────────────────────

    private RabbitMqTransportAdapter CreateAdapter(bool automaticRecovery = true, TimeSpan? recoveryInterval = null) =>
        new(
            new RabbitMqTransportOptions
            {
                ConnectionString = fixture.GetRabbitMqConnectionString(),
                AutomaticRecoveryEnabled = automaticRecovery,
                NetworkRecoveryInterval = recoveryInterval ?? TimeSpan.FromSeconds(2),
            },
            NullLogger<RabbitMqTransportAdapter>.Instance);

    private static async Task<(string ExchangeName, string QueueName)> DeploySimpleTopologyAsync(
        RabbitMqTransportAdapter adapter,
        string suffix,
        CancellationToken ct)
    {
        string exchangeName = $"e2e-rc-ex-{suffix}";
        string queueName = $"e2e-rc-q-{suffix}";

        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(exchangeName, ExchangeType.Direct, durable: false, autoDelete: false);
        configurator.DeclareQueue(queueName, durable: false, autoDelete: false);
        configurator.BindExchangeToQueue(exchangeName, queueName, routingKey: queueName);
        await adapter.DeployTopologyAsync(configurator.Build(), ct);

        return (exchangeName, queueName);
    }

    private static byte[] SerializeToJson<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value);

    private static T DeserializeFromSequence<T>(ReadOnlySequence<byte> body)
    {
        if (body.IsSingleSegment)
        {
            return JsonSerializer.Deserialize<T>(body.FirstSpan)
                ?? throw new InvalidOperationException($"Nie udało się zdeserializować {typeof(T).Name}.");
        }

        byte[] buffer = new byte[body.Length];
        int offset = 0;
        foreach (ReadOnlyMemory<byte> segment in body)
        {
            segment.Span.CopyTo(buffer.AsSpan(offset));
            offset += segment.Length;
        }

        return JsonSerializer.Deserialize<T>(buffer)
            ?? throw new InvalidOperationException($"Nie udało się zdeserializować {typeof(T).Name}.");
    }

    private static FlowControlOptions StandardFlow() =>
        new() { MaxInFlightMessages = 10, InternalQueueCapacity = 100 };

    private static async Task<InboundMessage> ConsumeOneAsync(
        RabbitMqTransportAdapter adapter,
        string queueName,
        CancellationToken ct)
    {
        await foreach (InboundMessage msg in adapter.ConsumeAsync(queueName, StandardFlow(), ct))
        {
            return msg;
        }

        throw new InvalidOperationException("Strumień konsumpcji zakończył się przed dostarczeniem wiadomości.");
    }

    /// <summary>
    /// Próbuje wymusić zamknięcie wszystkich połączeń RabbitMQ przez <c>docker exec rabbitmqctl</c>.
    /// Zwraca <see langword="true"/> po sukcesie; <see langword="false"/> gdy Docker/kontener są
    /// niedostępne lub polecenie zwróciło niezerowy kod wyjścia.
    /// </summary>
    private static bool TryCloseAllConnections(out string skipReason)
    {
        skipReason = string.Empty;

        // Krok 1: znajdź kontener RabbitMQ po obrazie
        string containerId;
        try
        {
            using var findProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = "ps --filter ancestor=rabbitmq --filter status=running --format {{.ID}}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            findProcess.Start();
            containerId = findProcess.StandardOutput.ReadToEnd().Trim();
            findProcess.WaitForExit(10_000);

            if (findProcess.ExitCode != 0 || string.IsNullOrEmpty(containerId))
            {
                // Spróbuj też filtra po nazwie obrazu z tagiem
                using var findProcess2 = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "docker",
                        Arguments = "ps --filter ancestor=rabbitmq:management --filter status=running --format {{.ID}}",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    },
                };
                findProcess2.Start();
                containerId = findProcess2.StandardOutput.ReadToEnd().Trim();
                findProcess2.WaitForExit(10_000);

                if (string.IsNullOrEmpty(containerId))
                {
                    skipReason =
                        "Nie znaleziono uruchomionego kontenera RabbitMQ przez 'docker ps' " +
                        "(filtr 'rabbitmq' i 'rabbitmq:management'). " +
                        "Test reconnect wymaga Dockera z kontenerem RabbitMQ — pomijamy deterministycznie.";
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            skipReason =
                $"Docker niedostępny lub zwrócił wyjątek podczas 'docker ps': {ex.GetType().Name}: {ex.Message}. " +
                "Test reconnect wymaga Dockera z kontenerem RabbitMQ — pomijamy deterministycznie.";
            return false;
        }

        // Weź tylko pierwszy ID (może być kilka linii jeśli wiele kontenerów)
        containerId = containerId.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();

        // Krok 2: wykonaj rabbitmqctl close_all_connections
        try
        {
            using var execProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"exec {containerId} rabbitmqctl close_all_connections \"test-reconnect-drop\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            execProcess.Start();
            execProcess.WaitForExit(15_000);

            if (execProcess.ExitCode != 0)
            {
                string stderr = execProcess.StandardError.ReadToEnd();
                skipReason =
                    $"'rabbitmqctl close_all_connections' zwrócił kod {execProcess.ExitCode}: {stderr}. " +
                    "Nie można wymusić dropu połączenia adaptera — pomijamy deterministycznie.";
                return false;
            }
        }
        catch (Exception ex)
        {
            skipReason =
                $"Wyjątek podczas 'docker exec rabbitmqctl close_all_connections': " +
                $"{ex.GetType().Name}: {ex.Message}. " +
                "Test reconnect wymaga dostępu do kontenera — pomijamy deterministycznie.";
            return false;
        }

        return true;
    }

    // ── E2E: Drop połączenia i automatyczny reconnect ─────────────────────────

    /// <summary>
    /// Weryfikuje, że adapter RabbitMQ z włączonym <c>AutomaticRecoveryEnabled = true</c>
    /// automatycznie wznawia konsumpcję nowych wiadomości po wymuszonym zamknięciu wszystkich
    /// połączeń po stronie serwera (<c>rabbitmqctl close_all_connections</c>).
    ///
    /// <para>
    /// Fazy testu:
    /// <list type="number">
    ///   <item>Publikacja i konsumpcja pierwszej wiadomości (weryfikacja baseline przed dropem).</item>
    ///   <item>
    ///     Wymuszenie zamknięcia połączeń przez <c>docker exec rabbitmqctl close_all_connections</c>.
    ///     Jeśli Docker lub kontener są niedostępne, test jest pomijany z jawnym powodem.
    ///   </item>
    ///   <item>
    ///     Publikacja nowej wiadomości po dropie; polling do momentu jej odebrania lub upływu
    ///     timeout — behawioralna weryfikacja reconnect (bez nasłuchu zdarzenia recovery).
    ///   </item>
    /// </list>
    /// </para>
    /// </summary>
    [Fact]
    public async Task ConnectionDrop_AdapterWithAutoRecovery_ResumesConsumptionAfterReconnect()
    {
        // Arrange — 30 s: obejmuje fazę baseline + drop + oczekiwanie na reconnect (NetworkRecoveryInterval=2s)
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));

        // Adapter pod testem: auto-recovery włączone, krótki interwał (2s) dla deterministyczności
        await using RabbitMqTransportAdapter adapter = CreateAdapter(
            automaticRecovery: true,
            recoveryInterval: TimeSpan.FromSeconds(2));

        string suffix = Guid.NewGuid().ToString("N");
        (string exchangeName, string queueName) = await DeploySimpleTopologyAsync(adapter, suffix, cts.Token);

        // ── Faza 1: baseline — wiadomość przed dropem ─────────────────────────

        byte[] phase1Body = SerializeToJson(new ReconnectProbeMessage(
            ProbeId: $"PROBE-{suffix[..8].ToUpperInvariant()}",
            Phase: "before-drop"));

        OutboundMessage phase1Outbound = new(
            routingKey: queueName,
            headers: new Dictionary<string, string>
            {
                ["BW-Exchange"] = exchangeName,
                ["X-Phase"] = "before-drop",
            },
            body: phase1Body,
            contentType: "application/json");

        await adapter.SendBatchAsync([phase1Outbound], cts.Token);
        InboundMessage phase1Received = await ConsumeOneAsync(adapter, queueName, cts.Token);

        // Weryfikacja baseline
        ReconnectProbeMessage phase1Msg = DeserializeFromSequence<ReconnectProbeMessage>(phase1Received.Body);
        phase1Msg.Phase.Should().Be("before-drop",
            because: "wiadomość baseline musi dotrzeć przed wymuszonym dropem");

        await adapter.SettleAsync(SettlementAction.Ack, phase1Received, cts.Token);
        phase1Received.Dispose();

        // ── Faza 2: wymuszony drop połączenia ─────────────────────────────────

        // GAP-1: adapter._connection jest prywatny — nie możemy go zamknąć bezpośrednio.
        // Jedyna opcja: rabbitmqctl close_all_connections przez docker exec.
        // Jeśli niedostępne → deterministyczny Skip, nigdy cicho-zielony.
        if (!TryCloseAllConnections(out string skipReason))
        {
            Assert.Skip(skipReason);
            return;
        }

        // ── Faza 3: wiadomość po dropie — behawioralna weryfikacja reconnect ──

        // Poczekaj chwilę, by drop zdążył dotrzeć do adaptera
        await Task.Delay(TimeSpan.FromMilliseconds(500), cts.Token);

        byte[] phase2Body = SerializeToJson(new ReconnectProbeMessage(
            ProbeId: $"PROBE-{suffix[..8].ToUpperInvariant()}",
            Phase: "after-reconnect"));

        OutboundMessage phase2Outbound = new(
            routingKey: queueName,
            headers: new Dictionary<string, string>
            {
                ["BW-Exchange"] = exchangeName,
                ["X-Phase"] = "after-reconnect",
            },
            body: phase2Body,
            contentType: "application/json");

        // Publish może wymagać kilku prób w trakcie reconnect
        bool published = false;
        for (int attempt = 0; attempt < 5 && !published; attempt++)
        {
            try
            {
                IReadOnlyList<SendResult> results = await adapter.SendBatchAsync([phase2Outbound], cts.Token);
                if (results.Count > 0 && results[0].IsConfirmed)
                {
                    published = true;
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
                }
            }
            catch (Exception)
            {
                // Adapter w trakcie reconnect — poczekaj i spróbuj ponownie
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            }
        }

        published.Should().BeTrue(
            because: "adapter z włączonym auto-recovery musi móc opublikować wiadomość po reconnect");

        // Pollinguj aż wiadomość po reconnect dotrze
        InboundMessage phase2Received = await ConsumeOneAsync(adapter, queueName, cts.Token);

        try
        {
            ReconnectProbeMessage phase2Msg = DeserializeFromSequence<ReconnectProbeMessage>(phase2Received.Body);
            phase2Msg.Phase.Should().Be("after-reconnect",
                because: "adapter z włączonym auto-recovery musi wznowić konsumpcję nowych wiadomości " +
                         "po automatycznym reconnect do brokera RabbitMQ");
        }
        finally
        {
            await adapter.SettleAsync(SettlementAction.Ack, phase2Received, cts.Token);
            phase2Received.Dispose();
        }
    }
}
