using BareWire.Transport.Kafka.Internal;

namespace BareWire.Transport.Kafka.Configuration;

/// <summary>
/// Default <see cref="IKafkaRetryDlqConfigurator"/> implementation. Accumulates fluent settings and
/// materialises a validated <see cref="KafkaRetryDlqOptions"/> via <see cref="Build"/>.
/// </summary>
internal sealed class KafkaRetryDlqConfigurator : IKafkaRetryDlqConfigurator
{
    private bool _enabled;
    private int? _maxRetries;
    private string? _retryTopicSuffix;
    private string? _dlqTopicSuffix;
    private TimeSpan? _baseDelay;
    private double? _backoffMultiplier;
    private TimeSpan? _maxDelay;

    public void Enable() => _enabled = true;

    public void MaxRetries(int maxRetries) => _maxRetries = maxRetries;

    public void RetryTopicSuffix(string suffix)
    {
        ArgumentException.ThrowIfNullOrEmpty(suffix);
        _retryTopicSuffix = suffix;
    }

    public void DlqTopicSuffix(string suffix)
    {
        ArgumentException.ThrowIfNullOrEmpty(suffix);
        _dlqTopicSuffix = suffix;
    }

    public void Backoff(TimeSpan baseDelay, double multiplier, TimeSpan maxDelay)
    {
        _baseDelay = baseDelay;
        _backoffMultiplier = multiplier;
        _maxDelay = maxDelay;
    }

    /// <summary>
    /// Builds the <see cref="KafkaRetryDlqOptions"/> from the accumulated settings. Validates only
    /// when the pattern was enabled (a disabled instance carries defaults and is never used).
    /// </summary>
    internal KafkaRetryDlqOptions Build()
    {
        var options = new KafkaRetryDlqOptions { Enabled = _enabled };

        if (_maxRetries.HasValue)
        {
            options.MaxRetryCount = _maxRetries.Value;
        }

        if (_retryTopicSuffix is not null)
        {
            options.RetryTopicSuffix = _retryTopicSuffix;
        }

        if (_dlqTopicSuffix is not null)
        {
            options.DlqTopicSuffix = _dlqTopicSuffix;
        }

        if (_baseDelay.HasValue)
        {
            options.BaseDelay = _baseDelay.Value;
        }

        if (_backoffMultiplier.HasValue)
        {
            options.BackoffMultiplier = _backoffMultiplier.Value;
        }

        if (_maxDelay.HasValue)
        {
            options.MaxDelay = _maxDelay.Value;
        }

        if (options.Enabled)
        {
            options.Validate();
        }

        return options;
    }
}
