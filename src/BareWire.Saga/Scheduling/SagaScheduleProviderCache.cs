using BareWire.Abstractions;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using Microsoft.Extensions.Logging;

namespace BareWire.Saga.Scheduling;

/// <summary>
/// Holds the single <see cref="IScheduleProvider"/> shared by every event of one saga type, so a
/// timeout scheduled by one event can be cancelled by a later one.
/// </summary>
/// <remarks>
/// Sharing only applies to the native-scheduler path (<see cref="TransportNativeScheduleProvider"/>):
/// its token map is an instance field, so all events of a saga must see the same instance for
/// cross-event cancellation to find the token. <see cref="DelayRequeueScheduleProvider"/> is
/// deliberately <b>not</b> cached — it is created for every event. It declares its delay queue
/// lazily and records it as declared only after the declaration succeeded, so every event that
/// schedules a timeout deploys the delay queue topology.
/// </remarks>
internal sealed class SagaScheduleProviderCache
{
    private IScheduleProvider? _provider;

    /// <summary>
    /// Returns the shared native schedule provider for this saga type, creating it lazily on the
    /// first call, when <paramref name="transport"/> implements <see cref="INativeMessageScheduler"/>.
    /// Otherwise returns a fresh <see cref="DelayRequeueScheduleProvider"/> on every call.
    /// </summary>
    internal IScheduleProvider GetOrCreate(
        ITransportAdapter transport,
        IMessageSerializer serializer,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        if (transport is not INativeMessageScheduler)
        {
            // DelayRequeue is deliberately not shared — see the <remarks> above.
            return ScheduleProviderFactory.Create(SchedulingStrategy.Auto, transport, loggerFactory, serializer);
        }

        IScheduleProvider? existing = Volatile.Read(ref _provider);
        if (existing is not null)
        {
            return existing;
        }

        IScheduleProvider created = ScheduleProviderFactory.Create(
            SchedulingStrategy.Auto, transport, loggerFactory, serializer);
        return Interlocked.CompareExchange(ref _provider, created, null) ?? created;
    }
}
