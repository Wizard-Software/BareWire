using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Bus;
using BareWire.Pipeline.Retry;
using Microsoft.Extensions.DependencyInjection;
using CoreCfg = BareWire.Configuration;
using RabbitCfg = BareWire.Transport.RabbitMQ.Configuration;

namespace BareWire.UnitTests.Core.Configuration;

/// <summary>
/// High-risk enforcement suite (ADR-036 §Enforcement) for ConsumerDefinition: I-1 retry carrier
/// (maps to core RetryPolicy + no RetryPolicy leak into zero-dep Abstractions + not an untyped handle),
/// discovery bit-for-bit core↔transport parity + DI-only (no assembly scan), single-vs-multi message-type
/// inference fail-fast, and default-off bit-identity (reference identity preserved, zero allocation).
/// These tie the four invariants together end-to-end; each fact regresses to RED if its guarantee breaks.
/// </summary>
public sealed class ConsumerDefinitionHighRiskBehaviorTests
{
    private sealed record OrderPlaced(string Id);
    private sealed record OrderShipped(string Id);

    private sealed class OrderConsumer : IConsumer<OrderPlaced>
    {
        public Task ConsumeAsync(ConsumeContext<OrderPlaced> context) => Task.CompletedTask;
    }

    // Implements two IConsumer<T> — target of the multi-consumer inference fail-fast test.
    private sealed class MultiConsumer : IConsumer<OrderPlaced>, IConsumer<OrderShipped>
    {
        public Task ConsumeAsync(ConsumeContext<OrderPlaced> context) => Task.CompletedTask;
        public Task ConsumeAsync(ConsumeContext<OrderShipped> context) => Task.CompletedTask;
    }

    // DI-registered definition — target of the discovery test.
    private sealed class OrderDefinition : ConsumerDefinition<OrderConsumer>
    {
        protected override void Configure(
            IReceiveEndpointConfigurator endpoint,
            IConsumerConfigurator<OrderConsumer> consumer)
        {
            consumer.RoutingKey("orders.*");
            consumer.AcceptUntyped();
        }
    }

    private static ConsumerRegistration Reg() => new(typeof(OrderConsumer), typeof(OrderPlaced));

    // --- Guarantee 1: I-1 retry carrier ---

    [Fact]
    public void RetryCarrier_ComposedViaPublicContract_MapsToCorrectCoreRetryPolicy()
    {
        Action<IRetryConfigurator> configure = c => c.Interval(3, TimeSpan.FromMilliseconds(1));
        ConsumerRegistration registration = Reg() with { ConfigureRetry = configure };

        RetryPolicy? policy = RetryPolicyMaterializer.Materialize(registration.ConfigureRetry);

        policy.Should().BeOfType<IntervalRetryPolicy>();
        policy!.ShouldRetry(new InvalidOperationException(), attempt: 0).Should().BeTrue();
        policy.ShouldRetry(new InvalidOperationException(), attempt: 2).Should().BeTrue();
        policy.ShouldRetry(new InvalidOperationException(), attempt: 3).Should().BeFalse(); // MaxRetries = 3
    }

    [Fact]
    public void FacadeRetry_ComposedInDefinition_FlowsIntoConsumerRegistration()
    {
        // The ergonomic path a ConsumerDefinition uses: consumer.Retry(r => ...) on the public façade.
        var configurator = new CoreCfg.ConsumerDefinitionConfigurator<OrderConsumer>();
        configurator.Retry(c => c.Interval(3, TimeSpan.FromMilliseconds(1)));

        ConsumerRegistration merged = configurator.Merge(Reg());

        merged.ConfigureRetry.Should().NotBeNull();
        RetryPolicy? policy = RetryPolicyMaterializer.Materialize(merged.ConfigureRetry);
        policy.Should().BeOfType<IntervalRetryPolicy>();
        policy!.ShouldRetry(new InvalidOperationException(), attempt: 2).Should().BeTrue();
        policy.ShouldRetry(new InvalidOperationException(), attempt: 3).Should().BeFalse();
    }

    [Fact]
    public void RetryCarrier_OnAbstractionsRecord_DoesNotReferenceCoreRetryPolicy()
    {
        System.Reflection.Assembly abstractionsAssembly = typeof(ConsumerRegistration).Assembly;
        Type carrierArg = typeof(ConsumerRegistration)
            .GetProperty(nameof(ConsumerRegistration.ConfigureRetry))!
            .PropertyType.GetGenericArguments()[0]; // the T of Action<T>

        // Carrier is typed on the public Abstractions contract, not the core RetryPolicy type.
        carrierArg.Should().Be<IRetryConfigurator>();
        carrierArg.Assembly.Should().BeSameAs(abstractionsAssembly);

        // The core RetryPolicy type is absent from the Abstractions assembly — zero-dep invariant held.
        abstractionsAssembly.GetType("BareWire.Pipeline.Retry.RetryPolicy").Should().BeNull();
        abstractionsAssembly.GetReferencedAssemblies()
            .Should().NotContain(a => a.Name == "BareWire");
    }

    [Fact]
    public void RetryCarrier_IsTypedActionNotUntypedHandle()
    {
        Type carrierType = typeof(ConsumerRegistration)
            .GetProperty(nameof(ConsumerRegistration.ConfigureRetry))!.PropertyType;

        carrierType.Should().Be<Action<IRetryConfigurator>>();
        carrierType.Should().NotBe<object>();
        carrierType.Should().NotBe<Delegate>();
        carrierType.Should().NotBe<string>();
    }

    // --- Guarantee 2: discovery bit-for-bit (core AND transport) + DI-only ---

    [Fact]
    public void DiscoveredSettings_MaterializeBitForBit_CoreAndTransport()
    {
        Action<IRetryConfigurator> retry = c => c.Interval(2, TimeSpan.FromMilliseconds(1));

        var core = new CoreCfg.ConsumerConfigurator<OrderConsumer, OrderPlaced>();
        core.RoutingKey("orders.created");
        core.RoutingKey("orders.updated");
        core.AcceptUntyped();
        core.UseMassTransitEnvelope();
        core.Retry(retry);
        core.PrefetchCount(16);
        core.ConcurrentMessageLimit(4);
        ConsumerRegistration coreReg = core.Build();

        var transport = new RabbitCfg.ConsumerConfigurator<OrderConsumer, OrderPlaced>();
        transport.RoutingKey("orders.created");
        transport.RoutingKey("orders.updated");
        transport.AcceptUntyped();
        transport.UseMassTransitEnvelope();
        transport.Retry(retry);
        transport.PrefetchCount(16);
        transport.ConcurrentMessageLimit(4);
        ConsumerRegistration transportReg = transport.Build();

        // Field-by-field: record equality would compare RoutingKeys by reference, so assert explicitly.
        transportReg.ConsumerType.Should().Be(coreReg.ConsumerType);
        transportReg.MessageType.Should().Be(coreReg.MessageType);
        transportReg.RoutingKeys.Should().Equal(coreReg.RoutingKeys);
        transportReg.AcceptUntyped.Should().Be(coreReg.AcceptUntyped);
        transportReg.UseMassTransitEnvelope.Should().Be(coreReg.UseMassTransitEnvelope);
        transportReg.ConfigureRetry.Should().BeSameAs(coreReg.ConfigureRetry);
        transportReg.PrefetchCount.Should().Be(coreReg.PrefetchCount);
        transportReg.ConcurrentMessageLimit.Should().Be(coreReg.ConcurrentMessageLimit);
    }

    [Fact]
    public void Discovery_AppliesRegisteredDefinition_ButNotUnregistered()
    {
        // Registered → applied.
        ServiceProvider withDef = new ServiceCollection()
            .AddSingleton<ConsumerDefinition<OrderConsumer>, OrderDefinition>()
            .BuildServiceProvider();
        IReadOnlyList<ConsumerRegistration> applied =
            ConsumerDefinitionDiscovery.ApplyRegisteredDefinitions([Reg()], withDef);
        applied[0].RoutingKeys.Should().ContainSingle().Which.Should().Be("orders.*");
        applied[0].AcceptUntyped.Should().BeTrue();

        // OrderDefinition exists in the assembly but is NOT registered → not applied (no assembly scan).
        ServiceProvider empty = new ServiceCollection().BuildServiceProvider();
        IReadOnlyList<ConsumerRegistration> input = [Reg()];
        IReadOnlyList<ConsumerRegistration> notApplied =
            ConsumerDefinitionDiscovery.ApplyRegisteredDefinitions(input, empty);
        notApplied.Should().BeSameAs(input); // reference identity — nothing matched.
    }

    // --- Guarantee 3: single-vs-multi inference ---

    [Fact]
    public void Inference_SingleIConsumer_InfersMessageType()
    {
        ConsumerMessageTypeInference.ResolveSingleMessageType(typeof(OrderConsumer))
            .Should().Be<OrderPlaced>();
    }

    [Fact]
    public void Inference_MultipleIConsumer_FailsFastRequestingExplicitOverload()
    {
        Action act = () => ConsumerMessageTypeInference.ResolveSingleMessageType(typeof(MultiConsumer));

        act.Should().Throw<BareWireConfigurationException>()
            .Which.Message.Should().Contain("overload"); // directs to Consumer<TConsumer, TMessage>()
    }

    // --- Guarantee 4: default-off bit-identity ---

    [Fact]
    public void NoDefinition_BehavesBitIdentically()
    {
        ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        ConsumerRegistration original = Reg();
        IReadOnlyList<ConsumerRegistration> input = [original];

        IReadOnlyList<ConsumerRegistration> result =
            ConsumerDefinitionDiscovery.ApplyRegisteredDefinitions(input, services);

        result.Should().BeSameAs(input);        // no new list allocated
        result[0].Should().BeSameAs(original);  // same registration instance
        result[0].RoutingKeys.Should().BeNull();
        result[0].AcceptUntyped.Should().BeFalse();
        result[0].UseMassTransitEnvelope.Should().BeFalse();
        result[0].ConfigureRetry.Should().BeNull();
        result[0].PrefetchCount.Should().BeNull();
        result[0].ConcurrentMessageLimit.Should().BeNull();
    }
}
