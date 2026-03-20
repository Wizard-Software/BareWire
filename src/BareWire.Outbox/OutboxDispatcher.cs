using BareWire.Abstractions.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BareWire.Outbox;

internal sealed partial class OutboxDispatcher : IHostedService, IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITransportAdapter _adapter;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxDispatcher> _logger;

    private CancellationTokenSource? _cts;
    private Task? _pollingTask;

    public OutboxDispatcher(
        IServiceScopeFactory scopeFactory,
        ITransportAdapter adapter,
        OutboxOptions options,
        ILogger<OutboxDispatcher> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollingTask = RunPollingLoopAsync(_cts.Token);
        LogDispatcherStarted(_logger, _options.PollingInterval, _options.DispatchBatchSize);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        LogDispatcherStopping(_logger);

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_pollingTask is not null)
        {
            try
            {
                await _pollingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on graceful shutdown — swallow.
            }
        }

        LogDispatcherStopped(_logger);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
            _cts = null;
        }
    }

    private async Task RunPollingLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.PollingInterval);

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await DispatchBatchAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogDispatchError(_logger, ex);
                // Continue — do not crash the hosted service on transient errors.
            }
        }
    }

    private async Task DispatchBatchAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IOutboxStore store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();

        IReadOnlyList<OutboxEntry> pending = await store
            .GetPendingAsync(_options.DispatchBatchSize, ct)
            .ConfigureAwait(false);

        if (pending.Count == 0)
        {
            return;
        }

        LogDispatching(_logger, pending.Count);

        OutboundMessage[] messages = new OutboundMessage[pending.Count];
        long[] ids = new long[pending.Count];

        for (int i = 0; i < pending.Count; i++)
        {
            OutboxEntry entry = pending[i];
            ids[i] = entry.Id;
            messages[i] = new OutboundMessage(
                routingKey: entry.RoutingKey,
                headers: entry.Headers,
                body: entry.PooledBody.AsMemory(0, entry.BodyLength),
                contentType: entry.ContentType);
        }

        IReadOnlyList<SendResult> results = await _adapter.SendBatchAsync(messages, ct).ConfigureAwait(false);

        // Only mark entries as delivered if the broker confirmed them.
        List<long> confirmedIds = new(pending.Count);
        int nackedCount = 0;

        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].IsConfirmed)
            {
                confirmedIds.Add(ids[i]);
            }
            else
            {
                nackedCount++;
            }
        }

        if (confirmedIds.Count > 0)
        {
            await store.MarkDeliveredAsync([.. confirmedIds], ct).ConfigureAwait(false);
        }

        if (nackedCount > 0)
        {
            LogPartialSendFailure(_logger, nackedCount, pending.Count);
        }

        LogDispatched(_logger, confirmedIds.Count);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "OutboxDispatcher started. PollingInterval={PollingInterval}, BatchSize={BatchSize}.")]
    private static partial void LogDispatcherStarted(
        ILogger logger,
        TimeSpan pollingInterval,
        int batchSize);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "OutboxDispatcher stopping.")]
    private static partial void LogDispatcherStopping(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "OutboxDispatcher stopped.")]
    private static partial void LogDispatcherStopped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dispatching {Count} pending outbox messages.")]
    private static partial void LogDispatching(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Successfully dispatched and marked {Count} outbox messages as delivered.")]
    private static partial void LogDispatched(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error during outbox dispatch batch. Will retry on next tick.")]
    private static partial void LogDispatchError(ILogger logger, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{NackedCount} of {TotalCount} outbox messages were not confirmed by the broker and will be retried.")]
    private static partial void LogPartialSendFailure(ILogger logger, int nackedCount, int totalCount);
}
