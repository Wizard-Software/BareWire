using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// Hosted service that logs, once at startup, the in-memory transport's at-most-once delivery
/// guarantee — and, when applicable, a separate warning about a transactional outbox without an inbox
/// feeding consumers of fanout or topic queues. Registered by <c>AddBareWireInMemory</c> via
/// <c>services.AddHostedService(...)</c>, so it runs once per container regardless of how many times
/// <c>AddBareWireInMemory</c> is called on the same <see cref="Microsoft.Extensions.DependencyInjection.IServiceCollection"/>.
/// </summary>
/// <remarks>
/// <para>
/// When the resolved <see cref="Abstractions.Transport.ITransportAdapter"/> is not the in-memory adapter
/// (another transport won the <c>TryAddSingleton</c> race), the registry passed to the constructor is
/// <see langword="null"/> and <see cref="Emit"/> logs nothing — the in-memory transport is not in use.
/// </para>
/// <para>
/// The <c>_emitted</c> guard is an instance field of this singleton, not static state: a second
/// <see cref="StartAsync"/> call (for example, a re-started generic host sharing the same container) does
/// not log the diagnostics again.
/// </para>
/// </remarks>
internal sealed partial class InMemoryStartupDiagnostics(
    InMemoryTransportOptions options,
    ExchangeRegistry? registry,
    OutboxRegistrationState outbox,
    IHostEnvironment? environment,
    ILogger<InMemoryStartupDiagnostics> logger) : IHostedService
{
    private int _emitted;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Emit();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Logs the startup diagnostics on the first call only. Does nothing (and returns
    /// <see langword="false"/>) when the in-memory adapter is not the active transport, or when this is
    /// not the first call.
    /// </summary>
    /// <returns><see langword="true"/> when this call logged the diagnostics.</returns>
    internal bool Emit()
    {
        if (Interlocked.Exchange(ref _emitted, 1) != 0)
        {
            return false;
        }

        if (registry is null)
        {
            return false;
        }

        LogLevel guaranteeLevel = ResolveGuaranteeLogLevel(environment);
        if (logger.IsEnabled(guaranteeLevel))
        {
            LogDeliveryGuarantee(logger, guaranteeLevel, environment?.EnvironmentName ?? "(not registered)");
        }

        if (outbox is { OutboxRegistered: true, InboxRegistered: false })
        {
            List<string> consumerQueueNames = [.. options.EndpointConfigurations
                .Where(static e => e.ConsumerRegistrations.Count > 0
                    || e.RawConsumerTypes.Count > 0
                    || e.SagaTypes.Count > 0)
                .Select(static e => e.QueueName)];

            IReadOnlyList<string> fanOutQueues = FanOutQueueResolver.FindConsumerQueues(registry, consumerQueueNames);
            if (fanOutQueues.Count > 0)
            {
                LogFanOutWithoutInbox(logger, string.Join(", ", fanOutQueues));
            }
        }

        return true;
    }

    /// <summary>
    /// Resolves the log level for the delivery-guarantee notice: <see cref="LogLevel.Warning"/> when
    /// <paramref name="environment"/> is registered and reports <c>Production</c> (via
    /// <see cref="HostEnvironmentEnvExtensions.IsProduction"/>); <see cref="LogLevel.Information"/>
    /// otherwise, including when no <see cref="IHostEnvironment"/> is registered.
    /// </summary>
    internal static LogLevel ResolveGuaranteeLogLevel(IHostEnvironment? environment) =>
        environment is not null && environment.IsProduction() ? LogLevel.Warning : LogLevel.Information;

    [LoggerMessage(EventId = 1, Message =
        "BareWire is using the in-memory transport (environment: {EnvironmentName}). Delivery is at-most-once " +
        "within a single process: messages still in in-memory queues are lost when the process stops or restarts, " +
        "and a message rejected by a full queue is lost unless it was published through the transactional outbox. " +
        "Use a message broker transport when at-least-once delivery is required.")]
    private static partial void LogDeliveryGuarantee(ILogger logger, LogLevel level, string environmentName);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message =
        "The transactional outbox is enabled with the in-memory transport, but no inbox is registered for the " +
        "consumers of queues fed by fanout or topic exchanges: {QueueNames}. An outbox retry of a message that " +
        "some of these queues already accepted delivers it to them again; register an inbox to deduplicate by " +
        "message id.")]
    private static partial void LogFanOutWithoutInbox(ILogger logger, string queueNames);
}
