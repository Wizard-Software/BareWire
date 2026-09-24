using System.Globalization;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Transport.InMemory.Configuration;

namespace BareWire.Transport.InMemory;

internal sealed class InMemoryTransportOptions
{
    internal const int DefaultQueueCapacity = 1_000;
    internal const int DefaultMaxMessageSize = 16 * 1024 * 1024;
    internal const int DefaultMaxRedeliveries = 20;
    internal const int DefaultRouteCacheCapacity = 10_000;
    internal static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan DefaultDeferDelay = TimeSpan.FromSeconds(30);

    // The largest due time an ITimer accepts (0xFFFFFFFE ms, about 49.7 days); a longer DeferDelay would
    // make the deferral timer throw after the pending redelivery was already created.
    internal static readonly TimeSpan MaxDeferDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    public int QueueCapacity { get; set; } = DefaultQueueCapacity;

    /// <summary>
    /// The maximum number of <c>(exchange, routingKey)</c> entries the in-memory router's route cache
    /// holds before it is cleared and rebuilt lazily. Bounds the cache's memory footprint numerically
    /// rather than by an eviction policy; see <c>InMemoryRouter</c> for the clearing behavior.
    /// </summary>
    public int RouteCacheCapacity { get; set; } = DefaultRouteCacheCapacity;

    public TimeSpan SendTimeout { get; set; } = DefaultSendTimeout;

    public int MaxMessageSize { get; set; } = DefaultMaxMessageSize;

    public int MaxRedeliveries { get; set; } = DefaultMaxRedeliveries;

    public TimeSpan DrainTimeout { get; set; } = DefaultDrainTimeout;

    public bool DeferEnabled { get; set; }

    public TimeSpan DeferDelay { get; set; } = DefaultDeferDelay;

    public string? DefaultExchange { get; set; }

    public bool GuaranteedRouting { get; set; }

    public bool AutoDeclareEndpointQueues { get; set; }

    /// <summary>
    /// The accumulated topology declaration produced by <c>IInMemoryConfigurator.ConfigureTopology</c>.
    /// <see langword="null"/> when no topology was configured.
    /// </summary>
    public TopologyDeclaration? Topology { get; set; }

    /// <summary>
    /// The accumulated receive endpoint configurations produced by
    /// <c>IInMemoryConfigurator.ReceiveEndpoint</c>.
    /// </summary>
    public IReadOnlyList<InMemoryEndpointConfiguration> EndpointConfigurations { get; set; } = [];

    /// <summary>
    /// Explicit type→routing-key mappings produced by <c>IInMemoryConfigurator.MapRoutingKey{T}</c>.
    /// </summary>
    public IReadOnlyDictionary<Type, string> RoutingKeyMappings { get; set; } = new Dictionary<Type, string>();

    /// <summary>
    /// Explicit type→exchange mappings produced by <c>IInMemoryConfigurator.MapExchange{T}</c>.
    /// </summary>
    public IReadOnlyDictionary<Type, string> ExchangeMappings { get; set; } = new Dictionary<Type, string>();

    /// <summary>
    /// Config-time-only diagnostic snapshot of divergent per-type publish-routing overwrites detected
    /// while the fluent configurator ran (the same message type receiving a different exchange or
    /// routing key from two registration paths). <see langword="null"/> when no divergence occurred.
    /// Never affects runtime resolution — last-call-wins applies regardless.
    /// </summary>
    public IReadOnlyList<PublishRoutingDivergence>? PublishRoutingDivergences { get; set; }

    internal void Validate()
    {
        if (QueueCapacity <= 0)
        {
            throw new BareWireConfigurationException(
                optionName: nameof(QueueCapacity),
                optionValue: QueueCapacity.ToString(CultureInfo.InvariantCulture),
                expectedValue: "a value greater than zero");
        }

        if (MaxMessageSize <= 0)
        {
            throw new BareWireConfigurationException(
                optionName: nameof(MaxMessageSize),
                optionValue: MaxMessageSize.ToString(CultureInfo.InvariantCulture),
                expectedValue: "a value greater than zero");
        }

        if (MaxRedeliveries <= 0)
        {
            throw new BareWireConfigurationException(
                optionName: nameof(MaxRedeliveries),
                optionValue: MaxRedeliveries.ToString(CultureInfo.InvariantCulture),
                expectedValue: "a value greater than zero");
        }

        if (DrainTimeout <= TimeSpan.Zero)
        {
            throw new BareWireConfigurationException(
                optionName: nameof(DrainTimeout),
                optionValue: DrainTimeout.ToString(),
                expectedValue: "a value greater than TimeSpan.Zero");
        }

        if (SendTimeout < TimeSpan.Zero)
        {
            throw new BareWireConfigurationException(
                optionName: nameof(SendTimeout),
                optionValue: SendTimeout.ToString(),
                expectedValue: "a value greater than or equal to TimeSpan.Zero (Zero = never wait)");
        }

        if (DeferEnabled && DeferDelay <= TimeSpan.Zero)
        {
            throw new BareWireConfigurationException(
                optionName: nameof(DeferDelay),
                optionValue: DeferDelay.ToString(),
                expectedValue: "a value greater than TimeSpan.Zero when EnableDefer is on");
        }

        if (DeferEnabled && DeferDelay > MaxDeferDelay)
        {
            throw new BareWireConfigurationException(
                optionName: nameof(DeferDelay),
                optionValue: DeferDelay.ToString(),
                expectedValue: $"a value no greater than {MaxDeferDelay} when EnableDefer is on");
        }

        if (DeferEnabled)
        {
            foreach (InMemoryEndpointConfiguration endpoint in EndpointConfigurations)
            {
                if (endpoint.Ordering is not null)
                {
                    throw new BareWireConfigurationException(
                        optionName: "EnableDefer",
                        optionValue: $"receive endpoint '{endpoint.QueueName}' declares ordering",
                        expectedValue: "Defer disabled, or no ordering declared on any receive endpoint " +
                            "(deferred redelivery breaks per-key order)");
                }
            }
        }

        if (RouteCacheCapacity <= 0)
        {
            throw new BareWireConfigurationException(
                optionName: nameof(RouteCacheCapacity),
                optionValue: RouteCacheCapacity.ToString(CultureInfo.InvariantCulture),
                expectedValue: "a value greater than zero");
        }
    }
}
