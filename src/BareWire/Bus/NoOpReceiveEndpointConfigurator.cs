using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Serialization;

namespace BareWire.Bus;

/// <summary>
/// Minimal, do-nothing <see cref="IReceiveEndpointConfigurator"/> passed as the <c>endpoint</c> argument when
/// the core's start-up discovery invokes a <c>ConsumerDefinition&lt;TConsumer&gt;</c>'s <c>Configure</c>
/// method. Every property is a plain auto-property and every method is an empty no-op: endpoint-level
/// settings made through this argument (prefetch, concurrency, retry, serializer overrides, ordering, nested
/// consumer/saga registrations) are intentionally ignored — materializing them back into the owning
/// <see cref="EndpointBinding"/> is deferred to a later task.
/// </summary>
internal sealed class NoOpReceiveEndpointConfigurator : IReceiveEndpointConfigurator
{
    /// <inheritdoc />
    public int PrefetchCount { get; set; }

    /// <inheritdoc />
    public int ConcurrentMessageLimit { get; set; }

    /// <inheritdoc />
    public bool ConfigureConsumeTopology { get; set; }

    /// <inheritdoc />
    public string? DefaultContentType { get; set; }

    /// <inheritdoc />
    public RawSerializerOptions RawSerializerOptions { get; set; }

    /// <inheritdoc />
    public int RetryCount { get; set; }

    /// <inheritdoc />
    public TimeSpan RetryInterval { get; set; }

    /// <inheritdoc />
    public void Consumer<TConsumer>()
        where TConsumer : class
    {
        // No-op: endpoint-level consumer registration through the definition's endpoint argument is ignored.
    }

    /// <inheritdoc />
    public void Consumer<TConsumer, TMessage>()
        where TConsumer : class, IConsumer<TMessage>
        where TMessage : class
    {
        // No-op: endpoint-level consumer registration through the definition's endpoint argument is ignored.
    }

    /// <inheritdoc />
    public void Consumer<TConsumer, TMessage>(Action<IConsumerConfigurator<TConsumer, TMessage>> configure)
        where TConsumer : class, IConsumer<TMessage>
        where TMessage : class
    {
        // No-op: endpoint-level consumer registration through the definition's endpoint argument is ignored.
    }

    /// <inheritdoc />
    public void RawConsumer<T>() where T : class, IRawConsumer
    {
        // No-op: endpoint-level raw consumer registration through the definition's endpoint argument is ignored.
    }

    /// <inheritdoc />
    public void StateMachineSaga<TSaga>() where TSaga : class
    {
        // No-op: endpoint-level saga registration through the definition's endpoint argument is ignored.
    }

    /// <inheritdoc />
    public void UseSerializer<TSerializer>() where TSerializer : class, IMessageSerializer
    {
        // No-op: endpoint-level serializer override through the definition's endpoint argument is ignored.
    }

    /// <inheritdoc />
    public void UseDeserializer<TDeserializer>() where TDeserializer : class, IMessageDeserializer
    {
        // No-op: endpoint-level deserializer override through the definition's endpoint argument is ignored.
    }

    /// <inheritdoc />
    public void OrderedBy<TMessage>(Func<TMessage, object?> selector) where TMessage : class
    {
        // No-op: endpoint-level ordering configuration through the definition's endpoint argument is ignored.
    }

    /// <inheritdoc />
    public void OrderedByHeader(string headerName)
    {
        // No-op: endpoint-level ordering configuration through the definition's endpoint argument is ignored.
    }

    /// <inheritdoc />
    public void OrderedBy(Action<IConsumerOrderingConfigurator> configure)
    {
        // No-op: endpoint-level ordering configuration through the definition's endpoint argument is ignored.
    }
}
