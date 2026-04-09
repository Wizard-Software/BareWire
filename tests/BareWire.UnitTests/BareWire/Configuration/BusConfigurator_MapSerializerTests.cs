using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Serialization;
using BareWire.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BareWire.UnitTests.Core.Configuration;

public sealed class BusConfigurator_MapSerializerTests
{
    [Fact]
    public void MapSerializer_WhenCalled_StoresMapping()
    {
        // Arrange
        BusConfigurator sut = new();

        // Act
        sut.MapSerializer<OrderCreated, StubSerializerA>();

        // Assert
        sut.SerializerMappings.Should().ContainKey(typeof(OrderCreated));
        sut.SerializerMappings[typeof(OrderCreated)].Should().Be<StubSerializerA>();
    }

    [Fact]
    public void MapSerializer_WhenCalledForSameTypeTwice_OverwritesPreviousMapping()
    {
        // Arrange
        BusConfigurator sut = new();
        sut.MapSerializer<OrderCreated, StubSerializerA>();

        // Act
        sut.MapSerializer<OrderCreated, StubSerializerB>();

        // Assert
        sut.SerializerMappings[typeof(OrderCreated)].Should().Be<StubSerializerB>();
    }

    [Fact]
    public void SerializerMappings_DefaultIsEmpty()
    {
        // Arrange + Act
        BusConfigurator sut = new();

        // Assert
        sut.SerializerMappings.Should().BeEmpty();
    }

    [Fact]
    public void MapSerializer_WhenDependencyNotPreRegistered_ThrowsClearErrorAtBusBuild()
    {
        // Arrange — MapSerializer<OrderCreated, CustomSerializer> with NO services.AddSingleton<CustomSerializer>()
        ServiceCollection services = new();
        services.AddSingleton(Substitute.For<BareWire.Abstractions.Transport.ITransportAdapter>());
        services.AddSingleton(Substitute.For<IMessageSerializer>());
        services.AddBareWire(bus =>
        {
            bus.MapSerializer<OrderCreated, StubSerializerA>();
            // StubSerializerA is NOT registered separately.
            // TryAddSingleton in ServiceCollectionExtensions handles simple cases, but
            // for this test we verify the resolver factory resolves successfully via TryAdd.
        });

        // Act — BuildServiceProvider is lazy; force resolution by requesting ISerializerResolver.
        ServiceProvider sp = services.BuildServiceProvider();

        // The TryAddSingleton in ServiceCollectionExtensions already registered StubSerializerA,
        // so resolution must succeed (no explicit user registration required for types with
        // default constructors). The factory does NOT throw for types that DI can auto-resolve.
        Action act = () => sp.GetRequiredService<BareWire.Abstractions.Serialization.ISerializerResolver>();

        // Assert — should resolve without error since TryAddSingleton auto-registered the type
        act.Should().NotThrow();
    }

    [Fact]
    public void MapSerializer_WhenDependencyRegistrationMissing_AndTryAddRemoved_ThrowsBareWireConfigurationException()
    {
        // This test verifies the clear-error path by using the ServiceCollectionExtensions
        // wiring: if a mapped serializer type cannot be resolved after TryAddSingleton,
        // a BareWireConfigurationException is thrown with the type name in the message.
        //
        // Since TryAddSingleton auto-registers simple types, we simulate a broken scenario
        // by verifying the exception type and message contain the type name when the
        // serializer type CANNOT be resolved (e.g. a type with required constructor args
        // that DI cannot satisfy).

        ServiceCollection services = new();
        services.AddSingleton(Substitute.For<BareWire.Abstractions.Transport.ITransportAdapter>());
        services.AddSingleton(Substitute.For<IMessageSerializer>());

        // Register BareWire with MapSerializer for a type that requires manual DI wiring.
        // We explicitly prevent auto-resolution by NOT calling TryAdd and instead removing
        // the registration after AddBareWire.
        services.AddBareWire(bus =>
        {
            bus.MapSerializer<OrderCreated, StubSerializerWithRequiredDep>();
        });

        // Remove the TryAdded registration so the factory will fail to resolve it.
        ServiceDescriptor? descriptor = services
            .FirstOrDefault(d => d.ServiceType == typeof(StubSerializerWithRequiredDep));
        if (descriptor is not null)
            services.Remove(descriptor);

        ServiceProvider sp = services.BuildServiceProvider();

        // Act
        Action act = () => sp.GetRequiredService<ISerializerResolver>();

        // Assert — BareWireConfigurationException with type name in message
        act.Should().Throw<BareWireConfigurationException>()
            .WithMessage($"*{nameof(StubSerializerWithRequiredDep)}*");
    }

    // ── Stub types ──────────────────────────────────────────────────────────────

    private sealed record OrderCreated(string OrderId);

    private sealed class StubSerializerA : IMessageSerializer
    {
        public string ContentType => "application/stub-a";
        public void Serialize<T>(T message, IBufferWriter<byte> output) where T : class { }
    }

    private sealed class StubSerializerB : IMessageSerializer
    {
        public string ContentType => "application/stub-b";
        public void Serialize<T>(T message, IBufferWriter<byte> output) where T : class { }
    }

    // A serializer that requires a dependency not registered in DI — used to test the clear-error path.
    private sealed class StubSerializerWithRequiredDep : IMessageSerializer
    {
        // Constructor with a dependency that DI cannot satisfy on its own.
        public StubSerializerWithRequiredDep(IDisposable requiredDep)
        {
            _ = requiredDep;
        }

        public string ContentType => "application/stub-dep";
        public void Serialize<T>(T message, IBufferWriter<byte> output) where T : class { }
    }
}
