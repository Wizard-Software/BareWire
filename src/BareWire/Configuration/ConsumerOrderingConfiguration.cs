using BareWire.Abstractions.Configuration;

namespace BareWire.Configuration;

/// <summary>
/// Stores per-key consumer-ordering configuration collected through
/// <see cref="IConsumerOrderingConfigurator"/> for a single receive endpoint. This is a configuration-time
/// data carrier only — it performs no runtime dispatch, validation, or key resolution (those arrive in
/// later tasks of the per-key ordering feature). The collected settings are exposed as internal properties
/// for the dispatch engine to consume.
/// </summary>
/// <remarks>
/// Implements the read-only <see cref="IConsumerOrderingConfiguration"/> (via explicit interface
/// implementation, because the read-side property names collide with the write-side fluent method names
/// such as <see cref="Concurrency(int)"/>) so the transport-agnostic dispatch engine can read the settings
/// from <see cref="EndpointBinding.Ordering"/> without downcasting to this concrete type.
/// </remarks>
internal sealed class ConsumerOrderingConfiguration : IConsumerOrderingConfigurator, IConsumerOrderingConfiguration
{
    /// <summary>Gets the header name to read the ordering key from, when configured via header.</summary>
    internal string? HeaderName { get; private set; }

    /// <summary>Gets the typed selector that projects a message to its ordering key, when configured.</summary>
    internal Delegate? Selector { get; private set; }

    /// <summary>Gets the message type the <see cref="Selector"/> reads, when a selector is configured.</summary>
    internal Type? SelectorMessageType { get; private set; }

    /// <summary>
    /// Gets the typed-selector adapter built at configuration time (R8.13, D2): a closure
    /// <c>o =&gt; selector((TMessage)o)</c> that lets <see cref="Bus.OrderingKeyResolver.ResolveTyped"/>
    /// invoke the user selector over a deserialized message WITHOUT <c>DynamicInvoke</c>, an
    /// <c>object[]</c> argument array, or reflection. Internal-only — never exposed on the public
    /// <see cref="IConsumerOrderingConfiguration"/> interface, so the public API surface is unchanged
    /// (no <c>approved.txt</c> regeneration). <see langword="null"/> when no selector is configured.
    /// </summary>
    internal Func<object, object?>? SelectorAdapter { get; private set; }

    /// <summary>Gets a value indicating whether the correlation-id is used as the ordering key.</summary>
    internal bool UseCorrelationId { get; private set; }

    /// <summary>Gets the configured cross-key concurrency (lane count), when set.</summary>
    internal int? Concurrency_ { get; private set; }

    /// <summary>Gets the configured ordering strategy. Defaults to <see cref="ConsumerOrderingStrategy.Auto"/>.</summary>
    internal ConsumerOrderingStrategy Strategy_ { get; private set; } = ConsumerOrderingStrategy.Auto;

    /// <summary>Gets the declared transport-native affinity. Defaults to <see cref="TransportAffinity.None"/>.</summary>
    internal Abstractions.Configuration.TransportAffinity TransportAffinity_ { get; private set; }
        = Abstractions.Configuration.TransportAffinity.None;

    /// <summary>Gets the configured per-key maximum delivery attempts. Defaults to <c>0</c> (disabled).</summary>
    internal int MaxDeliveryAttempts_ { get; private set; }

    /// <inheritdoc />
    public IConsumerOrderingConfigurator ByHeader(string headerName)
    {
        ArgumentException.ThrowIfNullOrEmpty(headerName);
        HeaderName = headerName;
        return this;
    }

    /// <inheritdoc />
    public IConsumerOrderingConfigurator By<TMessage>(Func<TMessage, object?> selector) where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(selector);
        Selector = selector;
        SelectorMessageType = typeof(TMessage);
        // D2 (R8.13): build the typed adapter at configuration time so ResolveTyped invokes the
        // user selector with no DynamicInvoke / object[] / reflection on the post-deserialization path.
        // The (TMessage) cast is safe because ResolveTyped only invokes the adapter after confirming the
        // deserialized message is an instance of SelectorMessageType (heterogeneous streams fall through
        // to keyless without invoking the adapter).
        SelectorAdapter = o => selector((TMessage)o);
        return this;
    }

    /// <inheritdoc />
    public IConsumerOrderingConfigurator ByCorrelationId()
    {
        UseCorrelationId = true;
        return this;
    }

    /// <inheritdoc />
    public IConsumerOrderingConfigurator Concurrency(int degree)
    {
        Concurrency_ = degree;
        return this;
    }

    /// <inheritdoc />
    public IConsumerOrderingConfigurator Strategy(ConsumerOrderingStrategy strategy)
    {
        Strategy_ = strategy;
        return this;
    }

    /// <inheritdoc />
    public IConsumerOrderingConfigurator TransportAffinity(TransportAffinity affinity)
    {
        TransportAffinity_ = affinity;
        return this;
    }

    /// <inheritdoc />
    public IConsumerOrderingConfigurator MaxDeliveryAttempts(int attempts)
    {
        MaxDeliveryAttempts_ = attempts;
        return this;
    }

    // ── IConsumerOrderingConfiguration (read-only view; explicit to avoid name clashes) ──────────

    string? IConsumerOrderingConfiguration.HeaderName => HeaderName;

    Delegate? IConsumerOrderingConfiguration.Selector => Selector;

    Type? IConsumerOrderingConfiguration.SelectorMessageType => SelectorMessageType;

    bool IConsumerOrderingConfiguration.UseCorrelationId => UseCorrelationId;

    int? IConsumerOrderingConfiguration.Concurrency => Concurrency_;

    ConsumerOrderingStrategy IConsumerOrderingConfiguration.Strategy => Strategy_;

    TransportAffinity IConsumerOrderingConfiguration.TransportAffinity => TransportAffinity_;

    int IConsumerOrderingConfiguration.MaxDeliveryAttempts => MaxDeliveryAttempts_;
}
