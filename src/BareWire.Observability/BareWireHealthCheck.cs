using BareWire.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BareWire.Observability;

/// <summary>
/// Health check that reports the operational status of the BareWire bus by delegating to
/// <see cref="IBusControl.CheckHealth"/>. Registered under the <c>"barewire"</c> tag group.
/// </summary>
/// <remarks>
/// Reports bus and per-endpoint status only. No connection strings, credentials, or secrets
/// are included in the health check data (SEC-06 compliance).
/// </remarks>
internal sealed class BareWireHealthCheck(IBusControl busControl) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var health = busControl.CheckHealth();

        var status = health.Status switch
        {
            BusStatus.Healthy   => HealthStatus.Healthy,
            BusStatus.Degraded  => HealthStatus.Degraded,
            BusStatus.Unhealthy => HealthStatus.Unhealthy,
            _                   => HealthStatus.Unhealthy,
        };

        var data = new Dictionary<string, object>
        {
            ["bus_status"]  = health.Status.ToString(),
            ["description"] = health.Description,
        };

        var unhealthyEndpoints = health.Endpoints
            .Where(e => e.Status is not BusStatus.Healthy)
            .ToList();

        if (unhealthyEndpoints.Count > 0)
        {
            var endpointData = unhealthyEndpoints
                .Select(e => new { name = e.EndpointName, status = e.Status.ToString() })
                .ToList<object>();

            data["endpoints"] = endpointData;
        }

        var result = new HealthCheckResult(status, health.Description, data: data);
        return Task.FromResult(result);
    }
}
