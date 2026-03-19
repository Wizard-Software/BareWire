using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Observability;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;

namespace BareWire.UnitTests.Observability;

/// <summary>
/// Unit tests for <see cref="BareWireHealthCheck"/> — verifies that bus health status is
/// correctly mapped to <see cref="HealthCheckResult"/> and that no sensitive data is leaked.
/// </summary>
public sealed class BareWireHealthCheckTests
{
    private readonly IBusControl _busControl = Substitute.For<IBusControl>();
    private readonly BareWireHealthCheck _sut;
    private readonly HealthCheckContext _context = new();

    public BareWireHealthCheckTests()
    {
        _sut = new BareWireHealthCheck(_busControl);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenBusHealthy_ReturnsHealthy()
    {
        // Arrange
        _busControl.CheckHealth().Returns(new BusHealthStatus(
            BusStatus.Healthy,
            "All systems operational.",
            []));

        // Act
        var result = await _sut.CheckHealthAsync(_context, CancellationToken.None);

        // Assert
        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenBusDegraded_ReturnsDegraded()
    {
        // Arrange
        _busControl.CheckHealth().Returns(new BusHealthStatus(
            BusStatus.Degraded,
            "One endpoint is slow.",
            []));

        // Act
        var result = await _sut.CheckHealthAsync(_context, CancellationToken.None);

        // Assert
        result.Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenBusUnhealthy_ReturnsUnhealthy()
    {
        // Arrange
        _busControl.CheckHealth().Returns(new BusHealthStatus(
            BusStatus.Unhealthy,
            "Transport connection lost.",
            []));

        // Act
        var result = await _sut.CheckHealthAsync(_context, CancellationToken.None);

        // Assert
        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task CheckHealthAsync_IncludesEndpointStatus_InData()
    {
        // Arrange
        _busControl.CheckHealth().Returns(new BusHealthStatus(
            BusStatus.Degraded,
            "Some endpoints are unhealthy.",
            [
                new EndpointHealthStatus("orders-queue", BusStatus.Unhealthy, "Consumer faulted."),
                new EndpointHealthStatus("payments-queue", BusStatus.Healthy, null),
            ]));

        // Act
        var result = await _sut.CheckHealthAsync(_context, CancellationToken.None);

        // Assert — data must contain the degraded endpoint but not the healthy one
        result.Data.Should().ContainKey("endpoints");

        var endpoints = result.Data["endpoints"] as IEnumerable<object>;
        endpoints.Should().NotBeNull();
        endpoints!.Should().HaveCount(1);

        // The endpoint entry must carry its name and status string
        var entry = endpoints!.First();
        var entryJson = System.Text.Json.JsonSerializer.Serialize(entry);
        entryJson.Should().Contain("orders-queue");
        entryJson.Should().Contain("Unhealthy");

        // The healthy endpoint must NOT appear in the data
        entryJson.Should().NotContain("payments-queue");
    }

    [Fact]
    public async Task CheckHealthAsync_NeverExposesConnectionStrings()
    {
        // Arrange — even if the description contained something that looks like a connection string
        _busControl.CheckHealth().Returns(new BusHealthStatus(
            BusStatus.Unhealthy,
            "Transport failed.",
            []));

        // Act
        var result = await _sut.CheckHealthAsync(_context, CancellationToken.None);

        // Assert — none of the data dictionary keys may reference sensitive information
        var sensitiveKeys = new[] { "connection_string", "password", "secret", "credential", "token", "key" };
        foreach (var sensitiveKey in sensitiveKeys)
        {
            result.Data.Keys.Should().NotContain(
                k => k.Contains(sensitiveKey, StringComparison.OrdinalIgnoreCase),
                because: $"key '{sensitiveKey}' would expose sensitive information (SEC-06)");
        }

        // Values must not contain anything labelled as connection string
        foreach (var value in result.Data.Values)
        {
            var valueStr = value?.ToString() ?? string.Empty;
            valueStr.Should().NotContainAny(
                ["amqp://", "amqps://", "Password=", "pwd="],
                because: "connection string fragments must never appear in health check data (SEC-06)");
        }
    }
}
