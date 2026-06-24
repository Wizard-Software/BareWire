using AwesomeAssertions;
using BareWire.Abstractions.Outbox;
using BareWire.Abstractions.Transport;
using BareWire.Outbox;
using Xunit;

namespace BareWire.UnitTests.Outbox;

/// <summary>
/// Unit tests for ordering-key promotion at save time — covering both
/// <see cref="InMemoryOutboxStore"/> and <see cref="EfCoreOutboxStore"/> semantics via
/// the in-memory store (which mirrors the EF Core promotion rules exactly).
/// </summary>
/// <remarks>
/// R7.7.3 / U3 tests:
///   - PerKey + header present and valid → OutboxEntry.OrderingKey == header value.
///   - PerKey + no header → OrderingKey is null (keyless/passthrough).
///   - PerKey + key length &gt; 256 → OrderingKey is null; NEVER truncated.
///   - Two distinct keys both &gt; 256 must NOT collapse to the same stored value (anti-collision).
///   - None mode → OrderingKey is always null regardless of headers.
/// </remarks>
public sealed class OutboxKeyPromotionTests
{
    private const string OrderingHeaderName = "x-ordering-key";

    private static OutboxOptions PerKeyOptions(string headerName = OrderingHeaderName)
        => new()
        {
            OrderingMode = OrderingMode.PerKey,
            OrderingKeyHeaderName = headerName
        };

    private static OutboundMessage MessageWithHeader(string headerValue)
        => new(
            routingKey: "test.topic",
            headers: new Dictionary<string, string> { [OrderingHeaderName] = headerValue },
            body: "body"u8.ToArray(),
            contentType: "application/json");

    private static OutboundMessage MessageWithoutHeader()
        => new(
            routingKey: "test.topic",
            headers: new Dictionary<string, string>(),
            body: "body"u8.ToArray(),
            contentType: "application/json");

    // -------------------------------------------------------------------------
    // PerKey + header present and valid
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SaveMessagesAsync_PerKeyWithHeader_SetsOrderingKey()
    {
        // Arrange
        await using var store = new InMemoryOutboxStore(PerKeyOptions());
        var message = MessageWithHeader("order-42");

        // Act
        await store.SaveMessagesAsync([message]);
        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(10);

        // Assert — key must be promoted exactly.
        batch.Should().ContainSingle();
        batch[0].OrderingKey.Should().Be("order-42");
    }

    // -------------------------------------------------------------------------
    // PerKey + header absent → keyless (null)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SaveMessagesAsync_PerKeyWithNoHeader_OrderingKeyIsNull()
    {
        // Arrange
        await using var store = new InMemoryOutboxStore(PerKeyOptions());
        var message = MessageWithoutHeader();

        // Act
        await store.SaveMessagesAsync([message]);
        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(10);

        // Assert — missing header → keyless passthrough.
        batch.Should().ContainSingle();
        batch[0].OrderingKey.Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // PerKey + whitespace-only header value → keyless (null)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SaveMessagesAsync_PerKeyWithWhitespaceHeader_OrderingKeyIsNull()
    {
        // Arrange
        await using var store = new InMemoryOutboxStore(PerKeyOptions());
        var message = new OutboundMessage(
            routingKey: "test.topic",
            headers: new Dictionary<string, string> { [OrderingHeaderName] = "   " },
            body: "body"u8.ToArray(),
            contentType: "application/json");

        // Act
        await store.SaveMessagesAsync([message]);
        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(10);

        // Assert
        batch[0].OrderingKey.Should().BeNull();
    }

    // -------------------------------------------------------------------------
    // PerKey + key length == 256 (boundary — must be accepted)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SaveMessagesAsync_PerKeyWithExactly256CharKey_SetsOrderingKey()
    {
        // Arrange
        string key256 = new('k', 256);
        await using var store = new InMemoryOutboxStore(PerKeyOptions());
        var message = new OutboundMessage(
            routingKey: "test.topic",
            headers: new Dictionary<string, string> { [OrderingHeaderName] = key256 },
            body: "body"u8.ToArray(),
            contentType: "application/json");

        // Act
        await store.SaveMessagesAsync([message]);
        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(10);

        // Assert — exactly 256 chars must be accepted.
        batch[0].OrderingKey.Should().Be(key256);
        batch[0].OrderingKey!.Length.Should().Be(256);
    }

    // -------------------------------------------------------------------------
    // PerKey + key length > 256 → null (NOT truncated — SEC-2 / anti-collision)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SaveMessagesAsync_PerKeyWithOver256CharKey_OrderingKeyIsNull()
    {
        // Arrange — key is exactly 257 chars.
        string key257 = new('k', 257);
        await using var store = new InMemoryOutboxStore(PerKeyOptions());
        var message = new OutboundMessage(
            routingKey: "test.topic",
            headers: new Dictionary<string, string> { [OrderingHeaderName] = key257 },
            body: "body"u8.ToArray(),
            contentType: "application/json");

        // Act
        await store.SaveMessagesAsync([message]);
        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(10);

        // Assert — over-limit must become keyless NULL, never truncated.
        batch[0].OrderingKey.Should().BeNull(
            "over-limit keys must be treated as keyless to prevent head-of-line collision (SEC-2)");
    }

    // -------------------------------------------------------------------------
    // Anti-collision: two DIFFERENT over-256 keys must NOT collapse
    // (verifies truncation is not happening silently)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SaveMessagesAsync_TwoDifferentOver256CharKeys_BothAreNull_NotCollapsed()
    {
        // Arrange — two keys that share a 256-char prefix but differ at position 257.
        string base256 = new('a', 256);
        string keyA = base256 + "X"; // 257 chars
        string keyB = base256 + "Y"; // 257 chars — different final char

        await using var store = new InMemoryOutboxStore(PerKeyOptions());

        var msgA = new OutboundMessage(
            routingKey: "test.topic",
            headers: new Dictionary<string, string> { [OrderingHeaderName] = keyA },
            body: "A"u8.ToArray(),
            contentType: "application/json");

        var msgB = new OutboundMessage(
            routingKey: "test.topic",
            headers: new Dictionary<string, string> { [OrderingHeaderName] = keyB },
            body: "B"u8.ToArray(),
            contentType: "application/json");

        // Act
        await store.SaveMessagesAsync([msgA, msgB]);
        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(10);

        // Assert — both entries are keyless (null), not collapsed to the same value.
        // This test would fail if truncation occurred, because both keys share the same
        // 256-char prefix and a truncated stored value would be identical for both.
        batch.Should().HaveCount(2);
        batch.Should().AllSatisfy(e => e.OrderingKey.Should().BeNull(
            "over-limit keys must not be truncated — truncation would collapse distinct keys"));
    }

    // -------------------------------------------------------------------------
    // None mode → OrderingKey always null regardless of headers
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SaveMessagesAsync_NoneModeWithHeader_OrderingKeyIsNull()
    {
        // Arrange — None mode, header present.
        var noneOptions = new OutboxOptions { OrderingMode = OrderingMode.None };
        await using var store = new InMemoryOutboxStore(noneOptions);
        var message = MessageWithHeader("should-be-ignored");

        // Act
        await store.SaveMessagesAsync([message]);
        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(10);

        // Assert — None mode must never promote a key (default-OFF invariant §2.1).
        batch[0].OrderingKey.Should().BeNull(
            "OrderingMode.None must not promote any ordering key (default-OFF invariant)");
    }
}
