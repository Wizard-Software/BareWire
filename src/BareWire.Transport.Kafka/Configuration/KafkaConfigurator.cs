using BareWire.Abstractions.Configuration;
using BareWire.Transport.Kafka.Internal;
using Confluent.Kafka;

namespace BareWire.Transport.Kafka.Configuration;

internal sealed class KafkaConfigurator : IKafkaConfigurator
{
    private readonly List<KafkaEndpointConfiguration> _endpoints = [];
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

    public void ReceiveEndpoint(string topicName, Action<IReceiveEndpointConfigurator> configure)
    {
        ArgumentException.ThrowIfNullOrEmpty(topicName);
        ArgumentNullException.ThrowIfNull(configure);

        var endpoint = new KafkaEndpointConfiguration(topicName);
        configure(endpoint);
        _endpoints.Add(endpoint);
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

        options.EndpointConfigurations = _endpoints.ToArray();

        // Fail fast on a missing consumer group when endpoints are declared, instead of failing later
        // inside the consume loop.
        if (_endpoints.Count > 0)
        {
            options.ValidateConsumer();
        }

        options.Validate();

        return options;
    }
}
