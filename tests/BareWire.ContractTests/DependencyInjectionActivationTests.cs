using System.Reflection;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using BareWire.Abstractions.Saga;
using BareWire.AWS.SQS;
using BareWire.AzureServiceBus;
using BareWire.CloudEvents;
using BareWire.Google.PubSub;
using BareWire.InMemory;
using BareWire.Interop.MassTransit;
using BareWire.Kafka;
using BareWire.Observability;
using BareWire.Outbox.EntityFramework;
using BareWire.RabbitMQ;
using BareWire.Saga;
using BareWire.Saga.EntityFramework;
using BareWire.Saga.Redis;
using BareWire.Serialization.Json;
using BareWire.Serialization.MsgPack;
using BareWire.Transport.AWS.SQS;
using BareWire.Transport.AWS.SQS.Configuration;
using BareWire.Transport.AzureServiceBus;
using BareWire.Transport.Google.PubSub;
using BareWire.Transport.Google.PubSub.Configuration;
using BareWire.Transport.InMemory;
using BareWire.Transport.Kafka;
using BareWire.Transport.Kafka.Configuration;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BareWire.ContractTests;

/// <summary>
/// Guards against DI registrations the container cannot activate.
/// <para>
/// Implementation classes are <c>internal</c> by convention, but <c>Microsoft.Extensions.DependencyInjection</c>
/// (and <c>ActivatorUtilities</c>) only consider <em>public</em> constructors. A type registered as
/// <c>services.AddScoped&lt;IService, Impl&gt;()</c> whose constructor is <c>internal</c> compiles fine and
/// only fails when the service is resolved (or at <c>ValidateOnBuild</c>) with
/// "A suitable constructor for type ... could not be located" — that is how the Redis saga repository
/// shipped unusable (GH #85). Types created through a factory lambda are not affected.
/// </para>
/// <para>
/// The scan runs every public <see cref="IServiceCollection"/> extension method shipped by BareWire and inspects
/// the resulting descriptors, so it needs no broker, database or Redis connection.
/// <see cref="EveryPublicServiceCollectionExtension_IsCoveredByTheActivationScan"/> fails when a new extension
/// method is added without a matching entry in <see cref="Registrations"/>.
/// </para>
/// </summary>
public sealed class DependencyInjectionActivationTests
{
    private const string AmqpUri = "amqp://guest:guest@localhost:5672/";

    private const string ServiceBusConnectionString =
        "Endpoint=sb://localhost/;SharedAccessKeyName=k;SharedAccessKey=dGVzdA==";

    /// <summary>
    /// One entry per public <see cref="IServiceCollection"/> extension method, invoked with a minimal valid
    /// configuration. The key is <c>{assembly}:{method}</c>.
    /// Decorating registrations (CloudEvents, MassTransit envelope, MessagePack routing) require the JSON
    /// serializer to be registered first, as they do in an application.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Action<IServiceCollection>> Registrations =
        new Dictionary<string, Action<IServiceCollection>>
        {
            ["BareWire:AddBareWire"] = s => s.AddBareWire(_ => { }),
            ["BareWire.Transport.InMemory:AddBareWireInMemory"] = s => s.AddBareWireInMemory(_ => { }),
            ["BareWire.InMemory:AddBareWireWithInMemory"] =
                s => s.AddBareWireWithInMemory(t => t.ConfigureTopology(x => x.DeclareQueue("q"))),
            ["BareWire.Transport.RabbitMQ:AddBareWireRabbitMq"] = s => s.AddBareWireRabbitMq(r => r.Host(AmqpUri)),
            ["BareWire.RabbitMQ:AddBareWireWithRabbitMq"] = s => s.AddBareWireWithRabbitMq(r => r.Host(AmqpUri)),
            ["BareWire.Transport.Kafka:AddBareWireKafka"] = s => s.AddBareWireKafka(ConfigureKafka),
            ["BareWire.Kafka:AddBareWireWithKafka"] = s => s.AddBareWireWithKafka(ConfigureKafka),
            ["BareWire.Transport.AzureServiceBus:AddBareWireAzureServiceBus"] =
                s => s.AddBareWireAzureServiceBus(a => a.ConnectionString(ServiceBusConnectionString)),
            ["BareWire.AzureServiceBus:AddBareWireWithAzureServiceBus"] =
                s => s.AddBareWireWithAzureServiceBus(a => a.ConnectionString(ServiceBusConnectionString)),
            ["BareWire.Transport.AWS.SQS:AddBareWireSqs"] = s => s.AddBareWireSqs(ConfigureSqs),
            ["BareWire.AWS.SQS:AddBareWireWithSqs"] = s => s.AddBareWireWithSqs(ConfigureSqs),
            ["BareWire.Transport.Google.PubSub:AddBareWirePubSub"] = s => s.AddBareWirePubSub(ConfigurePubSub),
            ["BareWire.Google.PubSub:AddBareWireWithPubSub"] = s => s.AddBareWireWithPubSub(ConfigurePubSub),
            ["BareWire.Serialization.Json:AddBareWireJsonSerializer"] = s => s.AddBareWireJsonSerializer(),
            ["BareWire.Serialization.MsgPack:AddBareWireMessagePackSerializer"] =
                s => s.AddBareWireMessagePackSerializer(),
            ["BareWire.Serialization.MsgPack:AddBareWireMessagePackDeserializerRouting"] =
                s => s.AddBareWireJsonSerializer().AddBareWireMessagePackDeserializerRouting(),
            ["BareWire.CloudEvents:AddCloudEvents"] = s => s.AddBareWireJsonSerializer().AddCloudEvents(),
            ["BareWire.CloudEvents:AddCloudEventsEnvelope"] = s => s.AddBareWireJsonSerializer().AddCloudEventsEnvelope(),
            ["BareWire.Interop.MassTransit:AddMassTransitEnvelopeSerializer"] =
                s => s.AddBareWireJsonSerializer().AddMassTransitEnvelopeSerializer(),
            ["BareWire.Interop.MassTransit:AddMassTransitEnvelopeDeserializer"] =
                s => s.AddBareWireJsonSerializer().AddMassTransitEnvelopeDeserializer(),
            ["BareWire.Observability:AddBareWireObservability"] =
                s => s.AddBareWireObservability(o => o.EnableOpenTelemetry = false),
            ["BareWire.Outbox.EntityFramework:AddBareWireOutbox"] = s => s.AddBareWireOutbox(_ => { }),
            ["BareWire.Saga:AddBareWireSagaStateMachine"] =
                s => s.AddBareWireSagaStateMachine<ActivationStateMachine, ActivationSagaState>(),
            ["BareWire.Saga:AddBareWireSagaInMemory"] = s => s.AddBareWireSagaInMemory<ActivationSagaState>(),
            ["BareWire.Saga.EntityFramework:AddBareWireSaga"] = s => s.AddBareWireSaga<ActivationSagaState>(_ => { }),
            ["BareWire.Saga.Redis:AddBareWireSagaRedis"] = s => s.AddBareWireSagaRedis<ActivationSagaState>(_ => { }),
            ["BareWire.Saga.Redis:AddBareWireRedisConnection"] = s => s.AddBareWireRedisConnection(o =>
            {
                o.Endpoints.Add("localhost:6379");
                o.Ssl = true;
            }),
        };

    public static TheoryData<string> RegistrationKeys => [.. Registrations.Keys.Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(RegistrationKeys))]
    public void TypeRegistrations_ForBareWireImplementations_HaveAPublicConstructor(string registration)
    {
        var services = new ServiceCollection();
        Registrations[registration](services);

        var offenders = services
            .Select(d => d.IsKeyedService ? d.KeyedImplementationType : d.ImplementationType)
            .OfType<Type>()
            .Where(IsBareWireType)
            .Where(t => t.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length == 0)
            .Select(t => t.FullName ?? t.Name)
            .Distinct()
            .ToList();

        offenders.Should().BeEmpty(
            $"'{registration}' registers these types by type, but DI only activates public constructors — " +
            "make the constructor public (the class may stay internal) or register a factory lambda");
    }

    [Fact]
    public void EveryPublicServiceCollectionExtension_IsCoveredByTheActivationScan()
    {
        var extensions = LoadBareWireAssemblies()
            .SelectMany(a => a.GetExportedTypes())
            .Where(t => t is { IsAbstract: true, IsSealed: true }) // static classes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.IsDefined(typeof(ExtensionAttribute), inherit: false))
            .Where(m => m.GetParameters() is [{ ParameterType: var p }, ..] && p == typeof(IServiceCollection))
            // Obsolete methods are on their way out; new registrations must not be added there.
            .Where(m => !m.IsDefined(typeof(ObsoleteAttribute), inherit: false))
            .Select(m => $"{m.DeclaringType!.Assembly.GetName().Name}:{m.Name}")
            .Distinct()
            .ToList();

        extensions.Should().NotBeEmpty();
        extensions.Where(e => !Registrations.ContainsKey(e)).Should().BeEmpty(
            "every public IServiceCollection extension must be added to DependencyInjectionActivationTests" +
            ".Registrations so its type registrations are checked for activatable constructors");
    }

    private static bool IsBareWireType(Type type) =>
        type.Assembly.GetName().Name is { } name
        && (name == "BareWire" || name.StartsWith("BareWire.", StringComparison.Ordinal))
        && type.Assembly != typeof(DependencyInjectionActivationTests).Assembly;

    private static IEnumerable<Assembly> LoadBareWireAssemblies()
    {
        string self = typeof(DependencyInjectionActivationTests).Assembly.GetName().Name!;

        return Directory.EnumerateFiles(AppContext.BaseDirectory, "BareWire*.dll")
            .Select(path => AssemblyName.GetAssemblyName(path))
            .Where(name => name.Name != self)
            .Select(Assembly.Load);
    }

    private static void ConfigureKafka(IKafkaConfigurator kafka)
    {
        kafka.BootstrapServers("localhost:9092");
        kafka.ConsumerGroup("test-group");
    }

    private static void ConfigureSqs(ISqsConfigurator sqs)
    {
        sqs.Region("eu-central-1");
        sqs.UseExplicitCredentials("test", "test");
    }

    private static void ConfigurePubSub(IPubSubConfigurator pubSub)
    {
        pubSub.ProjectId("test-project");
        pubSub.UseEmulator("localhost:8085");
    }

    public sealed class ActivationSagaState : ISagaState
    {
        public Guid CorrelationId { get; set; }
        public string CurrentState { get; set; } = "Initial";
        public int Version { get; set; }
    }

    public sealed record ActivationStarted(Guid Id);

    public sealed class ActivationStateMachine : BareWireStateMachine<ActivationSagaState>
    {
        public ActivationStateMachine()
        {
            var started = Event<ActivationStarted>();
            CorrelateBy<ActivationStarted>(e => e.Id);
            Initially(() => When(started, b => b.TransitionTo("Started")));
        }
    }
}
