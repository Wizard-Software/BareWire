using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Outbox;

namespace BareWire.Outbox;

internal sealed record OutboxOptions
{
    internal static readonly OutboxOptions Default = new();

    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromSeconds(1);
    public int DispatchBatchSize { get; init; } = 100;
    public TimeSpan InboxRetention { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan OutboxRetention { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan InboxLockTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan OutboxLockTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromHours(1);
    public bool AutoCreateSchema { get; init; }
    public OrderingMode OrderingMode { get; init; } = OrderingMode.None;
    public string? OrderingKeyHeaderName { get; init; }

    internal void Validate()
    {
        if (PollingInterval <= TimeSpan.Zero)
        {
            throw new BareWireConfigurationException(
                $"{nameof(PollingInterval)} must be greater than zero. Got: {PollingInterval}");
        }

        if (DispatchBatchSize <= 0 || DispatchBatchSize > 10_000)
        {
            throw new BareWireConfigurationException(
                $"{nameof(DispatchBatchSize)} must be between 1 and 10,000. Got: {DispatchBatchSize}");
        }

        if (InboxRetention <= TimeSpan.Zero)
        {
            throw new BareWireConfigurationException(
                $"{nameof(InboxRetention)} must be greater than zero. Got: {InboxRetention}");
        }

        if (OutboxRetention <= TimeSpan.Zero)
        {
            throw new BareWireConfigurationException(
                $"{nameof(OutboxRetention)} must be greater than zero. Got: {OutboxRetention}");
        }

        if (InboxLockTimeout <= TimeSpan.Zero)
        {
            throw new BareWireConfigurationException(
                $"{nameof(InboxLockTimeout)} must be greater than zero. Got: {InboxLockTimeout}");
        }

        if (InboxRetention <= InboxLockTimeout)
        {
            throw new BareWireConfigurationException(
                $"{nameof(InboxRetention)} ({InboxRetention}) must be greater than " +
                $"{nameof(InboxLockTimeout)} ({InboxLockTimeout}) to prevent locks from expiring before cleanup.");
        }

        if (OutboxLockTimeout <= TimeSpan.Zero)
        {
            throw new BareWireConfigurationException(
                $"{nameof(OutboxLockTimeout)} must be greater than zero. Got: {OutboxLockTimeout}");
        }

        if (OutboxRetention <= OutboxLockTimeout)
        {
            throw new BareWireConfigurationException(
                $"{nameof(OutboxRetention)} ({OutboxRetention}) must be greater than " +
                $"{nameof(OutboxLockTimeout)} ({OutboxLockTimeout}) to prevent stale locks surviving cleanup.");
        }

        TimeSpan minLockTimeout = TimeSpan.FromTicks(3 * PollingInterval.Ticks);
        if (OutboxLockTimeout < minLockTimeout)
        {
            throw new BareWireConfigurationException(
                $"{nameof(OutboxLockTimeout)} ({OutboxLockTimeout}) must be >= 3 * PollingInterval " +
                $"({PollingInterval}) = {minLockTimeout} to survive at least one full poll-publish-confirm cycle. " +
                $"Got: {OutboxLockTimeout}");
        }

        if (CleanupInterval <= TimeSpan.Zero)
        {
            throw new BareWireConfigurationException(
                $"{nameof(CleanupInterval)} must be greater than zero. Got: {CleanupInterval}");
        }

        if (OrderingMode == OrderingMode.PerKey && string.IsNullOrWhiteSpace(OrderingKeyHeaderName))
        {
            throw new BareWireConfigurationException(
                "OrderingKeyHeaderName must be set to a non-empty header name when OrderingMode is PerKey.");
        }
    }
}
