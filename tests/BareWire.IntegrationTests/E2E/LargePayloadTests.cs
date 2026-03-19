using System.Buffers;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.IntegrationTests.E2E;

/// <summary>A single record in a large payload array for round-trip testing.</summary>
public sealed record LargePayloadRecord(int Index, string Data);

/// <summary>
/// E2E-5: Large payload round-trip test.
///
/// <para>
/// Generates JSON payloads of varying sizes (256 KB, 512 KB, 1 MB), publishes each through
/// RabbitMQ, consumes and deserializes the message, and verifies that the payload
/// round-trips without data corruption.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
public sealed class LargePayloadTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private RabbitMqTransportAdapter CreateAdapter() =>
        new(
            new RabbitMqTransportOptions
            {
                ConnectionString = fixture.GetRabbitMqConnectionString(),
            },
            NullLogger<RabbitMqTransportAdapter>.Instance);

    private static async Task<(string ExchangeName, string QueueName)> DeployTopologyAsync(
        RabbitMqTransportAdapter adapter,
        string suffix,
        CancellationToken ct)
    {
        string exchangeName = $"e2e-large-ex-{suffix}";
        string queueName = $"e2e-large-q-{suffix}";

        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareExchange(exchangeName, ExchangeType.Direct, durable: false, autoDelete: false);
        configurator.DeclareQueue(queueName, durable: false, autoDelete: false);
        configurator.BindExchangeToQueue(exchangeName, queueName, routingKey: queueName);
        await adapter.DeployTopologyAsync(configurator.Build(), ct);

        return (exchangeName, queueName);
    }

    private static FlowControlOptions LargePayloadFlow() =>
        new() { MaxInFlightMessages = 5, InternalQueueCapacity = 20 };

    /// <summary>
    /// Generates a list of <see cref="LargePayloadRecord"/> instances whose JSON serialization
    /// is approximately <paramref name="targetSizeBytes"/> bytes.
    /// </summary>
    private static LargePayloadRecord[] GenerateLargePayload(int targetSizeBytes)
    {
        // Each record: { "Index": N, "Data": "XXXX...X" }
        // A ~100-char data string + JSON overhead ≈ ~130 bytes per record.
        const int BytesPerRecord = 130;
        int recordCount = Math.Max(1, targetSizeBytes / BytesPerRecord);

        // Use a fixed-length data string to get predictable sizes
        string dataValue = new string('A', 100);

        return Enumerable
            .Range(0, recordCount)
            .Select(i => new LargePayloadRecord(i, dataValue))
            .ToArray();
    }

    private static T DeserializeFromSequence<T>(ReadOnlySequence<byte> body)
    {
        if (body.IsSingleSegment)
        {
            return JsonSerializer.Deserialize<T>(body.FirstSpan)
                ?? throw new InvalidOperationException($"Failed to deserialize {typeof(T).Name}.");
        }

        byte[] buffer = new byte[body.Length];
        int offset = 0;
        foreach (ReadOnlyMemory<byte> segment in body)
        {
            segment.Span.CopyTo(buffer.AsSpan(offset));
            offset += segment.Length;
        }

        return JsonSerializer.Deserialize<T>(buffer)
            ?? throw new InvalidOperationException($"Failed to deserialize {typeof(T).Name}.");
    }

    // ── E2E-5: Large payload round-trip ───────────────────────────────────────

    /// <summary>
    /// E2E-5: Publishes a single message with a large JSON payload of approximately
    /// <paramref name="targetSizeBytes"/> bytes, consumes it, and verifies that the
    /// deserialized payload matches the original — no data corruption.
    /// </summary>
    [Theory]
    [InlineData(256_000)]
    [InlineData(512_000)]
    [InlineData(1_048_576)]
    public async Task LargePayload_RoundTrip_NoDataCorruption(int targetSizeBytes)
    {
        // Arrange — 60 s timeout per payload size
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");
        (string exchangeName, string queueName) =
            await DeployTopologyAsync(adapter, suffix, cts.Token);

        LargePayloadRecord[] original = GenerateLargePayload(targetSizeBytes);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(original);

        // Verify the generated payload meets the size target (within 20% tolerance for overhead)
        body.Length.Should().BeGreaterThanOrEqualTo(
            (int)(targetSizeBytes * 0.80),
            because: $"generated payload must be at least 80% of {targetSizeBytes} bytes");

        OutboundMessage outbound = new(
            routingKey: queueName,
            headers: new Dictionary<string, string>
            {
                ["BW-Exchange"] = exchangeName,
                ["X-PayloadSizeBytes"] = body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["X-RecordCount"] = original.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            body: body,
            contentType: "application/json");

        // Act — publish the large payload
        IReadOnlyList<SendResult> sendResults = await adapter.SendBatchAsync([outbound], cts.Token);

        // Assert — broker confirmed delivery
        sendResults.Should().HaveCount(1);
        sendResults[0].IsConfirmed.Should().BeTrue(
            because: "broker must confirm delivery of large payload messages");

        // Act — consume and deserialize
        InboundMessage? received = null;

        await foreach (InboundMessage msg in
            adapter.ConsumeAsync(queueName, LargePayloadFlow(), cts.Token))
        {
            received = msg;
            break;
        }

        received.Should().NotBeNull(because: "the published large payload message must be consumed");

        // Assert — body size header is preserved
        received!.Headers.Should().ContainKey("X-PayloadSizeBytes");

        // Assert — deserialize and verify content integrity
        LargePayloadRecord[] roundTripped =
            DeserializeFromSequence<LargePayloadRecord[]>(received.Body);

        roundTripped.Should().HaveCount(original.Length,
            because: "the deserialized array must contain the same number of records as the original");

        // Verify first, middle, and last records to confirm no corruption
        roundTripped[0].Index.Should().Be(original[0].Index,
            because: "first record index must match");
        roundTripped[0].Data.Should().Be(original[0].Data,
            because: "first record data must match");

        int middleIndex = original.Length / 2;
        roundTripped[middleIndex].Index.Should().Be(original[middleIndex].Index,
            because: "middle record index must match");
        roundTripped[middleIndex].Data.Should().Be(original[middleIndex].Data,
            because: "middle record data must match");

        roundTripped[^1].Index.Should().Be(original[^1].Index,
            because: "last record index must match");
        roundTripped[^1].Data.Should().Be(original[^1].Data,
            because: "last record data must match");

        // Assert — received body byte count matches the published body
        long receivedBodyLength = received.Body.Length;
        receivedBodyLength.Should().Be(body.Length,
            because: "the received body length must exactly match the published body length");

        // Clean up
        await adapter.SettleAsync(SettlementAction.Ack, received, cts.Token);
    }

    // ── E2E-5b: Large payload with custom binary content ──────────────────────

    /// <summary>
    /// E2E-5b: Publishes a 256 KB raw binary payload and verifies that the byte content
    /// is preserved exactly (no encoding mutations).
    /// </summary>
    [Fact]
    public async Task LargePayload_RawBinary_BytesPreservedExactly()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string suffix = Guid.NewGuid().ToString("N");
        (string exchangeName, string queueName) =
            await DeployTopologyAsync(adapter, suffix, cts.Token);

        // Generate 256 KB of deterministic pseudo-random bytes
        const int PayloadSize = 256 * 1024;
        byte[] rawPayload = new byte[PayloadSize];
        for (int i = 0; i < PayloadSize; i++)
        {
            rawPayload[i] = (byte)(i % 256);
        }

        OutboundMessage outbound = new(
            routingKey: queueName,
            headers: new Dictionary<string, string>
            {
                ["BW-Exchange"] = exchangeName,
                ["X-PayloadType"] = "binary",
            },
            body: rawPayload,
            contentType: "application/octet-stream");

        // Act
        await adapter.SendBatchAsync([outbound], cts.Token);

        InboundMessage? received = null;
        await foreach (InboundMessage msg in
            adapter.ConsumeAsync(queueName, LargePayloadFlow(), cts.Token))
        {
            received = msg;
            break;
        }

        received.Should().NotBeNull();

        // Assert — byte-for-byte integrity
        byte[] receivedBytes = received!.Body.IsSingleSegment
            ? received.Body.FirstSpan.ToArray()
            : received.Body.ToArray();

        receivedBytes.Should().HaveCount(PayloadSize,
            because: "received binary payload must be exactly 256 KB");

        receivedBytes.Should().BeEquivalentTo(rawPayload,
            because: "raw binary payload must survive the full broker round-trip unchanged");

        await adapter.SettleAsync(SettlementAction.Ack, received, cts.Token);
    }
}
