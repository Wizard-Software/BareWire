using System.Globalization;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ.Internal;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.RabbitMQ;

internal sealed partial class RabbitMqTransportAdapter
{
    /// <summary>
    /// Reports every consumer that has waited for automatic recovery for at least the recovery health timeout as a
    /// <see cref="BusStatus.Degraded"/> endpoint; with no such consumer the transport is
    /// <see cref="BusStatus.Healthy"/> and contributes no endpoints.
    /// </summary>
    /// <remarks>
    /// After a lost connection RabbitMQ.Client recovers the channel and re-registers the consumer; until then the
    /// endpoint receives nothing. If the connection never comes back, or the consumer cannot be re-registered (for
    /// example its queue was deleted), the endpoint would otherwise wait silently. Cost: one pass over the active
    /// consumers through the dictionary enumerator (no lock, no snapshot), one volatile read per consumer, and a
    /// list allocation only when a consumer is overdue. Descriptions carry metadata only (queue name, time waited,
    /// connection state). Never throws and never logs; transitions are logged by the consumer's overdue timer.
    /// </remarks>
    public BusHealthStatus GetHealth()
    {
        List<EndpointHealthStatus>? degraded = null;

        try
        {
            foreach (KeyValuePair<string, RabbitMqConsumer> entry in _activeConsumers)
            {
                RabbitMqConsumer consumer = entry.Value;
                if (!consumer.IsRecoveryOverdue(out TimeSpan waited))
                {
                    continue;
                }

                string state = IsConnectionOpenSafe(consumer)
                    ? "the connection is open but the consumer has not been re-registered"
                    : "the connection has not been re-established";
                string description = string.Create(
                    CultureInfo.InvariantCulture,
                    $"waiting {(long)waited.TotalSeconds} s for automatic recovery; {state}");

                degraded ??= [];
                degraded.Add(new EndpointHealthStatus(consumer.EndpointName, BusStatus.Degraded, description));
            }
        }
        catch (Exception)
        {
            // Never throw from a health probe: report what was collected so far.
        }

        if (degraded is null)
        {
            return new BusHealthStatus(BusStatus.Healthy, "All RabbitMQ consumers are registered.", []);
        }

        string aggregate = string.Create(
            CultureInfo.InvariantCulture,
            $"{degraded.Count} RabbitMQ consumer(s) waiting for automatic recovery for at least " +
            $"{(long)_options.EffectiveRecoveryHealthTimeout.TotalSeconds} s.");
        return new BusHealthStatus(BusStatus.Degraded, aggregate, degraded);
    }

    private static bool IsConnectionOpenSafe(RabbitMqConsumer consumer)
    {
        try
        {
            return consumer.IsConnectionOpen?.Invoke() ?? false;
        }
        catch (Exception)
        {
            // A disposed or broken connection is reported as not re-established.
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "RabbitMQ consumer of endpoint '{EndpointName}' has been waiting {WaitedSeconds} s for automatic " +
                  "recovery and receives no messages; the endpoint is reported as degraded until the client " +
                  "re-registers the consumer.")]
    private partial void LogConsumerRecoveryOverdue(string endpointName, long waitedSeconds);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "RabbitMQ consumer of endpoint '{EndpointName}' was recovered after {WaitedSeconds} s.")]
    private partial void LogConsumerRecoveredAfterOverdue(string endpointName, long waitedSeconds);
}
