using BareWire.Abstractions.Saga;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BareWire.Saga;

/// <summary>
/// Provides extension methods on <see cref="IServiceCollection"/> for registering BareWire
/// saga state machines with the dependency injection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a saga state machine and wires its message dispatcher into the BareWire
    /// consume pipeline.
    /// </summary>
    /// <remarks>
    /// This method does not register an <see cref="ISagaRepository{TSaga}"/>. Register one for
    /// <typeparamref name="TSaga"/> separately: <see cref="AddBareWireSagaInMemory{TSaga}"/> in this
    /// package (tests and development), <c>AddBareWireSaga&lt;TSaga&gt;()</c> from
    /// <c>BareWire.Saga.EntityFramework</c>, or <c>AddBareWireSagaRedis&lt;TSaga&gt;()</c> from
    /// <c>BareWire.Saga.Redis</c>.
    /// </remarks>
    /// <typeparam name="TStateMachine">
    /// The state machine type — the class derived from
    /// <see cref="BareWireStateMachine{TSaga}"/>.
    /// </typeparam>
    /// <typeparam name="TSaga">
    /// The saga state type managed by <typeparamref name="TStateMachine"/>.
    /// Must implement <see cref="ISagaState"/> and have a public parameterless constructor.
    /// </typeparam>
    /// <param name="services">The service collection to register services into.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> is <see langword="null"/>.
    /// </exception>
    public static IServiceCollection AddBareWireSagaStateMachine<TStateMachine, TSaga>(
        this IServiceCollection services)
        where TStateMachine : BareWireStateMachine<TSaga>
        where TSaga : class, ISagaState, new()
    {
        ArgumentNullException.ThrowIfNull(services);

        // Register the state machine itself as a singleton if not already registered.
        services.TryAddSingleton<TStateMachine>();

        // Register the StateMachineDefinition built from the singleton state machine.
        services.TryAddSingleton(sp =>
        {
            TStateMachine machine = sp.GetRequiredService<TStateMachine>();
            return StateMachineDefinition<TSaga>.Build(machine);
        });

        // NOTE: StateMachineExecutor<TSaga> is NOT registered as singleton because it depends
        // on ISagaRepository<TSaga> which is scoped (EF Core DbContext). Instead, the dispatcher
        // creates executors per-message using IServiceScopeFactory.

        // Register the ISagaMessageDispatcher implementation for this state machine.
        // Multiple saga machines register multiple ISagaMessageDispatcher entries.
        // The dispatcher uses IServiceScopeFactory to resolve ISagaRepository per-message,
        // avoiding the captive dependency problem (scoped repo in singleton executor).
        services.AddSingleton<ISagaMessageDispatcher>(sp => new SagaMessageDispatcher<TStateMachine, TSaga>(
            sp.GetRequiredService<StateMachineDefinition<TSaga>>(),
            sp.GetRequiredService<TStateMachine>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILoggerFactory>()));

        return services;
    }

    /// <summary>
    /// Registers an in-process, in-memory <see cref="ISagaRepository{TSaga}"/> for
    /// <typeparamref name="TSaga"/>.
    /// </summary>
    /// <typeparam name="TSaga">
    /// The saga state type to store. Must implement <see cref="ISagaState"/> and have a public
    /// parameterless constructor.
    /// </typeparam>
    /// <param name="services">The service collection to register services into.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Intended for tests, samples and local development. Saga state lives only in the memory of
    /// the current process: it is lost when the process stops and is not shared between instances.
    /// Use <c>BareWire.Saga.EntityFramework</c> or <c>BareWire.Saga.Redis</c> for durable state.
    /// </para>
    /// <para>
    /// The repository is registered as a singleton so that state survives across the per-message
    /// scopes the saga dispatcher creates. Saved and loaded instances are shallow copies of the
    /// public settable properties, so keep saga state to value types and immutable reference types
    /// (a mutable collection property would be shared with the stored copy).
    /// <see cref="ISagaState.Version"/> is checked on update for optimistic concurrency. Registration uses <c>TryAdd</c> semantics: an <see cref="ISagaRepository{TSaga}"/>
    /// already registered for <typeparamref name="TSaga"/> is kept.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddBareWireSagaInMemory<TSaga>(this IServiceCollection services)
        where TSaga : class, ISagaState, new()
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ISagaRepository<TSaga>, InMemorySagaRepository<TSaga>>();

        return services;
    }
}
