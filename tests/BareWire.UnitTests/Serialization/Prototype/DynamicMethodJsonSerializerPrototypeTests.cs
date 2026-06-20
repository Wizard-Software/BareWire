// [modern-csharp loaded] — applied net10.0 / C# 14 conventions
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using AwesomeAssertions;

using BareWire.Benchmarks.Prototype;
using BareWire.Buffers;
using BareWire.Serialization.Json;

namespace BareWire.UnitTests.Serialization.Prototype;

// Test-local record that mirrors BenchmarkOrder's shape.
// BenchmarkOrder lives in BareWire.Benchmarks (internal, different namespace root)
// and is not reachable from UnitTests — D-0/D-1 decision.
internal sealed record ProtoOrder(
    string OrderId,
    decimal Amount,
    string Currency,
    List<ProtoOrderItem>? Items);

internal sealed record ProtoOrderItem(
    string ProductId,
    string Name,
    int Quantity,
    decimal Price);

/// <summary>
/// Unit tests for <see cref="DynamicMethodJsonSerializerPrototype"/> (research spike).
/// Each test verifies a different correctness property of the emitted delegate:
///   round-trip fidelity, byte-parity with STJ, WhenWritingNull behaviour, and delegate caching.
/// </summary>
public sealed class DynamicMethodJsonSerializerPrototypeTests
{
    // Cached once per test class to satisfy CA1869 (TreatWarningsAsErrors in UnitTests).
    private static readonly JsonSerializerOptions s_roundTripOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SystemTextJsonSerializer _stjRef = new();

    /// <summary>
    /// Bytes emitted by the prototype must deserialize back to an object equal to the original.
    /// </summary>
    [Fact]
    public void Serialize_SimpleRecord_RoundTripsViaSystemTextJson()
    {
        var order = new ProtoOrder(
            OrderId: "ORD-001",
            Amount: 99.99m,
            Currency: "PLN",
            Items:
            [
                new ProtoOrderItem("P1", "Widget", 2, 49.99m),
                new ProtoOrderItem("P2", "Gadget", 1, 0.01m),
            ]);

        byte[] bytes = SerializeProto(order);
        var deserialized = JsonSerializer.Deserialize<ProtoOrder>(bytes, s_roundTripOptions);

        deserialized.Should().NotBeNull();
        deserialized!.OrderId.Should().Be(order.OrderId);
        deserialized.Amount.Should().Be(order.Amount);
        deserialized.Currency.Should().Be(order.Currency);
        deserialized.Items.Should().HaveCount(2);
        deserialized.Items![0].ProductId.Should().Be("P1");
        deserialized.Items[1].Quantity.Should().Be(1);
    }

    /// <summary>
    /// Bytes from the prototype must be BYTE-IDENTICAL to bytes from SystemTextJsonSerializer
    /// for the same input — this is the parity gate (D-2).
    /// </summary>
    [Fact]
    public void Serialize_Record_MatchesSystemTextJsonBytes()
    {
        var order = new ProtoOrder(
            OrderId: "ORD-PARITY",
            Amount: 123.45m,
            Currency: "EUR",
            Items:
            [
                new ProtoOrderItem("PROD-00001", "Product Name 1", 3, 10.99m),
                new ProtoOrderItem("PROD-00002", "Product Name 2", 7, 5.50m),
            ]);

        byte[] protoBytes = SerializeProto(order);
        byte[] stjBytes = SerializeStj(order);

        string protoJson = Encoding.UTF8.GetString(protoBytes);
        string stjJson = Encoding.UTF8.GetString(stjBytes);

        // Provide the actual JSON in the failure message to aid debugging.
        protoJson.Should().Be(stjJson,
            because: $"prototype must produce byte-identical JSON to STJ. Proto: {protoJson}  STJ: {stjJson}");
    }

    /// <summary>
    /// A record with a null list property must produce the same bytes as STJ (WhenWritingNull omits it).
    /// </summary>
    [Fact]
    public void Serialize_WithNullItems_ProducesValidJson()
    {
        var order = new ProtoOrder(
            OrderId: "ORD-NULL",
            Amount: 0m,
            Currency: "USD",
            Items: null);

        byte[] protoBytes = SerializeProto(order);
        byte[] stjBytes = SerializeStj(order);

        string protoJson = Encoding.UTF8.GetString(protoBytes);
        string stjJson = Encoding.UTF8.GetString(stjBytes);

        // Null Items must be OMITTED (WhenWritingNull).
        protoJson.Should().NotContain("items",
            because: "WhenWritingNull must omit null list properties");

        protoJson.Should().Be(stjJson,
            because: $"byte-parity with STJ required even for null fields. Proto: {protoJson}  STJ: {stjJson}");
    }

    /// <summary>
    /// The delegate cache must be hit on the second call — the build-counter must not increment.
    /// This is a falsifiable assertion (D-1): if caching is broken, counter exceeds the type count.
    /// </summary>
    [Fact]
    public void Serialize_CalledTwice_ReusesCachedDelegate()
    {
        // Reset to get a clean baseline for this test type.
        DynamicMethodJsonSerializerPrototype.ResetBuildCount();

        var order = new ProtoOrder("ORD-CACHE", 1.00m, "GBP",
            [new ProtoOrderItem("P-A", "Alpha", 1, 1.00m)]);

        using var buf1 = new PooledBufferWriter();
        using var buf2 = new PooledBufferWriter();

        DynamicMethodJsonSerializerPrototype.Serialize(order, buf1);   // First call — builds delegate
        DynamicMethodJsonSerializerPrototype.Serialize(order, buf2);   // Second call — must reuse

        // Two distinct types (ProtoOrder + ProtoOrderItem) will each be built at most once.
        // BuildCount should be ≤ 2 across both Serialize calls, not ≤ 4.
        DynamicMethodJsonSerializerPrototype.BuildCount.Should().BeLessThanOrEqualTo(2,
            because: "each type's delegate must be built at most once and cached thereafter");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static byte[] SerializeProto<T>(T message) where T : class
    {
        using var buf = new PooledBufferWriter();
        DynamicMethodJsonSerializerPrototype.Serialize(message, buf);
        return buf.WrittenSpan.ToArray();
    }

    private byte[] SerializeStj<T>(T message) where T : class
    {
        using var buf = new PooledBufferWriter();
        _stjRef.Serialize(message, buf);
        return buf.WrittenSpan.ToArray();
    }
}
