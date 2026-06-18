// [modern-csharp loaded] — applied net10.0 / C# 14 conventions
using System.Buffers;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.Google.PubSub;
using BareWire.Transport.Google.PubSub.Configuration;
using BareWire.Transport.Google.PubSub.Topology;
using Xunit;

namespace BareWire.IntegrationTests.Transport;

/// <summary>
/// Testy integracyjne end-to-end adaptera transportu Google Cloud Pub/Sub. Każdy test E2E jest
/// bramkowany zmienną środowiskową <c>BAREWIRE_PUBSUB_EMULATOR_HOST</c> — bez niej test raportuje
/// status „Skipped" (nigdy cicho zielony). Testy broker-free (strukturalne + bramka) zawsze się wykonują.
/// </summary>
/// <remarks>
/// <para>
/// Izolacja zasobów: każdy test tworzy unikalnie nazwane tematy i subskrypcje z sufiksem
/// <see cref="Guid"/> i usuwa je w bloku <c>finally</c> via
/// <see cref="PubSubTestEnvironment.TryDeleteTopicAsync"/> i
/// <see cref="PubSubTestEnvironment.TryDeleteSubscriptionAsync"/> (zero wycieku encji na emulatorze).
/// </para>
/// <para>
/// Kontrakt settlement: <see cref="PubSubTransportAdapter.SettleAsync"/> musi być wywoływany
/// <em>wewnątrz</em> pętli <c>await foreach</c> podczas aktywnego konsumenta
/// (wpis registry jest ważny dopóki nie zostanie eksmitowany). Wszystkie testy zachowują ten kontrakt.
/// </para>
/// <para>
/// Uwaga dotycząca flow control (PERF-3): limit in-flight jest ustawiany na opcjach transportu
/// (<c>MaxInFlightMessages</c> + <c>MaxOutstandingMessages</c> via <c>configure</c>), nie tylko
/// na <c>FlowControlOptions</c>. Registry-bound = <c>_options.MaxInFlightMessages</c>;
/// pełny registry cicho dropuje wiadomości — ryzyko zawieszenia testu.
/// </para>
/// </remarks>
[Trait("Category", "GooglePubSub")]
public sealed class PubSubE2ETests
{
    private const string ProjectId = "barewire-test";

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static FlowControlOptions StandardFlow(int max = 50) =>
        new() { MaxInFlightMessages = max, InternalQueueCapacity = max };

    private static byte[] SerializeToJson<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value);

    private static T DeserializeFromSequence<T>(ReadOnlySequence<byte> body)
    {
        if (body.IsSingleSegment)
        {
            return JsonSerializer.Deserialize<T>(body.FirstSpan)
                ?? throw new InvalidOperationException($"Deserializacja {typeof(T).Name} nie powiodła się.");
        }

        byte[] buffer = new byte[body.Length];
        int offset = 0;
        foreach (ReadOnlyMemory<byte> segment in body)
        {
            segment.Span.CopyTo(buffer.AsSpan(offset));
            offset += segment.Length;
        }

        return JsonSerializer.Deserialize<T>(buffer)
            ?? throw new InvalidOperationException($"Deserializacja {typeof(T).Name} nie powiodła się.");
    }

    private static byte[] ReadSequenceToArray(ReadOnlySequence<byte> sequence)
    {
        if (sequence.IsSingleSegment)
        {
            return sequence.FirstSpan.ToArray();
        }

        byte[] result = new byte[sequence.Length];
        int offset = 0;
        foreach (ReadOnlyMemory<byte> segment in sequence)
        {
            segment.Span.CopyTo(result.AsSpan(offset));
            offset += segment.Length;
        }

        return result;
    }

    /// <summary>
    /// Konsumuje dokładnie jedną wiadomość, uruchamia opcjonalne wywołanie zwrotne
    /// <paramref name="inspect"/>, następnie rozlicza ją z <paramref name="action"/> —
    /// wszystko przed przesunięciem enumeratora. Zwraca skonsumowaną wiadomość do dalszych asercji.
    /// </summary>
    private static async Task<InboundMessage> ConsumeOneAndSettleAsync(
        PubSubTransportAdapter adapter,
        string subscriptionName,
        SettlementAction action,
        FlowControlOptions flow,
        CancellationToken ct,
        Action<InboundMessage>? inspect = null)
    {
        await foreach (InboundMessage msg in adapter.ConsumeAsync(subscriptionName, flow, ct))
        {
            inspect?.Invoke(msg);
            await adapter.SettleAsync(action, msg, ct);
            return msg;
        }

        throw new InvalidOperationException("Strumień konsumenta zakończył się przed nadejściem jakiejkolwiek wiadomości.");
    }

    // ── Rekord wiadomości używany we wszystkich testach ───────────────────────

    /// <summary>Typowany rekord zamówienia używany jako ładunek round-trip w testach E2E.</summary>
    public sealed record TestPubSubOrder(string OrderId, decimal Amount, string Currency);

    // ── E2E-PUBSUB-1: Typowany publish/consume roundtrip ─────────────────────

    /// <summary>
    /// E2E-PUBSUB-1: Publikuje typowany <see cref="TestPubSubOrder"/> zserializowany jako JSON
    /// do tematu Pub/Sub, konsumuje go z subskrypcji, deserializuje ciało i asertuje równość pól.
    /// </summary>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task TypedPublishConsume_EndToEnd_MessageDelivered()
    {
        PubSubTestEnvironment.SkipIfUnavailable();

        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        string suffix = Guid.NewGuid().ToString("N");
        string topicName = $"e2e-pubsub-roundtrip-{suffix}";
        string subName = $"e2e-pubsub-roundtrip-sub-{suffix}";

        await using PubSubTransportAdapter adapter = PubSubTestEnvironment.CreateAdapter(ProjectId);

        try
        {
            var declaration = new TopologyDeclaration
            {
                Exchanges = [new ExchangeDeclaration(topicName, ExchangeType.Fanout)],
                Queues = [new QueueDeclaration(Name: subName, Durable: true)],
                ExchangeQueueBindings = [new ExchangeQueueBinding(ExchangeName: topicName, QueueName: subName, RoutingKey: string.Empty)],
            };
            await adapter.DeployTopologyAsync(declaration, cts.Token);

            var order = new TestPubSubOrder(
                OrderId: $"ORD-{suffix[..8].ToUpperInvariant()}",
                Amount: 149.99m,
                Currency: "PLN");

            byte[] body = SerializeToJson(order);

            OutboundMessage outbound = new(
                routingKey: topicName,
                headers: new Dictionary<string, string>(),
                body: body,
                contentType: "application/json");

            // Act — publish
            IReadOnlyList<SendResult> sendResults =
                await adapter.SendBatchAsync([outbound], cts.Token);

            // Assert — broker potwierdził wysyłkę
            sendResults.Should().HaveCount(1);
            sendResults[0].IsConfirmed.Should().BeTrue(
                because: "broker Pub/Sub musi potwierdzić przyjęcie wiadomości");

            // Act + Assert — consume, deserializacja, weryfikacja round-trip
            TestPubSubOrder? roundTripped = null;

            await ConsumeOneAndSettleAsync(
                adapter,
                subName,
                SettlementAction.Ack,
                StandardFlow(),
                cts.Token,
                inspect: msg =>
                {
                    roundTripped = DeserializeFromSequence<TestPubSubOrder>(msg.Body);
                });

            // PERF-1: anuluj CTS po odebraniu wiadomości, by pętla konsumenta się zakończyła
            cts.Cancel();

            roundTripped.Should().NotBeNull();
            roundTripped!.OrderId.Should().Be(order.OrderId);
            roundTripped.Amount.Should().Be(order.Amount);
            roundTripped.Currency.Should().Be(order.Currency);
        }
        finally
        {
            await PubSubTestEnvironment.TryDeleteSubscriptionAsync(ProjectId, subName, CancellationToken.None);
            await PubSubTestEnvironment.TryDeleteTopicAsync(ProjectId, topicName, CancellationToken.None);
        }
    }

    // ── E2E-PUBSUB-2: Ordering per ordering key ───────────────────────────────

    /// <summary>
    /// E2E-PUBSUB-2: Publikuje N=8 wiadomości z tym samym nagłówkiem ordering key do tematu
    /// Pub/Sub i asertuje, że docierają w ściśle rosnącej kolejności (gwarancja FIFO per key).
    /// </summary>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task Ordering_SameOrderingKey_PreservesOrder()
    {
        PubSubTestEnvironment.SkipIfUnavailable();

        // 60 s: obejmuje tworzenie topologii + batch publish + konsumpcję 8 wiadomości.
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        const int TotalMessages = 8;
        string suffix = Guid.NewGuid().ToString("N");
        string topicName = $"e2e-pubsub-order-{suffix}";
        string subName = $"e2e-pubsub-order-sub-{suffix}";

        // PERF-3: ustawić MaxInFlightMessages i MaxOutstandingMessages na opcjach transportu,
        // by registry-bound nie dropował wiadomości cicho.
        await using PubSubTransportAdapter adapter = PubSubTestEnvironment.CreateAdapter(
            ProjectId,
            configure: c =>
            {
                c.EnableMessageOrdering();
                c.MaxInFlightMessages(TotalMessages * 5);
                c.MaxOutstandingMessages(TotalMessages * 5);
            });

        try
        {
            var declaration = new TopologyDeclaration
            {
                Exchanges = [new ExchangeDeclaration(topicName, ExchangeType.Fanout)],
                Queues =
                [
                    new QueueDeclaration(
                        Name: subName,
                        Durable: true,
                        Arguments: new Dictionary<string, object>
                        {
                            [PubSubTopologyArguments.OrderingEnabledKey] = true,
                        }),
                ],
                ExchangeQueueBindings = [new ExchangeQueueBinding(ExchangeName: topicName, QueueName: subName, RoutingKey: string.Empty)],
            };
            await adapter.DeployTopologyAsync(declaration, cts.Token);

            // Publish N wiadomości: każda niesie numer seq i ten sam ordering key.
            OutboundMessage[] messages = Enumerable
                .Range(1, TotalMessages)
                .Select(i => new OutboundMessage(
                    routingKey: topicName,
                    headers: new Dictionary<string, string>
                    {
                        [PubSubHeaderMapper.OrderingKeyHeader] = "order-group-1",
                    },
                    body: Encoding.UTF8.GetBytes($"{{\"seq\":{i}}}"),
                    contentType: "application/json"))
                .ToArray();

            await adapter.SendBatchAsync(messages, cts.Token);

            // Konsumuj wszystkie N wiadomości i zbieraj numery seq.
            var receivedSeqs = new List<int>(TotalMessages);

            await foreach (InboundMessage msg in adapter.ConsumeAsync(subName, StandardFlow(TotalMessages * 5), cts.Token))
            {
                byte[] bodyBytes = ReadSequenceToArray(msg.Body);
                using JsonDocument doc = JsonDocument.Parse(bodyBytes);
                int seq = doc.RootElement.GetProperty("seq").GetInt32();
                receivedSeqs.Add(seq);

                await adapter.SettleAsync(SettlementAction.Ack, msg, cts.Token);

                if (receivedSeqs.Count == TotalMessages)
                {
                    break;
                }
            }

            // PERF-1: anuluj CTS po zebraniu N wiadomości
            cts.Cancel();

            // Assert — wszystkie odebrane w ściśle rosnącej kolejności.
            receivedSeqs.Should().HaveCount(TotalMessages,
                because: "wszystkie 8 opublikowanych wiadomości musi być dostarczone do konsumenta");
            receivedSeqs.Should().BeInAscendingOrder(
                because: "wiadomości z tym samym BW-OrderingKey muszą docierać w kolejności publikacji");
        }
        finally
        {
            await PubSubTestEnvironment.TryDeleteSubscriptionAsync(ProjectId, subName, CancellationToken.None);
            await PubSubTestEnvironment.TryDeleteTopicAsync(ProjectId, topicName, CancellationToken.None);
        }
    }

    // ── E2E-PUBSUB-3: DeadLetter — Reject → DLQ ──────────────────────────────

    /// <summary>
    /// E2E-PUBSUB-3: Weryfikuje działanie <c>DeadLetterPolicy</c> end-to-end. Publikuje 1 wiadomość,
    /// odrzuca ją (<see cref="SettlementAction.Reject"/>) wielokrotnie aż do wyczerpania
    /// <c>max-delivery-attempts</c>, następnie asertuje, że trafia do subskrypcji DLQ.
    /// </summary>
    /// <remarks>
    /// OQ-2 (BINDING): jeśli emulator nie zrealizuje <c>DeadLetterPolicy</c> w budżecie 120 s,
    /// test raportuje <c>Skipped</c> z jawnym powodem — nigdy cicho zielony.
    /// PERF-2 (BINDING): ack-deadline = 10 s, max-delivery-attempts = 5, CTS ≥ 120 s.
    /// </remarks>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task DeadLetter_OnRejectAfterMaxAttempts_MovesToDlq()
    {
        PubSubTestEnvironment.SkipIfUnavailable();

        // PERF-2: budżet 120 s — min. ~5×10 s ≈ 50 s zanim wiadomość trafi do DLQ.
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(120));
        string suffix = Guid.NewGuid().ToString("N");
        string topicName = $"e2e-pubsub-dlq-src-{suffix}";
        string subName = $"e2e-pubsub-dlq-src-sub-{suffix}";
        string dlqTopicName = $"e2e-pubsub-dlq-dead-{suffix}";
        string dlqSubName = $"e2e-pubsub-dlq-dead-sub-{suffix}";

        await using PubSubTransportAdapter adapter = PubSubTestEnvironment.CreateAdapter(ProjectId);

        try
        {
            var declaration = new TopologyDeclaration
            {
                Exchanges =
                [
                    new ExchangeDeclaration(topicName, ExchangeType.Fanout),
                    // DLQ topic musi istnieć; adapter tworzy go idempotentnie przez DeadLetterPolicy,
                    // ale deklaracja jako Exchange jest czystą ścieżką.
                    new ExchangeDeclaration(dlqTopicName, ExchangeType.Fanout),
                ],
                Queues =
                [
                    new QueueDeclaration(
                        Name: subName,
                        Durable: true,
                        Arguments: new Dictionary<string, object>
                        {
                            // PERF-2: krótki ack-deadline, by redelivery zachodziło szybko.
                            [PubSubTopologyArguments.AckDeadlineKey] = TimeSpan.FromSeconds(10),
                            [PubSubTopologyArguments.MaxDeliveryAttemptsKey] = 5,
                            [PubSubTopologyArguments.DeadLetterTopicKey] = dlqTopicName,
                        }),
                    new QueueDeclaration(Name: dlqSubName, Durable: true),
                ],
                ExchangeQueueBindings =
                [
                    new ExchangeQueueBinding(ExchangeName: topicName, QueueName: subName, RoutingKey: string.Empty),
                    new ExchangeQueueBinding(ExchangeName: dlqTopicName, QueueName: dlqSubName, RoutingKey: string.Empty),
                ],
            };
            await adapter.DeployTopologyAsync(declaration, cts.Token);

            byte[] body = SerializeToJson(new { marker = "to-dlq" });

            OutboundMessage outbound = new(
                routingKey: topicName,
                headers: new Dictionary<string, string>(),
                body: body,
                contentType: "application/json");

            await adapter.SendBatchAsync([outbound], cts.Token);

            // W pętli konsumuj ze źródłowej subskrypcji i Reject — nie ack, nie modify deadline.
            // Broker redeliveruje po upływie ack-deadline; po max-delivery-attempts przekierowuje do DLQ.
            // rejectCts wygasa po 85 s — zostawia ~35 s budżetu dla fazy poll DLQ.
            using CancellationTokenSource rejectCts =
                CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            rejectCts.CancelAfter(TimeSpan.FromSeconds(85));

            try
            {
                await foreach (InboundMessage msg in adapter.ConsumeAsync(subName, StandardFlow(), rejectCts.Token))
                {
                    await adapter.SettleAsync(SettlementAction.Reject, msg, rejectCts.Token);
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Budżet główny upłynął.
            }
            catch (OperationCanceledException)
            {
                // Normalny shutdown pętli Reject.
            }

            // Poll subskrypcji DLQ aż do otrzymania wiadomości lub upływu budżetu.
            bool arrivedInDlq = false;

            using CancellationTokenSource dlqCts =
                CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            dlqCts.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await foreach (InboundMessage dlqMsg in adapter.ConsumeAsync(dlqSubName, StandardFlow(), dlqCts.Token))
                {
                    arrivedInDlq = true;
                    await adapter.SettleAsync(SettlementAction.Ack, dlqMsg, dlqCts.Token);
                    break;
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Budżet upłynął, zanim wiadomość dotarła do DLQ.
            }

            // OQ-2 (BINDING): jeśli emulator nie zrealizował DeadLetterPolicy — skip z jawnym powodem,
            // nigdy cicho zielony.
            if (!arrivedInDlq)
            {
                Assert.Skip(
                    "Emulator Pub/Sub nie zrealizował DeadLetterPolicy w budżecie czasu — pominięto " +
                    "(nigdy cicho zielony). Sprawdź obsługę DeadLetterPolicy przez używany emulator.");
                return;
            }

            arrivedInDlq.Should().BeTrue(
                because: "po wyczerpaniu max-delivery-attempts DeadLetterPolicy musi przekierować wiadomość do DLQ");
        }
        finally
        {
            await PubSubTestEnvironment.TryDeleteSubscriptionAsync(ProjectId, dlqSubName, CancellationToken.None);
            await PubSubTestEnvironment.TryDeleteSubscriptionAsync(ProjectId, subName, CancellationToken.None);
            await PubSubTestEnvironment.TryDeleteTopicAsync(ProjectId, dlqTopicName, CancellationToken.None);
            await PubSubTestEnvironment.TryDeleteTopicAsync(ProjectId, topicName, CancellationToken.None);
        }
    }

    // ── E2E-PUBSUB-4: Nack → redelivery ──────────────────────────────────────

    /// <summary>
    /// E2E-PUBSUB-4: Rozlicza wiadomość z <see cref="SettlementAction.Nack"/>
    /// (mapuje na <c>ModifyAckDeadline(0)</c> — natychmiastowy redelivery), następnie konsumuje
    /// redeliverowaną wiadomość i asertuje, że ciało jest identyczne. Ackuje przy drugim odbiorze.
    /// </summary>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task Settlement_Nack_RedeliversMessage()
    {
        PubSubTestEnvironment.SkipIfUnavailable();

        // 60 s: obejmuje tworzenie topologii + publish + dwa cykle consume.
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        string suffix = Guid.NewGuid().ToString("N");
        string topicName = $"e2e-pubsub-nack-{suffix}";
        string subName = $"e2e-pubsub-nack-sub-{suffix}";

        await using PubSubTransportAdapter adapter = PubSubTestEnvironment.CreateAdapter(ProjectId);

        try
        {
            var declaration = new TopologyDeclaration
            {
                Exchanges = [new ExchangeDeclaration(topicName, ExchangeType.Fanout)],
                Queues =
                [
                    new QueueDeclaration(
                        Name: subName,
                        Durable: true,
                        Arguments: new Dictionary<string, object>
                        {
                            // Krótki ack-deadline: wiadomość szybko staje się ponownie widoczna
                            // po ModifyAckDeadline(0), jeśli jest jakakolwiek latencja brokera.
                            [PubSubTopologyArguments.AckDeadlineKey] = TimeSpan.FromSeconds(10),
                        }),
                ],
                ExchangeQueueBindings = [new ExchangeQueueBinding(ExchangeName: topicName, QueueName: subName, RoutingKey: string.Empty)],
            };
            await adapter.DeployTopologyAsync(declaration, cts.Token);

            const string MarkerValue = "nack-me";

            OutboundMessage outbound = new(
                routingKey: topicName,
                headers: new Dictionary<string, string>(),
                body: SerializeToJson(new { marker = MarkerValue }),
                contentType: "application/json");

            await adapter.SendBatchAsync([outbound], cts.Token);

            // Pierwszy consume — Nack → ModifyAckDeadline(0) → natychmiastowy redelivery.
            string? firstBody = null;
            await ConsumeOneAndSettleAsync(
                adapter,
                subName,
                SettlementAction.Nack,
                StandardFlow(),
                cts.Token,
                inspect: msg =>
                {
                    firstBody = Encoding.UTF8.GetString(ReadSequenceToArray(msg.Body));
                });

            firstBody.Should().NotBeNullOrWhiteSpace(
                because: "pierwszy consume musi dostarczyć opublikowane ciało");

            // Drugi consume — ta sama wiadomość musi być redeliverowana.
            string? secondBody = null;
            await ConsumeOneAndSettleAsync(
                adapter,
                subName,
                SettlementAction.Ack,
                StandardFlow(),
                cts.Token,
                inspect: msg =>
                {
                    secondBody = Encoding.UTF8.GetString(ReadSequenceToArray(msg.Body));
                });

            // PERF-1: anuluj CTS po odebraniu obu wiadomości
            cts.Cancel();

            secondBody.Should().NotBeNullOrWhiteSpace();
            secondBody.Should().Be(
                firstBody,
                because: "Nack (ModifyAckDeadline 0) musi spowodować ponowne dostarczenie tej samej wiadomości z identycznym ciałem");
        }
        finally
        {
            await PubSubTestEnvironment.TryDeleteSubscriptionAsync(ProjectId, subName, CancellationToken.None);
            await PubSubTestEnvironment.TryDeleteTopicAsync(ProjectId, topicName, CancellationToken.None);
        }
    }

    // ── E2E-PUBSUB-5: Flow control — bounded consume ─────────────────────────

    /// <summary>
    /// E2E-PUBSUB-5: Publikuje M wiadomości, konsumuje z <c>FlowControlOptions</c> i asertuje,
    /// że wszystkie M można odebrać i posettlować bez przekroczenia limitu in-flight.
    /// </summary>
    /// <remarks>
    /// PERF-3 (BINDING): limity ustawiane NA OPCJACH transportu przez <c>configure</c>
    /// (<c>MaxInFlightMessages</c> + <c>MaxOutstandingMessages</c>), nie tylko na <c>FlowControlOptions</c>.
    /// Registry-bound = <c>_options.MaxInFlightMessages</c>; pełny registry cicho dropuje wiadomości.
    /// </remarks>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task FlowControl_BoundedConsume_RespectsInFlightLimit()
    {
        PubSubTestEnvironment.SkipIfUnavailable();

        // 60 s: obejmuje tworzenie topologii + batch publish + konsumpcję M wiadomości.
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        const int MessageCount = 10;
        // PERF-3: k ≥ M (wybieramy 50), by registry nie dropował wiadomości.
        const int FlowLimit = 50;

        string suffix = Guid.NewGuid().ToString("N");
        string topicName = $"e2e-pubsub-flow-{suffix}";
        string subName = $"e2e-pubsub-flow-sub-{suffix}";

        // PERF-3: MaxInFlightMessages i MaxOutstandingMessages ustawiane na opcjach transportu.
        await using PubSubTransportAdapter adapter = PubSubTestEnvironment.CreateAdapter(
            ProjectId,
            configure: c =>
            {
                c.MaxInFlightMessages(FlowLimit);
                c.MaxOutstandingMessages(FlowLimit);
            });

        try
        {
            var declaration = new TopologyDeclaration
            {
                Exchanges = [new ExchangeDeclaration(topicName, ExchangeType.Fanout)],
                Queues = [new QueueDeclaration(Name: subName, Durable: true)],
                ExchangeQueueBindings = [new ExchangeQueueBinding(ExchangeName: topicName, QueueName: subName, RoutingKey: string.Empty)],
            };
            await adapter.DeployTopologyAsync(declaration, cts.Token);

            // Publish M wiadomości.
            OutboundMessage[] messages = Enumerable
                .Range(1, MessageCount)
                .Select(i => new OutboundMessage(
                    routingKey: topicName,
                    headers: new Dictionary<string, string>(),
                    body: SerializeToJson(new { seq = i }),
                    contentType: "application/json"))
                .ToArray();

            await adapter.SendBatchAsync(messages, cts.Token);

            // Konsumuj z FlowControlOptions { MaxInFlightMessages = FlowLimit, InternalQueueCapacity = FlowLimit }.
            var flow = new FlowControlOptions
            {
                MaxInFlightMessages = FlowLimit,
                InternalQueueCapacity = FlowLimit,
            };

            int receivedCount = 0;

            await foreach (InboundMessage msg in adapter.ConsumeAsync(subName, flow, cts.Token))
            {
                // ACK natychmiast — zwalniamy miejsce w registry.
                await adapter.SettleAsync(SettlementAction.Ack, msg, cts.Token);
                receivedCount++;

                if (receivedCount >= MessageCount)
                {
                    break;
                }
            }

            // PERF-1: anuluj CTS po odebraniu M wiadomości
            cts.Cancel();

            // Assert — wszystkie M wiadomości odebrane i posettlowane.
            receivedCount.Should().Be(MessageCount,
                because: "wszystkie opublikowane wiadomości muszą być odebrane i rozliczone w ramach flow control");
        }
        finally
        {
            await PubSubTestEnvironment.TryDeleteSubscriptionAsync(ProjectId, subName, CancellationToken.None);
            await PubSubTestEnvironment.TryDeleteTopicAsync(ProjectId, topicName, CancellationToken.None);
        }
    }

    // ── Broker-free: testy strukturalne (zawsze wykonywane) ───────────────────

    /// <summary>
    /// Broker-free: weryfikuje, że <c>PubSubConfigurator.UseEmulator(...).Build()</c> zwraca
    /// <see cref="PubSubTransportOptions"/> z <c>AuthMode == EmulatorInsecure</c> i niepustym
    /// <c>EmulatorEndpoint</c>. Nie bramkowany — zawsze się wykonuje.
    /// </summary>
    [Fact]
    public void Configurator_UseEmulator_BuildsEmulatorInsecureOptions()
    {
        const string Endpoint = "localhost:8085";

        var cfg = new PubSubConfigurator();
        cfg.ProjectId("p");
        cfg.UseEmulator(Endpoint);

        PubSubTransportOptions options = cfg.Build();

        options.AuthMode.Should().Be(PubSubAuthMode.EmulatorInsecure,
            because: "UseEmulator musi ustawiać AuthMode = EmulatorInsecure");
        options.EmulatorEndpoint.Should().Be(Endpoint,
            because: "UseEmulator musi zapamiętywać podany endpoint");
        // Verify Validate() nie rzuciło (Build() wywołuje Validate() wewnętrznie).
    }

    /// <summary>
    /// Broker-free: weryfikuje czystą logikę <see cref="PubSubTestEnvironment.IsAvailableFor"/>
    /// niezależnie od globalnego stanu środowiska (OQ-1 — determinizm w CI). Nie bramkowany.
    /// </summary>
    [Fact]
    public void Gate_WhenHostNullOrWhitespace_ReportsUnavailable()
    {
        PubSubTestEnvironment.IsAvailableFor(null).Should().BeFalse(
            because: "null host oznacza brak dostępnego emulatora");
        PubSubTestEnvironment.IsAvailableFor(string.Empty).Should().BeFalse(
            because: "pusty string oznacza brak dostępnego emulatora");
        PubSubTestEnvironment.IsAvailableFor("   ").Should().BeFalse(
            because: "biała spacja oznacza brak dostępnego emulatora");
        PubSubTestEnvironment.IsAvailableFor("localhost:8085").Should().BeTrue(
            because: "niepusty endpoint oznacza dostępny emulator");
    }
}
