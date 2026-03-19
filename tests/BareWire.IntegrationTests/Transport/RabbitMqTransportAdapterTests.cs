using System.Text;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;

namespace BareWire.IntegrationTests.Transport;

/// <summary>
/// Integration tests for <see cref="RabbitMqTransportAdapter"/> lifecycle:
/// lazy connection init, graceful dispose, capabilities, and transport name.
/// All tests use a real RabbitMQ instance provisioned via <see cref="AspireFixture"/>.
/// </summary>
public sealed class RabbitMqTransportAdapterTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    // ── Factory ───────────────────────────────────────────────────────────────

    private RabbitMqTransportAdapter CreateAdapter(Action<RabbitMqTransportOptions>? configure = null)
    {
        var options = new RabbitMqTransportOptions
        {
            ConnectionString = fixture.GetRabbitMqConnectionString(),
        };
        configure?.Invoke(options);
        return new RabbitMqTransportAdapter(options, NullLogger<RabbitMqTransportAdapter>.Instance);
    }

    private static OutboundMessage MakeMessage(string routingKey = "test-lifecycle") =>
        new(
            routingKey: routingKey,
            headers: new Dictionary<string, string>(),
            body: Encoding.UTF8.GetBytes("{\"ping\":true}"),
            contentType: "application/json");

    private static async Task DeployMinimalTopologyAsync(
        RabbitMqTransportAdapter adapter,
        string queueName,
        CancellationToken ct)
    {
        var configurator = new RabbitMqTopologyConfigurator();
        configurator.DeclareQueue(queueName);
        TopologyDeclaration topology = configurator.Build();
        await adapter.DeployTopologyAsync(topology, ct);
    }

    // ── TransportName ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TransportName_ReturnsRabbitMQ()
    {
        // Arrange — no I/O needed; just verify the property before any connection is opened
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        // Act + Assert
        adapter.TransportName.Should().Be("RabbitMQ");
    }

    // ── Capabilities ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Capabilities_ReturnsExpectedFlags()
    {
        // Arrange — no I/O needed; capabilities are compile-time constants
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        // Act
        TransportCapabilities capabilities = adapter.Capabilities;

        // Assert
        capabilities.Should().HaveFlag(TransportCapabilities.PublisherConfirms);
        capabilities.Should().HaveFlag(TransportCapabilities.DlqNative);
        capabilities.Should().HaveFlag(TransportCapabilities.FlowControl);
    }

    // ── SendBatchAsync — lazy init ─────────────────────────────────────────────

    [Fact]
    public async Task SendBatchAsync_FirstCall_EstablishesConnection()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        await using RabbitMqTransportAdapter adapter = CreateAdapter();

        string queueName = $"test-lazy-init-{Guid.NewGuid():N}";
        await DeployMinimalTopologyAsync(adapter, queueName, cts.Token);

        OutboundMessage message = MakeMessage(routingKey: queueName);

        // Act — first call; connection is established lazily inside SendBatchAsync
        IReadOnlyList<SendResult> results = await adapter.SendBatchAsync([message], cts.Token);

        // Assert — send succeeded, meaning the connection was established and the broker confirmed
        results.Should().HaveCount(1);
        results[0].IsConfirmed.Should().BeTrue();
        results[0].DeliveryTag.Should().BeGreaterThan(0UL);
    }

    // ── DisposeAsync — graceful disconnect ────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_WhenConnected_DisconnectsGracefully()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        RabbitMqTransportAdapter adapter = CreateAdapter();

        string queueName = $"test-dispose-{Guid.NewGuid():N}";
        await DeployMinimalTopologyAsync(adapter, queueName, cts.Token);

        // Trigger lazy init so the connection is active
        OutboundMessage message = MakeMessage(routingKey: queueName);
        await adapter.SendBatchAsync([message], cts.Token);

        // Act — dispose should close the connection without throwing
        Func<Task> act = async () => await adapter.DisposeAsync();

        // Assert — no exception means graceful shutdown
        await act.Should().NotThrowAsync();
    }
}
