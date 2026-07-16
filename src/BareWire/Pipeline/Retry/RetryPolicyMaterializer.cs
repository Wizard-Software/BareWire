using BareWire.Abstractions.Configuration;

namespace BareWire.Pipeline.Retry;

/// <summary>
/// Materializes a per-consumer retry configuration delegate into a core <see cref="RetryPolicy"/>.
/// </summary>
/// <remarks>
/// Bridges the public, dependency-free <see cref="IRetryConfigurator"/> contract (carried on
/// <c>ConsumerRegistration.ConfigureRetry</c>) to the core <see cref="RetryPolicy"/> type. Building the
/// policy (<c>Build()</c>) stays in the <c>BareWire</c> core so that <c>BareWire.Abstractions</c> remains
/// dependency-free (the <see cref="RetryPolicy"/> type never appears there). Materialization runs once at
/// startup, never on the per-message hot path.
/// </remarks>
internal static class RetryPolicyMaterializer
{
    /// <summary>
    /// Materializes the supplied retry configuration delegate into a <see cref="RetryPolicy"/>.
    /// </summary>
    /// <param name="configure">
    /// The retry configuration delegate (e.g. <c>ConsumerRegistration.ConfigureRetry</c>), or
    /// <see langword="null"/> when no retry policy is configured (default-off).
    /// </param>
    /// <param name="timeProvider">
    /// Optional time provider forwarded to the built policy; defaults to the system clock when omitted.
    /// </param>
    /// <returns>
    /// The built <see cref="RetryPolicy"/>, or <see langword="null"/> when <paramref name="configure"/>
    /// is <see langword="null"/>.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Propagated from <c>Build()</c> when the delegate ran but selected no retry strategy
    /// (fail-fast at startup).
    /// </exception>
    internal static RetryPolicy? Materialize(
        Action<IRetryConfigurator>? configure,
        TimeProvider? timeProvider = null)
    {
        if (configure is null)
        {
            return null;
        }

        RetryConfigurator configurator = new(timeProvider);
        configure(configurator);
        return configurator.Build();
    }
}
