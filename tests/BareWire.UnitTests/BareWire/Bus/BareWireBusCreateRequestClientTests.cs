// NSubstitute's Returns() for ValueTask-returning mocks triggers CA2012 as a false positive.
// See: https://github.com/nsubstitute/NSubstitute/issues/597
#pragma warning disable CA2012

using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.FlowControl;
using BareWire;
using BareWire.Pipeline;
using BareWire.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Core.Bus;

// Must be public so NSubstitute's Castle proxy can create IRequestClient<PingRequest>.
public sealed record PingRequest(string Value);

public sealed class BareWireBusCreateRequestClientTests
{

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static BareWireBus CreateBus(IRequestClientFactory? factory = null)
    {
        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("test");

        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");

        IDeserializerResolver deserializerResolver = Substitute.For<IDeserializerResolver>();

        MiddlewareChain chain = new([]);
        MessagePipeline pipeline = new(
            chain,
            deserializerResolver,
            NullLogger<MessagePipeline>.Instance,
            new NullInstrumentation());
        FlowController flowController = new(NullLogger<FlowController>.Instance);

        return new BareWireBus(
            adapter,
            new DefaultSerializerResolver(serializer),
            pipeline,
            flowController,
            new PublishFlowControlOptions(),
            NullLogger<BareWireBus>.Instance,
            new NullInstrumentation(),
            routingKeyResolver: null,
            requestClientFactory: factory);
    }

    // ── CreateRequestClientAsync ──────────────────────────────────────────────

    [Fact]
    public async Task CreateRequestClientAsync_WhenFactoryRegistered_DelegatesToFactory()
    {
        // Arrange
        IRequestClient<PingRequest> expectedClient = Substitute.For<IRequestClient<PingRequest>>();
        IRequestClientFactory factory = Substitute.For<IRequestClientFactory>();
        factory.CreateRequestClientAsync<PingRequest>(Arg.Any<CancellationToken>())
               .Returns(_ => new ValueTask<IRequestClient<PingRequest>>(expectedClient));

        BareWireBus bus = CreateBus(factory);

        // Act
        IRequestClient<PingRequest> result = await bus.CreateRequestClientAsync<PingRequest>();

        // Assert
        result.Should().BeSameAs(expectedClient);
        await factory.Received(1).CreateRequestClientAsync<PingRequest>(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateRequestClientAsync_WhenNoFactoryRegistered_ThrowsNotSupportedException()
    {
        // Arrange — no factory, null is passed
        BareWireBus bus = CreateBus(factory: null);

        // Act
        Func<Task> act = async () => await bus.CreateRequestClientAsync<PingRequest>();

        // Assert
        await act.Should().ThrowAsync<NotSupportedException>()
                 .WithMessage("*IRequestClientFactory*");
    }
}
