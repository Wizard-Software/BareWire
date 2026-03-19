using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Serialization;

namespace BareWire.Transport.RabbitMQ.Configuration;

internal sealed class RabbitMqEndpointConfiguration : IReceiveEndpointConfigurator
{
    private readonly List<Type> _consumerTypes = [];
    private readonly List<Type> _rawConsumerTypes = [];
    private readonly List<Type> _sagaTypes = [];

    internal RabbitMqEndpointConfiguration(string queueName)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueName);
        QueueName = queueName;
    }

    internal string QueueName { get; }

    internal IReadOnlyList<Type> ConsumerTypes => _consumerTypes;
    internal IReadOnlyList<Type> RawConsumerTypes => _rawConsumerTypes;
    internal IReadOnlyList<Type> SagaTypes => _sagaTypes;

    // ── IReceiveEndpointConfigurator ───────────────────────────────────────────

    public int PrefetchCount { get; set; } = 16;

    public int ConcurrentMessageLimit { get; set; } = 8;

    public bool ConfigureConsumeTopology { get; set; }

    public string? DefaultContentType { get; set; }

    public RawSerializerOptions RawSerializerOptions { get; set; } = RawSerializerOptions.None;

    public int RetryCount { get; set; }

    public TimeSpan RetryInterval { get; set; } = TimeSpan.Zero;

    public void Consumer<TConsumer, TMessage>()
        where TConsumer : class, IConsumer<TMessage>
        where TMessage : class
    {
        _consumerTypes.Add(typeof(TConsumer));
    }

    public void RawConsumer<T>() where T : class, IRawConsumer
    {
        _rawConsumerTypes.Add(typeof(T));
    }

    public void StateMachineSaga<TSaga>() where TSaga : class
    {
        _sagaTypes.Add(typeof(TSaga));
    }
}
