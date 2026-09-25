using BareWire.Samples.InMemoryModularMonolith.Messaging;
using BareWire.Samples.InMemoryModularMonolith.Modules.Ordering;
using BareWire.Samples.InMemoryModularMonolith.Modules.Shipping;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BareWire.Samples.InMemoryModularMonolith.Smoke;

/// <summary>
/// Runs a bounded end-to-end smoke check when <c>Smoke:Enabled</c> is <see langword="true"/>: places
/// <c>Smoke:OrderCount</c> orders through the same <see cref="OrderingModule"/> path <c>POST /orders</c>
/// uses, waits for every order to reach <see cref="ShipmentStatus.ReadyToShip"/>, then stops the host.
/// Requires no HTTP client and no broker — it drives the modules directly, so it also proves the
/// in-memory transport end-to-end without Docker.
/// </summary>
/// <param name="ordering">Places orders — the same entry point the HTTP endpoint uses.</param>
/// <param name="board">Polled for each order's readiness.</param>
/// <param name="lifetime">Used to wait for startup and to stop the host when the run finishes.</param>
/// <param name="configuration">Supplies <c>Smoke:*</c> and <c>Transport</c> configuration.</param>
/// <param name="logger">Logs the pass/fail outcome.</param>
internal sealed partial class SmokeRunner(
    OrderingModule ordering,
    ShipmentBoard board,
    IHostApplicationLifetime lifetime,
    IConfiguration configuration,
    ILogger<SmokeRunner> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await WaitForApplicationStartedAsync(stoppingToken).ConfigureAwait(false);

            int orderCount = configuration.GetValue("Smoke:OrderCount", 5);
            TimeSpan timeout = TimeSpan.FromSeconds(configuration.GetValue("Smoke:TimeoutSeconds", 30));
            var transport = TransportKindParser.Parse(configuration["Transport"]);

            using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            deadlineCts.CancelAfter(timeout);

            var orderIds = new List<string>(orderCount);
            for (int i = 0; i < orderCount; i++)
            {
                string orderId = await ordering
                    .PlaceOrderAsync($"smoke-customer-{i}", 10m + i, deadlineCts.Token)
                    .ConfigureAwait(false);
                orderIds.Add(orderId);
            }

            await WaitForReadyToShipAsync(orderIds, deadlineCts.Token).ConfigureAwait(false);

            LogSmokeRunPassed(logger, orderCount, transport);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is shutting down before the smoke run reached its own deadline — not a smoke
            // failure; ExitCode is left at its default (0) and StopApplication below is a no-op.
        }
        catch (OperationCanceledException ex)
        {
            Environment.ExitCode = 1;
            LogSmokeRunFailed(logger, "timed out waiting for orders to reach ReadyToShip", ex);
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    /// <summary>
    /// Waits for <see cref="IHostApplicationLifetime.ApplicationStarted"/>, cancellable by
    /// <paramref name="stoppingToken"/> so a host that stops before starting does not hang this wait.
    /// </summary>
    private async Task WaitForApplicationStartedAsync(CancellationToken stoppingToken)
    {
        if (lifetime.ApplicationStarted.IsCancellationRequested)
        {
            return;
        }

        var startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using CancellationTokenRegistration startedRegistration = lifetime.ApplicationStarted.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(), startedTcs);
        await using CancellationTokenRegistration stoppingRegistration = stoppingToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetCanceled(), startedTcs);

        await startedTcs.Task.ConfigureAwait(false);
    }

    private async Task WaitForReadyToShipAsync(IReadOnlyList<string> orderIds, CancellationToken cancellationToken)
    {
        while (true)
        {
            bool allReady = true;
            foreach (string orderId in orderIds)
            {
                if (board.GetStatus(orderId) != ShipmentStatus.ReadyToShip)
                {
                    allReady = false;
                    break;
                }
            }

            if (allReady)
            {
                return;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Smoke run passed: {OrderCount} orders reached ReadyToShip via {Transport}")]
    private static partial void LogSmokeRunPassed(ILogger logger, int orderCount, TransportKind transport);

    [LoggerMessage(Level = LogLevel.Error, Message = "Smoke run failed: {Reason}")]
    private static partial void LogSmokeRunFailed(ILogger logger, string reason, Exception exception);
}
