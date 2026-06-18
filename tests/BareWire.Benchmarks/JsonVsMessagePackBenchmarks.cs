using System.Buffers;
using System.Diagnostics.CodeAnalysis;

using BareWire.Abstractions.Serialization;
using BareWire.Buffers;
using BareWire.Serialization.Json;
using BareWire.Serialization.MsgPack;

using BenchmarkDotNet.Attributes;

// NOTE: Intentionally NO `using MessagePack;` here. The library exposes a static
// `MessagePack.MessagePackSerializer`, whose simple name collides (CS0104) with
// BareWire's `BareWire.Serialization.MsgPack.MessagePackSerializer`. With only the
// BareWire namespace imported, the names resolve unambiguously to the BareWire types.
namespace BareWire.Benchmarks;

// Public records are required by MessagePack's ContractlessStandardResolver
// (BareWireMessagePackSerializerOptions.Default — see ADR-013). System.Text.Json
// serializes these the same way, so both serializers share one object graph.
public sealed record MsgPackOrder(
    string OrderId,
    decimal Amount,
    string Currency,
    List<MsgPackOrderItem>? Items);

public sealed record MsgPackOrderItem(
    string ProductId,
    string Name,
    int Quantity,
    decimal Price);

/// <summary>
/// Comparative benchmark: System.Text.Json (raw, application/json) versus MessagePack
/// (application/x-msgpack) over the same object graph, for payload sizes 100 B – 100 KB.
/// Measures allocations (B/op via <see cref="MemoryDiagnoserAttribute"/>), throughput, and
/// latency for serialize/deserialize, plus on-wire serialized size. Documented expectation
/// (R3.3 / TASKS-ROADMAP): MessagePack allocates ~2-5x less, most visibly on the serialize path.
/// </summary>
[MemoryDiagnoser(displayGenColumns: true)]
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "BenchmarkDotNet manages object lifetime via [GlobalCleanup].")]
public class JsonVsMessagePackBenchmarks
{
    [Params(100, 1_000, 10_000, 100_000)]
    public int PayloadSizeBytes { get; set; }

    private SystemTextJsonSerializer _jsonSerializer = null!;
    private SystemTextJsonRawDeserializer _jsonDeserializer = null!;
    private MessagePackSerializer _msgPackSerializer = null!;
    private MessagePackDeserializer _msgPackDeserializer = null!;
    private PooledBufferWriter _writer = null!;
    private MsgPackOrder _payload = null!;
    private ReadOnlySequence<byte> _serializedJson;
    private ReadOnlySequence<byte> _serializedMsgPack;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _jsonSerializer = new SystemTextJsonSerializer();
        _jsonDeserializer = new SystemTextJsonRawDeserializer();
        _msgPackSerializer = new MessagePackSerializer();
        _msgPackDeserializer = new MessagePackDeserializer();
        _writer = new PooledBufferWriter(initialCapacity: PayloadSizeBytes * 4);
        _payload = GeneratePayload(PayloadSizeBytes);

        _serializedJson = PreSerialize(_jsonSerializer, _payload);
        _serializedMsgPack = PreSerialize(_msgPackSerializer, _payload);
    }

    [Benchmark(Baseline = true)]
    public int Serialize_Json()
    {
        _writer.Reset();
        _jsonSerializer.Serialize(_payload, _writer);
        return _writer.WrittenCount;
    }

    [Benchmark]
    public int Serialize_MsgPack()
    {
        _writer.Reset();
        _msgPackSerializer.Serialize(_payload, _writer);
        return _writer.WrittenCount;
    }

    [Benchmark]
    public object? Deserialize_Json()
    {
        return _jsonDeserializer.Deserialize<MsgPackOrder>(_serializedJson);
    }

    [Benchmark]
    public object? Deserialize_MsgPack()
    {
        return _msgPackDeserializer.Deserialize<MsgPackOrder>(_serializedMsgPack);
    }

    [Benchmark]
    public int SerializedSize_Json()
    {
        _writer.Reset();
        _jsonSerializer.Serialize(_payload, _writer);
        return _writer.WrittenCount;
    }

    [Benchmark]
    public int SerializedSize_MsgPack()
    {
        _writer.Reset();
        _msgPackSerializer.Serialize(_payload, _writer);
        return _writer.WrittenCount;
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _writer.Dispose();
    }

    private static MsgPackOrder GeneratePayload(int targetSize)
    {
        // Start with an estimate: each item serializes to roughly 80 bytes.
        const int bytesPerItem = 80;
        int estimatedItems = Math.Max(1, (targetSize - 60) / bytesPerItem);

        MsgPackOrder order = BuildOrder(estimatedItems);

        // Iteratively adjust item count until serialized size is within ±10% of target.
        // Size is measured against JSON so PayloadSizeBytes stays consistent with
        // SerializationBenchmarks; MessagePack will naturally be smaller for the same graph.
        for (int attempt = 0; attempt < 20; attempt++)
        {
            int actualSize = MeasureSerializedSize(order);
            double ratio = (double)actualSize / targetSize;

            if (ratio >= 0.9 && ratio <= 1.1)
                break;

            // Scale item count proportionally to the size difference.
            estimatedItems = Math.Max(1, (int)(estimatedItems * ((double)targetSize / actualSize)));
            order = BuildOrder(estimatedItems);
        }

        return order;
    }

    private static MsgPackOrder BuildOrder(int itemCount)
    {
        var items = new List<MsgPackOrderItem>(itemCount);

        for (int i = 0; i < itemCount; i++)
        {
            items.Add(new MsgPackOrderItem(
                ProductId: $"PROD-{i:D5}",
                Name: $"Product Name {i}",
                Quantity: (i % 10) + 1,
                Price: 9.99m + i));
        }

        return new MsgPackOrder(
            OrderId: "ORD-20260318-001",
            Amount: items.Sum(x => x.Price * x.Quantity),
            Currency: "PLN",
            Items: items);
    }

    private static int MeasureSerializedSize(MsgPackOrder order)
    {
        using var tempWriter = new PooledBufferWriter(initialCapacity: 4096);
        using var jsonWriter = new System.Text.Json.Utf8JsonWriter(tempWriter);
        System.Text.Json.JsonSerializer.Serialize(jsonWriter, order);
        jsonWriter.Flush();
        return tempWriter.WrittenCount;
    }

    private ReadOnlySequence<byte> PreSerialize(IMessageSerializer serializer, MsgPackOrder message)
    {
        using var tempWriter = new PooledBufferWriter(initialCapacity: PayloadSizeBytes * 4);
        serializer.Serialize(message, tempWriter);
        byte[] copy = tempWriter.WrittenSpan.ToArray();
        return new ReadOnlySequence<byte>(copy);
    }
}
