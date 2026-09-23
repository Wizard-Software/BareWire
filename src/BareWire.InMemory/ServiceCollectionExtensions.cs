using System.Globalization;

using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.InMemory.Internal;
using BareWire.Transport.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BareWire.InMemory;

/// <summary>
/// Provides a single-call registration entry point that wires up both the BareWire core
/// engine and the in-memory transport in one statement.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers BareWire together with the in-memory transport in a single call. This is the
    /// recommended ergonomic entry point: it is equivalent to calling
    /// <see cref="BareWire.Transport.InMemory.ServiceCollectionExtensions.AddBareWireInMemory(IServiceCollection, Action{IInMemoryConfigurator})"/>
    /// (which registers the <c>ITransportAdapter</c>) followed by
    /// <see cref="BareWire.ServiceCollectionExtensions.AddBareWire(IServiceCollection, Action{IBusConfigurator})"/>
    /// (which registers the core engine), and it additionally registers the core's internal
    /// bus-shutdown drain timeout from the value passed to <see cref="IInMemoryConfigurator.DrainTimeout"/>
    /// (or a 10-second default when it is not configured).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Delivery guarantee.</strong> The in-memory transport is explicitly at-most-once — see
    /// <see cref="IInMemoryConfigurator"/> for the full delivery-guarantee and trust-boundary remarks.
    /// </para>
    /// <para>
    /// <strong>One call per service collection.</strong> Call this method at most once per
    /// <see cref="IServiceCollection"/>. It guards against a second registration of the core bus, of
    /// any transport adapter (including a prior <c>AddBareWireInMemory</c> call, or a call registering
    /// a different transport), or of the internal bus-shutdown options — each of those is rejected with
    /// a <see cref="BareWireConfigurationException"/> before this method registers anything. The guard
    /// only detects registrations made <em>before</em> this call; it cannot detect a plain
    /// <c>AddBareWire(...)</c> call made <em>after</em> this method.
    /// </para>
    /// <para>
    /// <strong>Drain timeout.</strong> The value passed to <see cref="IInMemoryConfigurator.DrainTimeout"/>
    /// is forwarded to the core's internal bus-shutdown coordination, which bounds how long
    /// <c>StopAsync</c> waits for in-flight work to settle before cancelling consumer loops. That value
    /// is in practice further bounded by the hosting process's own shutdown budget (for example
    /// <c>HostOptions.ShutdownTimeout</c> under the .NET generic host) — a drain timeout longer than the
    /// host's shutdown budget cannot be honored in full.
    /// </para>
    /// </remarks>
    /// <param name="services">The <see cref="IServiceCollection"/> to register services into.</param>
    /// <param name="transport">Configures the in-memory transport via <see cref="IInMemoryConfigurator"/> (topology, endpoints, options).</param>
    /// <param name="bus">Optional core bus configuration via <see cref="IBusConfigurator"/> (endpoints, middleware, serializers). When omitted, the core is registered with defaults.</param>
    /// <returns>The same <see cref="IServiceCollection"/> to allow chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> or <paramref name="transport"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="BareWireConfigurationException">
    /// Thrown when the core bus, a transport adapter, or the internal bus-shutdown options are already
    /// registered in <paramref name="services"/>, when the configured transport options fail validation,
    /// or when the configured <see cref="IInMemoryConfigurator.DrainTimeout"/> exceeds the drain timer's
    /// upper bound (approximately 49.7 days). The last case is detected after the transport has been
    /// registered, so the collection must be discarded; startup fails either way.
    /// </exception>
    public static IServiceCollection AddBareWireWithInMemory(
        this IServiceCollection services,
        Action<IInMemoryConfigurator> transport,
        Action<IBusConfigurator>? bus = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(transport);

        GuardAgainstPriorRegistration(services);

        var capture = new DrainTimeoutCapturingConfigurator.Capture();
        services.AddBareWireInMemory(inner => transport(new DrainTimeoutCapturingConfigurator(inner, capture)));

        BusShutdownOptions shutdown = CreateShutdownOptions(capture.DrainTimeout);
        services.TryAddSingleton(shutdown);

        services.AddBareWire(bus ?? (_ => { }));
        return services;
    }

    // Rejects a second registration BEFORE anything is registered, so a call rejected by this guard
    // never leaves a half-registered container behind (a DrainTimeout above the timer bound is only
    // detected after the transport is registered). Only detects registrations made earlier in the same
    // IServiceCollection — a plain AddBareWire(...) call made AFTER this method cannot be seen here.
    private static void GuardAgainstPriorRegistration(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(BareWireBusControl)))
        {
            throw new BareWireConfigurationException(
                optionName: "AddBareWire",
                optionValue: "already registered",
                expectedValue: "a single AddBareWire per service collection. AddBareWireWithInMemory " +
                               "registers the core bus itself, so it must not be combined with a separate " +
                               "AddBareWire(...) call. Use either a single AddBareWireWithInMemory(...) " +
                               "call, or the two-call form AddBareWireInMemory(...) followed by exactly one " +
                               "AddBareWire(...).");
        }

        if (services.Any(d => d.ServiceType == typeof(ITransportAdapter)))
        {
            throw new BareWireConfigurationException(
                optionName: "AddBareWireWithInMemory",
                optionValue: "a transport adapter is already registered",
                expectedValue: "no transport adapter to be registered before AddBareWireWithInMemory — " +
                               "including a prior AddBareWireInMemory(...) call, or a call registering a " +
                               "different transport. Use either a single AddBareWireWithInMemory(...) call " +
                               "as the only transport registration, or the two-call form " +
                               "AddBareWireInMemory(...) followed by exactly one AddBareWire(...) without " +
                               "also calling AddBareWireWithInMemory.");
        }

        if (services.Any(d => d.ServiceType == typeof(BusShutdownOptions)))
        {
            throw new BareWireConfigurationException(
                optionName: "AddBareWireWithInMemory",
                optionValue: "BusShutdownOptions is already registered",
                expectedValue: "no bus-shutdown options to be registered before AddBareWireWithInMemory. " +
                               "Use either a single AddBareWireWithInMemory(...) call, which registers " +
                               "bus-shutdown options itself from IInMemoryConfigurator.DrainTimeout, or the " +
                               "two-call form AddBareWireInMemory(...) followed by exactly one " +
                               "AddBareWire(...) without registering bus-shutdown options yourself.");
        }
    }

    private static BusShutdownOptions CreateShutdownOptions(TimeSpan? drainTimeout)
    {
        if (drainTimeout is not { } value)
        {
            return new BusShutdownOptions();
        }

        try
        {
            return new BusShutdownOptions { DrainTimeout = value };
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new BareWireConfigurationException(
                optionName: "DrainTimeout",
                optionValue: value.ToString("c", CultureInfo.InvariantCulture),
                expectedValue: "a value no greater than 49.7 days (the drain timer's upper bound).",
                innerException: ex);
        }
    }
}
