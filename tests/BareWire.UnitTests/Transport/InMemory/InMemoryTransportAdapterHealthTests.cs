using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Transport;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Configuration;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BareWire.UnitTests.Transport.InMemory;

/// <summary>
/// Covers <see cref="InMemoryTransportAdapter.GetHealth"/> (the <see cref="ITransportHealthSource"/>
/// implementation): per-queue <see cref="BusStatus.Degraded"/> at or above 90% occupancy with an exact,
/// hysteresis-free reported status, and the SEPARATE, hysteresed logging of health transitions
/// (<see cref="InMemoryQueueDiagnostics.ReportHealth"/>) — a <see cref="LogLevel.Warning"/> on first
/// entering ≥ 90%, a <see cref="LogLevel.Information"/> only once occupancy drops below 80%, and nothing
/// in between, however many times <see cref="InMemoryTransportAdapter.GetHealth"/> is polled.
/// </summary>
public sealed class InMemoryTransportAdapterHealthTests
{
    private static InMemoryTransportAdapter Adapter(
        Action<IInMemoryConfigurator> configure, ILogger<InMemoryTransportAdapter>? logger = null)
    {
        var c = new InMemoryConfigurator();
        configure(c);
        InMemoryTransportOptions o = c.Build();
        return new InMemoryTransportAdapter(o, new InMemoryBroker(o), logger: logger);
    }

    private static InMemoryQueue Queue(InMemoryTransportAdapter a, string name)
    {
        a.Broker.TryGetQueue(name, out InMemoryQueue? q).Should().BeTrue();
        return q!;
    }

    // Calls through the ITransportHealthSource seam without keeping an interface-typed local around (the
    // concrete adapter type is always known here — CA1859 flags a stored interface-typed local as a
    // needless devirtualization cost).
    private static BusHealthStatus GetHealth(InMemoryTransportAdapter adapter) =>
        ((ITransportHealthSource)adapter).GetHealth();

    // Directly reserves `count` slots on `queue` — test-side setup driving occupancy without needing a
    // full send/consume round-trip; never writes to the channel (see InMemoryQueueLatchEpisodeTests'
    // remarks on why a paired WriteReserved would be unsafe here with nothing to consume it).
    private static void Occupy(InMemoryQueue queue, int count)
    {
        for (int i = 0; i < count; i++)
        {
            queue.TryReserve().Should().Be(QueueReservationResult.Reserved);
        }
    }

    [Fact]
    public void Adapter_IsTransportHealthSource()
    {
        InMemoryTransportAdapter adapter = Adapter(c => c.ConfigureTopology(t => t.DeclareQueue("q")));

        (adapter is ITransportHealthSource).Should().BeTrue();
    }

    [Fact]
    public void GetHealth_WhenOccupancyReaches90Percent_ReportsDegradedForThatQueueOnly()
    {
        InMemoryTransportAdapter adapter = Adapter(c =>
        {
            c.QueueCapacity(10);
            c.ConfigureTopology(t =>
            {
                t.DeclareQueue("a");
                t.DeclareQueue("b");
            });
        });
        Occupy(Queue(adapter, "a"), 9); // 9 of 10 => 90% => Degraded
        Occupy(Queue(adapter, "b"), 1); // 1 of 10 => 10% => Healthy

        BusHealthStatus health = GetHealth(adapter);

        health.Status.Should().Be(BusStatus.Degraded);
        health.Endpoints.Single(e => e.EndpointName == "a").Status.Should().Be(BusStatus.Degraded);
        health.Endpoints.Single(e => e.EndpointName == "a").Description.Should().Be("occupancy 9 of 10");
        health.Endpoints.Single(e => e.EndpointName == "b").Status.Should().Be(BusStatus.Healthy);
    }

    [Fact]
    public void GetHealth_WhenOccupancyBelow90Percent_ReportsHealthy()
    {
        InMemoryTransportAdapter adapter = Adapter(c =>
        {
            c.QueueCapacity(10);
            c.ConfigureTopology(t => t.DeclareQueue("a"));
        });
        Occupy(Queue(adapter, "a"), 8); // 8 of 10 => 80% => still Healthy

        BusHealthStatus health = GetHealth(adapter);

        health.Status.Should().Be(BusStatus.Healthy);
        health.Endpoints.Single(e => e.EndpointName == "a").Status.Should().Be(BusStatus.Healthy);
        health.Endpoints.Single(e => e.EndpointName == "a").Description.Should().Be("occupancy 8 of 10");
    }

    [Fact]
    public void GetHealth_CalledRepeatedlyWhileDegraded_LogsWarningOnceAndInformationOnRecovery()
    {
        var logger = new CapturingLogger<InMemoryTransportAdapter>();
        InMemoryTransportAdapter adapter = Adapter(
            c =>
            {
                c.QueueCapacity(10);
                c.ConfigureTopology(t => t.DeclareQueue("a"));
            },
            logger);
        InMemoryQueue queue = Queue(adapter, "a");
        Occupy(queue, 9); // 90% => Degraded

        GetHealth(adapter);
        GetHealth(adapter);
        GetHealth(adapter);

        logger.Entries.Count(e => e.EventId.Id == 2303 && e.Level == LogLevel.Warning).Should().Be(1);
        logger.Entries.Count(e => e.EventId.Id == 2304).Should().Be(0);

        for (int i = 0; i < 3; i++)
        {
            queue.ReleaseSlot(); // 9 -> 6 of 10 = 60% < 80% => recovery
        }

        GetHealth(adapter);
        GetHealth(adapter);

        logger.Entries.Count(e => e.EventId.Id == 2303 && e.Level == LogLevel.Warning).Should().Be(1);
        logger.Entries.Count(e => e.EventId.Id == 2304 && e.Level == LogLevel.Information).Should().Be(1);
    }

    [Fact]
    public void GetHealth_OscillatingAcrossReportedThreshold_LogsOnlyOnEnterAndRecoverWithHysteresis()
    {
        var logger = new CapturingLogger<InMemoryTransportAdapter>();
        InMemoryTransportAdapter adapter = Adapter(
            c =>
            {
                c.QueueCapacity(10);
                c.ConfigureTopology(t => t.DeclareQueue("a"));
            },
            logger);
        InMemoryQueue queue = Queue(adapter, "a");
        Occupy(queue, 9); // 90% => Degraded, reported AND logged
        GetHealth(adapter).Status.Should().Be(BusStatus.Degraded);

        // Oscillate between 9 (90%, Degraded) and 8 (80%, Healthy — but the LOG hysteresis band is
        // [80%, 90%), so recovery logs only below 80%) many times: the REPORTED status must flip exactly
        // with occupancy every time, but the LOG must stay silent throughout this band.
        for (int i = 0; i < 20; i++)
        {
            queue.ReleaseSlot(); // 9 -> 8 (80%)
            GetHealth(adapter).Status.Should().Be(BusStatus.Healthy); // exact: 80% is below the 90% line

            queue.TryReserve().Should().Be(QueueReservationResult.Reserved); // 8 -> 9 (90%)
            GetHealth(adapter).Status.Should().Be(BusStatus.Degraded);
        }

        logger.Entries.Count(e => e.EventId.Id == 2303).Should().Be(1); // only the very first entry into >= 90%
        logger.Entries.Count(e => e.EventId.Id == 2304).Should().Be(0); // never dropped below 80%

        queue.ReleaseSlot(); // 9 -> 8 (80%, still in the hysteresis band)
        GetHealth(adapter);
        logger.Entries.Count(e => e.EventId.Id == 2304).Should().Be(0);

        queue.ReleaseSlot(); // 8 -> 7 (70% < 80%) => recovery
        GetHealth(adapter);
        logger.Entries.Count(e => e.EventId.Id == 2304).Should().Be(1);
    }

    [Fact]
    public async Task GetHealth_Descriptions_NeverContainRoutingKeyOrMessageId()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string secretMessageId = "mid-9c1e-secret";
        const string queueName = "q";
        InMemoryTransportAdapter adapter = Adapter(c =>
        {
            c.QueueCapacity(10);
            c.ConfigureTopology(t => t.DeclareQueue(queueName));
        });

        // Drives real traffic carrying a publisher-supplied message id through the queue that GetHealth
        // reports on — GetHealth itself never touches a message or its headers at all (it only reads
        // InMemoryQueue's own Name/Occupancy/Capacity), so this is a structural guarantee, exercised here
        // end to end rather than asserted from code inspection alone.
        OutboundMessage[] messages = [.. Enumerable.Range(0, 9).Select(_ => new OutboundMessage(
            queueName,
            new Dictionary<string, string> { ["BW-Exchange"] = "", ["message-id"] = secretMessageId },
            new byte[8],
            ""))];
        await adapter.SendBatchAsync(messages, ct);

        BusHealthStatus health = GetHealth(adapter);

        health.Description.Should().NotContain(secretMessageId);
        health.Endpoints.Should().OnlyContain(e => !(e.Description ?? string.Empty).Contains(secretMessageId, StringComparison.Ordinal));
        health.Endpoints.Single().Description.Should().MatchRegex(@"^occupancy \d+ of \d+$");
    }
}
