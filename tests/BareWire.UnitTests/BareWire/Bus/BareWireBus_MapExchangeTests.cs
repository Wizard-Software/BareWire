using AwesomeAssertions;
using BareWire.Abstractions.Routing;
using BareWire.Abstractions.Transport;
using BareWire.Routing;
using BareWire.Testing;

namespace BareWire.UnitTests.Core.Bus;

/// <summary>
/// Precedence tests for <c>BW-Exchange</c> header injection in <c>BareWireBus.PublishAsync&lt;T&gt;</c>.
/// Validates the contract from <c>MapExchange&lt;T&gt;</c> (task 3.13):
/// caller header > type→exchange mapping > DefaultExchange fallback.
/// </summary>
public sealed class BareWireBus_MapExchangeTests
{
    private sealed record PaymentRequested(decimal Amount);
    private sealed record OrderCreated(string OrderId);

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(2);

    // ── (b) Mapping injects BW-Exchange when no header supplied ───────────────

    [Fact]
    public async Task PublishAsync_WithExchangeMapping_InjectsBwExchangeHeader()
    {
        // Arrange
        var mappings = new Dictionary<Type, string>
        {
            [typeof(PaymentRequested)] = "payments.topic",
        };
        IExchangeResolver resolver = new ExchangeResolver(mappings);

        await using BareWireTestHarness harness = await BareWireTestHarness.CreateAsync(
            exchangeResolver: resolver);

        Task<OutboundMessage> observe = harness.WaitForPublishAsync<PaymentRequested>(TestTimeout);

        // Act
        await harness.Bus.PublishAsync(new PaymentRequested(10m));
        OutboundMessage sent = await observe;

        // Assert
        sent.Headers.Should().ContainKey("BW-Exchange")
            .WhoseValue.Should().Be("payments.topic");
    }

    // ── (a) Explicit BW-Exchange header wins over mapping ─────────────────────

    [Fact]
    public async Task PublishAsync_WithBwExchangeHeaderAndMapping_HeaderWins()
    {
        // Arrange
        var mappings = new Dictionary<Type, string>
        {
            [typeof(PaymentRequested)] = "payments.topic",
        };
        IExchangeResolver resolver = new ExchangeResolver(mappings);

        await using BareWireTestHarness harness = await BareWireTestHarness.CreateAsync(
            exchangeResolver: resolver);

        Task<OutboundMessage> observe = harness.WaitForPublishAsync<PaymentRequested>(TestTimeout);

        var headers = new Dictionary<string, string>
        {
            ["BW-Exchange"] = "explicit.override",
        };

        // Act
        await harness.Bus.PublishAsync(new PaymentRequested(10m), headers);
        OutboundMessage sent = await observe;

        // Assert — explicit header from caller wins over the type→exchange mapping.
        sent.Headers.Should().ContainKey("BW-Exchange")
            .WhoseValue.Should().Be("explicit.override");
    }

    // ── (c) No mapping + no header → BW-Exchange absent (adapter falls back) ──

    [Fact]
    public async Task PublishAsync_WithoutMappingAndWithoutHeader_DoesNotInjectBwExchange()
    {
        // Arrange — no mappings, default resolver returns null for every type.
        await using BareWireTestHarness harness = await BareWireTestHarness.CreateAsync();

        Task<OutboundMessage> observe = harness.WaitForPublishAsync<PaymentRequested>(TestTimeout);

        // Act
        await harness.Bus.PublishAsync(new PaymentRequested(10m));
        OutboundMessage sent = await observe;

        // Assert — BW-Exchange must NOT be injected when neither the caller nor a mapping supplied one.
        // Downstream, the transport adapter falls back to DefaultExchange.
        sent.Headers.Should().NotContainKey("BW-Exchange");
    }

    // ── Type isolation: mapping for one type does not bleed into another ─────

    [Fact]
    public async Task PublishAsync_WithMappingForDifferentType_DoesNotInject()
    {
        // Arrange — mapping only for PaymentRequested, publish OrderCreated.
        var mappings = new Dictionary<Type, string>
        {
            [typeof(PaymentRequested)] = "payments.topic",
        };
        IExchangeResolver resolver = new ExchangeResolver(mappings);

        await using BareWireTestHarness harness = await BareWireTestHarness.CreateAsync(
            exchangeResolver: resolver);

        Task<OutboundMessage> observe = harness.WaitForPublishAsync<OrderCreated>(TestTimeout);

        // Act
        await harness.Bus.PublishAsync(new OrderCreated("O-1"));
        OutboundMessage sent = await observe;

        // Assert — OrderCreated has no mapping, so BW-Exchange must remain absent.
        sent.Headers.Should().NotContainKey("BW-Exchange");
    }
}
