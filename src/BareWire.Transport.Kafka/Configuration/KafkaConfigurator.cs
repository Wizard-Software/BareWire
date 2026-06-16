using BareWire.Transport.Kafka.Internal;
using Confluent.Kafka;

namespace BareWire.Transport.Kafka.Configuration;

internal sealed class KafkaConfigurator : IKafkaConfigurator
{
    private string? _bootstrapServers;
    private string? _groupId;
    private AutoOffsetReset? _autoOffsetReset;
    private KafkaPartitionAssignmentStrategy? _partitionAssignmentStrategy;
    private KafkaRetryDlqOptions? _retryDlqOptions;

    public void BootstrapServers(string bootstrapServers)
    {
        ArgumentException.ThrowIfNullOrEmpty(bootstrapServers);
        _bootstrapServers = bootstrapServers;
    }

    public void ConsumerGroup(string groupId)
    {
        ArgumentException.ThrowIfNullOrEmpty(groupId);
        _groupId = groupId;
    }

    public void ConsumerAutoOffsetReset(AutoOffsetReset autoOffsetReset)
    {
        _autoOffsetReset = autoOffsetReset;
    }

    public void ConsumerPartitionAssignmentStrategy(KafkaPartitionAssignmentStrategy strategy)
    {
        _partitionAssignmentStrategy = strategy;
    }

    public void ConfigureRetryDlq(Action<IKafkaRetryDlqConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var retryDlqConfigurator = new KafkaRetryDlqConfigurator();
        configure(retryDlqConfigurator);
        _retryDlqOptions = retryDlqConfigurator.Build();
    }

    internal KafkaTransportOptions Build()
    {
        var options = new KafkaTransportOptions();

        if (_bootstrapServers is not null)
        {
            options.BootstrapServers = _bootstrapServers;
        }

        if (_groupId is not null)
        {
            options.GroupId = _groupId;
        }

        if (_autoOffsetReset.HasValue)
        {
            options.AutoOffsetReset = _autoOffsetReset.Value;
        }

        if (_partitionAssignmentStrategy.HasValue)
        {
            options.ConsumerPartitionAssignmentStrategy = _partitionAssignmentStrategy.Value;
        }

        if (_retryDlqOptions is not null)
        {
            options.RetryDlq = _retryDlqOptions;
        }

        options.Validate();

        return options;
    }
}
