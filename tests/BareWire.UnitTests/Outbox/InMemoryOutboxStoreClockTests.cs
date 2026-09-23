using AwesomeAssertions;
using BareWire.Abstractions.Transport;
using BareWire.Outbox;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Outbox;

public sealed class InMemoryOutboxStoreClockTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static OutboundMessage Message()
        => new(
            routingKey: "orders.created",
            headers: new Dictionary<string, string>(),
            body: "{}"u8.ToArray(),
            contentType: "application/json");

    [Fact]
    public async Task SaveMessagesAsync_WithFakeTimeProvider_StampsCreatedAtFromInjectedClock()
    {
        // Arrange
        var clock = new FakeTimeProvider(T0);
        await using var store = new InMemoryOutboxStore(timeProvider: clock);

        // Act
        await store.SaveMessagesAsync([Message()]);
        IReadOnlyList<OutboxEntry> batch = await store.GetPendingAsync(10);

        // Assert
        batch.Should().ContainSingle().Which.CreatedAt.Should().Be(T0);
    }

    [Fact]
    public async Task MarkDeliveredAsync_WhenFakeClockAdvanced_StampsDeliveredAtWithAdvancedTime()
    {
        // Arrange
        var clock = new FakeTimeProvider(T0);
        await using var store = new InMemoryOutboxStore(timeProvider: clock);
        await store.SaveMessagesAsync([Message()]);
        OutboxEntry entry = (await store.GetPendingAsync(10)).Single();
        clock.Advance(TimeSpan.FromMinutes(5));

        // Act
        await store.MarkDeliveredAsync([entry.Id]);

        // Assert
        entry.DeliveredAt.Should().Be(T0 + TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Constructor_WithoutClockOrJitter_DefaultsToSystemClockAndSharedRandom()
    {
        // Act
        await using var store = new InMemoryOutboxStore();

        // Assert
        store.TimeProvider.Should().BeSameAs(TimeProvider.System);
        store.JitterSource.Should().BeSameAs(SharedRandomOutboxJitterSource.Instance);
    }

    [Fact]
    public async Task Constructor_WithInjectedJitterSource_ExposesInjectedInstance()
    {
        // Arrange
        IOutboxJitterSource jitter = Substitute.For<IOutboxJitterSource>();

        // Act
        await using var store = new InMemoryOutboxStore(jitterSource: jitter);

        // Assert
        store.JitterSource.Should().BeSameAs(jitter);
    }

    [Fact]
    public void SharedRandomOutboxJitterSource_NextDouble_ReturnsValueInUnitInterval()
    {
        for (int i = 0; i < 1_000; i++)
        {
            double value = SharedRandomOutboxJitterSource.Instance.NextDouble();

            value.Should().BeGreaterThanOrEqualTo(0.0).And.BeLessThan(1.0);
        }
    }
}
