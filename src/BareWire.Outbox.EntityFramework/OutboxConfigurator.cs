using BareWire.Abstractions.Outbox;
using BareWire.Outbox;

namespace BareWire.Outbox.EntityFramework;

/// <summary>
/// Mutable configurator that accumulates outbox settings specified by the caller
/// and produces a validated <see cref="OutboxOptions"/> instance.
/// </summary>
internal sealed class OutboxConfigurator : IOutboxConfigurator
{
    private TimeSpan _pollingInterval = OutboxOptions.Default.PollingInterval;
    private int _dispatchBatchSize = OutboxOptions.Default.DispatchBatchSize;
    private TimeSpan _inboxRetention = OutboxOptions.Default.InboxRetention;
    private TimeSpan _outboxRetention = OutboxOptions.Default.OutboxRetention;
    private TimeSpan _inboxLockTimeout = OutboxOptions.Default.InboxLockTimeout;
    private TimeSpan _outboxLockTimeout = OutboxOptions.Default.OutboxLockTimeout;
    private TimeSpan _cleanupInterval = OutboxOptions.Default.CleanupInterval;
    private bool _allowNonAtomicProvider = OutboxOptions.Default.AllowNonAtomicProvider;
    private bool _allowDegradedOrdering = OutboxOptions.Default.AllowDegradedOrdering;
    private bool _autoCreateSchema = OutboxOptions.Default.AutoCreateSchema;
    private OrderingMode _orderingMode = OutboxOptions.Default.OrderingMode;
    private string _orderingKeyHeaderName = OutboxOptions.Default.OrderingKeyHeaderName ?? string.Empty;

    /// <inheritdoc />
    public TimeSpan PollingInterval
    {
        get => _pollingInterval;
        set => _pollingInterval = value;
    }

    /// <inheritdoc />
    public int DispatchBatchSize
    {
        get => _dispatchBatchSize;
        set => _dispatchBatchSize = value;
    }

    /// <inheritdoc />
    public TimeSpan InboxRetention
    {
        get => _inboxRetention;
        set => _inboxRetention = value;
    }

    /// <inheritdoc />
    public TimeSpan OutboxRetention
    {
        get => _outboxRetention;
        set => _outboxRetention = value;
    }

    /// <inheritdoc />
    public TimeSpan InboxLockTimeout
    {
        get => _inboxLockTimeout;
        set => _inboxLockTimeout = value;
    }

    /// <inheritdoc />
    public TimeSpan OutboxLockTimeout
    {
        get => _outboxLockTimeout;
        set => _outboxLockTimeout = value;
    }

    /// <inheritdoc />
    public TimeSpan CleanupInterval
    {
        get => _cleanupInterval;
        set => _cleanupInterval = value;
    }

    /// <inheritdoc />
    public bool AllowNonAtomicProvider
    {
        get => _allowNonAtomicProvider;
        set => _allowNonAtomicProvider = value;
    }

    /// <inheritdoc />
    public bool AllowDegradedOrdering
    {
        get => _allowDegradedOrdering;
        set => _allowDegradedOrdering = value;
    }

    /// <inheritdoc />
    public bool AutoCreateSchema
    {
        get => _autoCreateSchema;
        set => _autoCreateSchema = value;
    }

    /// <inheritdoc />
    public OrderingMode OrderingMode
    {
        get => _orderingMode;
        set => _orderingMode = value;
    }

    /// <inheritdoc />
    public string OrderingKeyHeaderName
    {
        get => _orderingKeyHeaderName;
        set => _orderingKeyHeaderName = value;
    }

    /// <summary>
    /// Builds and validates the <see cref="OutboxOptions"/> from the accumulated configuration.
    /// </summary>
    /// <exception cref="BareWire.Abstractions.Exceptions.BareWireConfigurationException">
    /// Thrown when any option value is out of its valid range.
    /// </exception>
    internal OutboxOptions Build()
    {
        var options = new OutboxOptions
        {
            AllowNonAtomicProvider = _allowNonAtomicProvider,
            AllowDegradedOrdering = _allowDegradedOrdering,
            PollingInterval = _pollingInterval,
            DispatchBatchSize = _dispatchBatchSize,
            InboxRetention = _inboxRetention,
            OutboxRetention = _outboxRetention,
            InboxLockTimeout = _inboxLockTimeout,
            OutboxLockTimeout = _outboxLockTimeout,
            CleanupInterval = _cleanupInterval,
            AutoCreateSchema = _autoCreateSchema,
            OrderingMode = _orderingMode,
            OrderingKeyHeaderName = string.IsNullOrEmpty(_orderingKeyHeaderName) ? null : _orderingKeyHeaderName,
        };

        options.Validate();
        return options;
    }
}
