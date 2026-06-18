using AwesomeAssertions;

using BareWire.Abstractions.Serialization;
using BareWire.Configuration;
using BareWire.Serialization.MsgPack;

using Microsoft.Extensions.DependencyInjection;

namespace BareWire.UnitTests.Serialization;

/// <summary>
/// Verifies that the per-endpoint deserializer override path works end-to-end for MessagePack.
/// </summary>
/// <remarks>
/// GAP-1 (from R3.2 plan): <c>AddBareWireMessagePackSerializer()</c> must register the concrete
/// <see cref="MessagePackDeserializer"/> type (not only <c>IMessageDeserializer</c>) so that
/// <c>BareWireBusControl.StartAsync</c> can resolve it via
/// <c>GetRequiredService(typeof(MessagePackDeserializer))</c> when
/// <c>UseDeserializer&lt;MessagePackDeserializer&gt;()</c> is configured on an endpoint.
/// </remarks>
public sealed class MessagePackPerEndpointDeserializerTests
{
    [Fact]
    public void AddBareWireMessagePackSerializer_RegistersConcreteDeserializerType_DoesNotThrow()
    {
        // GAP-1 proof: GetRequiredService(typeof(MessagePackDeserializer)) must succeed at runtime.
        // Without TryAddSingleton<MessagePackDeserializer>() in AddBareWireMessagePackSerializer(),
        // this would throw InvalidOperationException — the same failure that occurs in
        // BareWireBusControl.StartAsync when UseDeserializer<MessagePackDeserializer>() is used.
        var provider = new ServiceCollection()
            .AddBareWireMessagePackSerializer()
            .BuildServiceProvider();

        var deserializer = provider.GetRequiredService(typeof(MessagePackDeserializer));

        deserializer.Should().NotBeNull();
        deserializer.Should().BeOfType<MessagePackDeserializer>();
    }

    [Fact]
    public void UseDeserializer_MessagePackDeserializer_SetsDeserializerOverrideType()
    {
        // Verify the ReceiveEndpointConfiguration correctly stores the override type
        // so BareWireBusControl can use it for per-endpoint resolution.
        var config = new ReceiveEndpointConfiguration("test-queue");

        config.UseDeserializer<MessagePackDeserializer>();

        config.DeserializerOverrideType.Should().Be<MessagePackDeserializer>();
    }
}
