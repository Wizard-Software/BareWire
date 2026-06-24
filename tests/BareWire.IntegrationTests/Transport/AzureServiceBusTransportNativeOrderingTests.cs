using System.Buffers;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.AzureServiceBus;
using BareWire.Transport.AzureServiceBus.Configuration;
using BareWire.Transport.AzureServiceBus.Internal;
using BareWire.Transport.AzureServiceBus.Topology;
using Xunit;

namespace BareWire.IntegrationTests.Transport;

/// <summary>
/// Integration tests for Azure Service Bus transport-native per-key consumer ordering (M2).
/// </summary>
/// <remarks>
/// <para>
/// Test A (<see cref="Sessions_TransportNativeAffinity_PreservesPerKeyFifoOrder"/>) is broker-gated
/// behind <c>BAREWIRE_ASB_CONNECTION_STRING</c> and proves end-to-end FIFO per <c>SessionId</c>
/// with sessions enabled on the adapter.
/// </para>
/// <para>
/// Test B (<see cref="TransportNativeWithoutUseSessions_FailsFastFromRealConfig"/>) is NOT
/// broker-gated — it exercises only configuration validation and runs in every environment,
/// including CI without a live broker. Its purpose is to prove that
/// <see cref="AzureServiceBusOrderingGate.EnsureSessionAffinityAvailable"/> is reachable from
/// real Azure Service Bus configuration objects (anti-dead-code).
/// </para>
/// </remarks>
[Trait("Category", "AzureServiceBus")]
public sealed class AzureServiceBusTransportNativeOrderingTests
{
    // ── Shared helpers ────────────────────────────────────────────────────────

    private static FlowControlOptions StandardFlow() =>
        new() { MaxInFlightMessages = 10, InternalQueueCapacity = 100 };

    private static byte[] SerializeToJson<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value);

    private static int ReadSeqFromSequence(ReadOnlySequence<byte> body)
    {
        byte[] buffer;
        if (body.IsSingleSegment)
        {
            buffer = body.FirstSpan.ToArray();
        }
        else
        {
            buffer = new byte[body.Length];
            int offset = 0;
            foreach (ReadOnlyMemory<byte> segment in body)
            {
                segment.Span.CopyTo(buffer.AsSpan(offset));
                offset += segment.Length;
            }
        }

        using JsonDocument doc = JsonDocument.Parse(buffer);
        return doc.RootElement.GetProperty("seq").GetInt32();
    }

    // ── Test A: E2E FIFO per SessionId (broker-gated) ─────────────────────────

    /// <summary>
    /// Publishes N=8 messages sharing one <c>BW-SessionId</c> to a session-enabled queue and
    /// asserts they arrive in strictly ascending order. Mirrors E2E-ASB-2
    /// (<c>Sessions_SameSessionId_PreservesFifoOrder</c>) in the per-key-ordering framing:
    /// <c>SessionId</c> acts as the ordering key, and the session receiver provides the
    /// single-active-consumer affinity required by strategy <c>TransportNative</c>.
    /// </summary>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task Sessions_TransportNativeAffinity_PreservesPerKeyFifoOrder()
    {
        AzureServiceBusTestEnvironment.SkipIfUnavailable();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        const int TotalMessages = 8;
        string suffix = Guid.NewGuid().ToString("N");
        string queueName = $"e2e-asb-ordering-{suffix}";

        await using AzureServiceBusTransportAdapter adapter =
            AzureServiceBusTestEnvironment.CreateSasAdapter(c => c.UseSessions());

        try
        {
            var declaration = new TopologyDeclaration
            {
                Queues =
                [
                    new QueueDeclaration(
                        Name: queueName,
                        Durable: true,
                        Arguments: new Dictionary<string, object>
                        {
                            [AzureServiceBusTopologyArguments.RequiresSession] = true,
                        }),
                ],
            };
            await adapter.DeployTopologyAsync(declaration, cts.Token);

            // Publish N messages with a shared ordering key embedded as BW-SessionId header.
            OutboundMessage[] messages = Enumerable
                .Range(1, TotalMessages)
                .Select(i => new OutboundMessage(
                    routingKey: queueName,
                    headers: new Dictionary<string, string>
                    {
                        ["BW-SessionId"] = "ordering-key-1",
                    },
                    body: SerializeToJson(new { seq = i }),
                    contentType: "application/json"))
                .ToArray();

            await adapter.SendBatchAsync(messages, cts.Token);

            // Consume all N messages via the session path and collect sequence numbers.
            var receivedSeqs = new List<int>(TotalMessages);

            await foreach (InboundMessage msg in adapter.ConsumeAsync(queueName, StandardFlow(), cts.Token))
            {
                receivedSeqs.Add(ReadSeqFromSequence(msg.Body));
                await adapter.SettleAsync(SettlementAction.Ack, msg, cts.Token);

                if (receivedSeqs.Count == TotalMessages)
                {
                    break;
                }
            }

            // All messages received and in strictly ascending (publish) order.
            receivedSeqs.Should().HaveCount(TotalMessages);
            receivedSeqs.Should().BeInAscendingOrder(
                because: "messages sharing a BW-SessionId must arrive in publish order " +
                         "(FIFO per-key guarantee via Azure Service Bus session affinity)");
        }
        finally
        {
            await AzureServiceBusTestEnvironment.TryDeleteQueueAsync(queueName, CancellationToken.None);
        }
    }

    // ── Test B: fail-fast from real config (NOT broker-gated) ─────────────────

    /// <summary>
    /// Proves that <see cref="AzureServiceBusOrderingGate.EnsureSessionAffinityAvailable"/>
    /// is reachable from real Azure Service Bus configuration — built through the same fluent
    /// API that production uses — and fires a <see cref="BareWireConfigurationException"/>
    /// when sessions are not enabled (anti-dead-code, anti-fail-OPEN). No broker required.
    /// </summary>
    [Fact]
    public void TransportNativeWithoutUseSessions_FailsFastFromRealConfig()
    {
        // Build options via the real fluent configurator WITHOUT calling UseSessions().
        // Uses a placeholder SAS connection string (non-functional; never contacted).
        var cfg = new AzureServiceBusConfigurator();
        cfg.UseSasAuth(
            "Endpoint=sb://placeholder.servicebus.windows.net/;SharedAccessKeyName=x;SharedAccessKey=y");
        AzureServiceBusTransportOptions options = cfg.Build();

        // options.EnableSessions must be false because UseSessions() was never called.
        options.EnableSessions.Should().BeFalse(
            because: "UseSessions() was not called, so EnableSessions must default to false");

        Action act = () =>
            AzureServiceBusOrderingGate.EnsureSessionAffinityAvailable(
                ConsumerOrderingStrategy.TransportNative, options.EnableSessions);

        var ex = act.Should().Throw<BareWireConfigurationException>().Which;

        // S1: message and OptionValue must not contain any ordering-key value.
        const string HypotheticalKey = "customer-42";
        ex.Message.Should().NotContain(HypotheticalKey,
            because: "ordering-key values must never appear in exception messages (S1 rule)");
        ex.OptionValue.Should().NotContain(HypotheticalKey,
            because: "ordering-key values must never appear in OptionValue (S1 rule)");

        // The message must refer to the missing configuration knob.
        ex.Message.Should().Contain("UseSessions",
            because: "the exception must guide the operator to the missing configuration knob");
    }
}
