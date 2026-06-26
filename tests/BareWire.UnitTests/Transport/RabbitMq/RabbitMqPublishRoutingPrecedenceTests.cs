using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Routing;
using BareWire.Abstractions.Transport;
using BareWire.Testing;
using BareWire.Transport.RabbitMQ;
using BareWire.Transport.RabbitMQ.Configuration;
using CoreExchangeResolver = BareWire.Routing.ExchangeResolver;
using TransportExchangeResolver = BareWire.Transport.RabbitMQ.Internal.ExchangeResolver;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// 16.11 — Runtime publish precedence is UNCHANGED by the ergonomic registration shapes: explicit
/// <c>BW-Exchange</c> header &gt; per-type mapping &gt; <c>DefaultExchange</c>. The registration shape
/// (<c>Publish&lt;T&gt;</c> vs <c>DeclareExchange&lt;T&gt;</c> vs <c>MapExchange&lt;T&gt;</c>) only
/// affects the snapshotted exchange map — it must give IDENTICAL runtime resolution. The header &gt;
/// mapping tier is exercised through the real bus publish path (in-memory harness); the mapping &gt;
/// <c>DefaultExchange</c> tier is asserted on the resolver directly (null = defer to DefaultExchange).
/// </summary>
public sealed class RabbitMqPublishRoutingPrecedenceTests
{
    private sealed record OrderPlaced(string OrderId);
    private sealed record Unmapped(string Value);

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(2);

    // Shape discriminators kept as strings so the [Theory] test methods can stay public.
    public const string ShapeMapExchange = "MapExchange";
    public const string ShapeDeclareExchangeGeneric = "DeclareExchangeGeneric";
    public const string ShapePublish = "Publish";

    private static RabbitMqTransportOptions BuildOptionsMappingOrderPlacedToOrders(string shape)
    {
        var configurator = new RabbitMqConfigurator();
        configurator.Host("amqp://guest:guest@localhost:5672/");

        switch (shape)
        {
            case ShapeMapExchange:
                configurator.ConfigureTopology(t => t.DeclareExchange("orders", ExchangeType.Topic));
                configurator.MapExchange<OrderPlaced>("orders");
                break;
            case ShapeDeclareExchangeGeneric:
                configurator.ConfigureTopology(t => t.DeclareExchange<OrderPlaced>("orders", ExchangeType.Topic));
                break;
            case ShapePublish:
                configurator.ConfigureTopology(t => t.DeclareExchange("orders", ExchangeType.Topic));
                configurator.Publish<OrderPlaced>(p => p.Exchange("orders"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown registration shape.");
        }

        return configurator.Build();
    }

    // ── Shape invariance: every registration shape yields the same resolved exchange ──────────

    [Theory]
    [InlineData(ShapeMapExchange)]
    [InlineData(ShapeDeclareExchangeGeneric)]
    [InlineData(ShapePublish)]
    public void TransportResolver_ForMappedType_ResolvesSameExchange_RegardlessOfShape(string shape)
    {
        // Arrange
        RabbitMqTransportOptions options = BuildOptionsMappingOrderPlacedToOrders(shape);

        // Act
        var resolver = new TransportExchangeResolver(options.ExchangeMappings);

        // Assert — identical runtime resolution irrespective of which API set the mapping.
        resolver.Resolve<OrderPlaced>().Should().Be("orders");
    }

    // ── Mapping > DefaultExchange: a mapped type resolves non-null (so the adapter uses it over
    //    DefaultExchange); an unmapped type resolves null (so the adapter falls back to DefaultExchange) ─

    [Fact]
    public void TransportResolver_UnmappedType_ResolvesNull_DefersToDefaultExchange()
    {
        // Arrange — only OrderPlaced is mapped.
        RabbitMqTransportOptions options = BuildOptionsMappingOrderPlacedToOrders(ShapePublish);

        // Act
        var resolver = new TransportExchangeResolver(options.ExchangeMappings);

        // Assert — null is the documented "defer to BW-Exchange header / DefaultExchange" signal.
        resolver.Resolve<OrderPlaced>().Should().Be("orders");
        resolver.Resolve<Unmapped>().Should().BeNull();
    }

    // ── header > mapping (real bus path): explicit BW-Exchange header wins for every shape ─────

    [Theory]
    [InlineData(ShapeMapExchange)]
    [InlineData(ShapeDeclareExchangeGeneric)]
    [InlineData(ShapePublish)]
    public async Task PublishAsync_ExplicitHeader_WinsOverMapping_RegardlessOfShape(string shape)
    {
        // Arrange — feed the bus the SAME mapping the transport would (snapshot → core resolver).
        RabbitMqTransportOptions options = BuildOptionsMappingOrderPlacedToOrders(shape);
        IExchangeResolver resolver = new CoreExchangeResolver(options.ExchangeMappings);

        await using BareWireTestHarness harness = await BareWireTestHarness.CreateAsync(
            exchangeResolver: resolver);

        Task<OutboundMessage> observe = harness.WaitForPublishAsync<OrderPlaced>(TestTimeout);
        var headers = new Dictionary<string, string> { ["BW-Exchange"] = "explicit.override" };

        // Act
        await harness.Bus.PublishAsync(new OrderPlaced("O-1"), headers);
        OutboundMessage sent = await observe;

        // Assert — caller header beats the per-type mapping, no matter which shape registered it.
        sent.Headers.Should().ContainKey("BW-Exchange").WhoseValue.Should().Be("explicit.override");
    }

    // ── mapping > DefaultExchange (real bus path): mapping injects BW-Exchange for every shape ─

    [Theory]
    [InlineData(ShapeMapExchange)]
    [InlineData(ShapeDeclareExchangeGeneric)]
    [InlineData(ShapePublish)]
    public async Task PublishAsync_NoHeader_InjectsMappedExchange_RegardlessOfShape(string shape)
    {
        // Arrange
        RabbitMqTransportOptions options = BuildOptionsMappingOrderPlacedToOrders(shape);
        IExchangeResolver resolver = new CoreExchangeResolver(options.ExchangeMappings);

        await using BareWireTestHarness harness = await BareWireTestHarness.CreateAsync(
            exchangeResolver: resolver);

        Task<OutboundMessage> observe = harness.WaitForPublishAsync<OrderPlaced>(TestTimeout);

        // Act — no caller header → the mapping supplies BW-Exchange (which then beats DefaultExchange).
        await harness.Bus.PublishAsync(new OrderPlaced("O-1"));
        OutboundMessage sent = await observe;

        // Assert — identical injected value across all shapes.
        sent.Headers.Should().ContainKey("BW-Exchange").WhoseValue.Should().Be("orders");
    }

    // ── DefaultExchange fallback (real bus path): unmapped type injects no BW-Exchange header ──

    [Fact]
    public async Task PublishAsync_UnmappedType_DoesNotInjectBwExchange_AdapterUsesDefaultExchange()
    {
        // Arrange — OrderPlaced mapped, Unmapped not; default resolver returns null for Unmapped.
        RabbitMqTransportOptions options = BuildOptionsMappingOrderPlacedToOrders(ShapePublish);
        IExchangeResolver resolver = new CoreExchangeResolver(options.ExchangeMappings);

        await using BareWireTestHarness harness = await BareWireTestHarness.CreateAsync(
            exchangeResolver: resolver);

        Task<OutboundMessage> observe = harness.WaitForPublishAsync<Unmapped>(TestTimeout);

        // Act
        await harness.Bus.PublishAsync(new Unmapped("U-1"));
        OutboundMessage sent = await observe;

        // Assert — no BW-Exchange injected → downstream the adapter falls back to DefaultExchange.
        sent.Headers.Should().NotContainKey("BW-Exchange");
    }
}
