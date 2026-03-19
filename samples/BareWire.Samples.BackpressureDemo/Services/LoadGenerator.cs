using BareWire.Abstractions;
using BareWire.Samples.BackpressureDemo.Messages;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.BackpressureDemo.Services;

/// <summary>
/// A hosted background service that publishes <see cref="LoadTestMessage"/> instances at a
/// configurable rate. Start and stop generation via <see cref="StartGeneration"/> and
/// <see cref="StopGeneration"/> from the Minimal API endpoints.
///
/// When the consumer cannot keep up, ADR-006 publish-side back-pressure kicks in:
/// <c>PublishFlowControlOptions.MaxPendingPublishes</c> bounds the outgoing channel and
/// <c>PublishAsync</c> awaits until capacity is freed — naturally throttling this loop.
/// </summary>
public sealed partial class LoadGenerator(
    IPublishEndpoint publishEndpoint,
    ILogger<LoadGenerator> logger) : BackgroundService
{
    // Default target: 1 000 messages per second.
    private const int DefaultMessagesPerSecond = 1_000;

    private volatile bool _isRunning;
    private int _messagesPerSecond = DefaultMessagesPerSecond;

    private long _totalPublished;
    private long _totalErrors;
    private DateTime _generationStartedAt = DateTime.MinValue;

    /// <summary>Gets the total number of messages successfully published since the last start.</summary>
    public long TotalPublished => Interlocked.Read(ref _totalPublished);

    /// <summary>Gets the total number of publish errors since the last start.</summary>
    public long TotalErrors => Interlocked.Read(ref _totalErrors);

    /// <summary>Gets the UTC timestamp when generation last started, or <see cref="DateTime.MinValue"/> if never started.</summary>
    public DateTime GenerationStartedAt => _generationStartedAt;

    /// <summary>Indicates whether the load generator is currently producing messages.</summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Starts message generation at the specified rate.
    /// If already running, the rate is updated immediately.
    /// </summary>
    /// <param name="messagesPerSecond">
    /// Target publish rate in messages per second. Clamped to the range [1, 100_000].
    /// Defaults to <c>1 000</c>.
    /// </param>
    public void StartGeneration(int messagesPerSecond = DefaultMessagesPerSecond)
    {
        _messagesPerSecond = Math.Clamp(messagesPerSecond, 1, 100_000);
        Interlocked.Exchange(ref _totalPublished, 0L);
        Interlocked.Exchange(ref _totalErrors, 0L);
        _generationStartedAt = DateTime.UtcNow;
        _isRunning = true;

        LogGenerationStarted(logger, _messagesPerSecond);
    }

    /// <summary>Stops message generation. In-progress publishes complete normally.</summary>
    public void StopGeneration()
    {
        _isRunning = false;
        LogGenerationStopped(logger, TotalPublished, TotalErrors);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_isRunning)
            {
                // Poll every 200 ms while idle so we respond to StartGeneration promptly.
                await Task.Delay(200, stoppingToken).ConfigureAwait(false);
                continue;
            }

            int rate = _messagesPerSecond;
            long batchStart = TotalPublished;
            DateTime windowStart = DateTime.UtcNow;

            // Publish one second's worth of messages, then sleep for the remainder
            // of the second. If PublishAsync blocks due to ADR-006 back-pressure,
            // the loop naturally slows to match consumer capacity.
            for (int i = 0; i < rate && _isRunning && !stoppingToken.IsCancellationRequested; i++)
            {
                int seqNo = (int)(Interlocked.Read(ref _totalPublished) + 1);

                try
                {
                    await publishEndpoint
                        .PublishAsync(new LoadTestMessage(seqNo, DateTime.UtcNow), stoppingToken)
                        .ConfigureAwait(false);

                    Interlocked.Increment(ref _totalPublished);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _totalErrors);
                    LogPublishError(logger, seqNo, ex);
                }
            }

            // Log periodic rate statistics every window.
            long published = TotalPublished - batchStart;
            double elapsed = (DateTime.UtcNow - windowStart).TotalSeconds;
            double actualRate = elapsed > 0 ? published / elapsed : 0;
            LogRateStatistics(logger, published, actualRate, TotalPublished, TotalErrors);

            // Sleep for the remainder of the 1-second window (may be 0 if already exceeded).
            TimeSpan remaining = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - windowStart);
            if (remaining > TimeSpan.Zero && _isRunning && !stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(remaining, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Load generation started at {Rate} msg/s")]
    private static partial void LogGenerationStarted(ILogger logger, int rate);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Load generation stopped. Total published: {TotalPublished}, errors: {TotalErrors}")]
    private static partial void LogGenerationStopped(ILogger logger, long totalPublished, long totalErrors);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Failed to publish message #{SequenceNumber}")]
    private static partial void LogPublishError(ILogger logger, int sequenceNumber, Exception ex);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Rate window: {WindowPublished} msg in window ({ActualRate:F0} msg/s), " +
                  "cumulative: {TotalPublished} published, {TotalErrors} errors")]
    private static partial void LogRateStatistics(
        ILogger logger,
        long windowPublished,
        double actualRate,
        long totalPublished,
        long totalErrors);
}
