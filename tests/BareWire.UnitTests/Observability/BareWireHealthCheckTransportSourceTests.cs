using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.Observability;
using BareWire.UnitTests.Core.Bus;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;

namespace BareWire.UnitTests.Observability;

/// <summary>
/// End-to-end unit tests for <see cref="BareWireHealthCheck"/> against a real
/// <see cref="BareWireBusControl"/> that consults an <see cref="ITransportHealthSource"/> seam —
/// verifies that transport-reported queue occupancy reaches the health check result without
/// leaking anything beyond queue metadata.
/// </summary>
public sealed class BareWireHealthCheckTransportSourceTests
{
    [Fact]
    public async Task CheckHealthAsync_WhenTransportQueueAbove90Percent_ReturnsDegradedWithQueueMetadataOnly()
    {
        ITransportAdapter adapter = Substitute.For<ITransportAdapter, ITransportHealthSource>();
        ((ITransportHealthSource)adapter).GetHealth().Returns(new BusHealthStatus(
            BusStatus.Degraded, "Queue 'orders' is at 92% of capacity (920/1000).",
            [new EndpointHealthStatus("orders", BusStatus.Degraded, "Queue 'orders' is at 92% of capacity (920/1000).")]));
        var (control, _) = BareWireBusControlHealthSourceTests.CreateControl(adapter);
        var sut = new BareWireHealthCheck(control);

        HealthCheckResult result = await sut.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Degraded);
        // Exact match: the description carries only the bus summary plus the transport's queue metadata.
        result.Description.Should().Be(
            "One or more endpoints are approaching capacity. Queue 'orders' is at 92% of capacity (920/1000).");
        result.Data.Should().ContainKey("endpoints");
        result.Data.Keys.Should().BeEquivalentTo(["bus_status", "description", "endpoints"]);
    }
}
