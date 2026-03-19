namespace BareWire.Saga.Scheduling;

internal interface IScheduleProvider
{
    Task ScheduleAsync<T>(T message, TimeSpan delay, string destinationQueue,
        CancellationToken cancellationToken = default) where T : class;

    Task CancelAsync<T>(Guid correlationId, CancellationToken cancellationToken = default)
        where T : class;
}
