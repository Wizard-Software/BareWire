using System.Reflection;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Saga;
using BareWire.Saga.Scheduling;
using Microsoft.Extensions.Logging;

namespace BareWire.Saga;

internal sealed partial class StateMachineExecutor<TSaga>
    where TSaga : class, ISagaState, new()
{
    private readonly StateMachineDefinition<TSaga> _definition;
    private readonly ISagaRepository<TSaga> _repository;
    private readonly ILogger<StateMachineExecutor<TSaga>> _logger;
    private readonly IScheduleProvider? _scheduleProvider;
    private readonly int _maxRetries;

    internal StateMachineExecutor(
        StateMachineDefinition<TSaga> definition,
        ISagaRepository<TSaga> repository,
        ILogger<StateMachineExecutor<TSaga>> logger,
        IScheduleProvider? scheduleProvider = null,
        int maxRetries = 3)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(logger);
        _definition = definition;
        _repository = repository;
        _logger = logger;
        _scheduleProvider = scheduleProvider;
        _maxRetries = maxRetries;
    }

    internal async Task ProcessEventAsync<TEvent>(
        TEvent @event,
        ConsumeContext consumeContext,
        CancellationToken cancellationToken = default)
        where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(consumeContext);

        var correlationId = _definition.GetCorrelationId(@event);
        int lastVersion = 0;

        for (int attempt = 0; attempt <= _maxRetries; attempt++)
        {
            var saga = await _repository.FindAsync(correlationId, cancellationToken).ConfigureAwait(false);
            bool isNew = saga is null;

            if (isNew)
            {
                saga = new TSaga { CorrelationId = correlationId, CurrentState = "Initial" };
            }

            lastVersion = saga!.Version;

            var activities = _definition.GetActivities<TEvent>(saga.CurrentState);
            if (activities is null)
            {
                LogNoHandler(_logger, typeof(TEvent).Name, saga.CurrentState, typeof(TSaga).Name, correlationId);
                return;
            }

            var context = new BehaviorContext<TSaga, TEvent>(saga, @event, consumeContext);

            foreach (var step in activities)
            {
                await step.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            }

            if (context.TargetState is not null)
            {
                saga.CurrentState = context.TargetState;
            }

            try
            {
                if (context.ShouldFinalize)
                {
                    await _repository.DeleteAsync(correlationId, cancellationToken).ConfigureAwait(false);
                }
                else if (isNew)
                {
                    await _repository.SaveAsync(saga, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _repository.UpdateAsync(saga, cancellationToken).ConfigureAwait(false);
                }

                // Execute buffered side-effects after persist (persist-first pattern).
                // MVP: if these fail, the event will be nack'd and redelivered.
                foreach (var action in context.PendingActions)
                {
                    await action(consumeContext).ConfigureAwait(false);
                }

                // Dispatch scheduled timeouts and cancellations via IScheduleProvider.
                if (_scheduleProvider is not null)
                {
                    foreach (var timeout in context.ScheduledTimeouts)
                    {
                        await DispatchScheduledTimeoutAsync(timeout, cancellationToken).ConfigureAwait(false);
                    }

                    foreach (var cancelled in context.CancelledTimeouts)
                    {
                        await DispatchCancelledTimeoutAsync(cancelled, correlationId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                return; // Success
            }
            catch (ConcurrencyException) when (attempt < _maxRetries)
            {
                LogConcurrencyConflict(_logger, typeof(TSaga).Name, correlationId, attempt + 1, _maxRetries);
            }
        }

        throw new ConcurrencyException(typeof(TSaga), correlationId, lastVersion, lastVersion);
    }

    private Task DispatchScheduledTimeoutAsync(ScheduledTimeout timeout, CancellationToken cancellationToken)
    {
        // Use reflection to invoke the generic ScheduleAsync<T> with the concrete message type.
        var scheduleMethod = typeof(IScheduleProvider)
            .GetMethod(nameof(IScheduleProvider.ScheduleAsync))!
            .MakeGenericMethod(timeout.MessageType);

        // Derive the destination queue name from the message type (convention: lowercase type name).
        var destinationQueue = timeout.MessageType.Name.ToLowerInvariant();

        return (Task)scheduleMethod.Invoke(
            _scheduleProvider,
            [timeout.Message, timeout.Delay, destinationQueue, cancellationToken])!;
    }

    private Task DispatchCancelledTimeoutAsync(
        CancelledTimeout cancelled,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        // Use reflection to invoke the generic CancelAsync<T> with the concrete message type.
        var cancelMethod = typeof(IScheduleProvider)
            .GetMethod(nameof(IScheduleProvider.CancelAsync))!
            .MakeGenericMethod(cancelled.MessageType);

        return (Task)cancelMethod.Invoke(
            _scheduleProvider,
            [correlationId, cancellationToken])!;
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "No handler for {EventType} in state {State} for saga {SagaType} ({CorrelationId})")]
    private static partial void LogNoHandler(
        ILogger logger, string eventType, string state, string sagaType, Guid correlationId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Concurrency conflict on saga {SagaType} ({CorrelationId}), retry {Attempt}/{MaxRetries}")]
    private static partial void LogConcurrencyConflict(
        ILogger logger, string sagaType, Guid correlationId, int attempt, int maxRetries);
}
