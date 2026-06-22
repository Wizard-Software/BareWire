using System.Buffers;
using System.Text;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Serialization;
using BareWire.Bus;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BareWire.UnitTests.Core.Bus;

/// <summary>
/// Tests for the MassTransit request routing wiring in <see cref="ConsumerInvokerFactory"/>
/// for the raw <see cref="IRawConsumer"/> path: when the resolved deserializer implements
/// <see cref="IRequestEnvelopeRouteReader"/> and the content-type is MT JSON, the invoker must
/// set <see cref="ConsumeContext.InboundRequestContext"/> and
/// <see cref="ConsumeContext.ResponseEnvelopeWriter"/> on the context passed to the raw consumer.
/// </summary>
public sealed class ConsumerInvokerFactoryRawMtWiringTests
{
    public sealed class CapturingRawConsumer : IRawConsumer
    {
        public RawConsumeContext? LastContext { get; private set; }

        public Task ConsumeAsync(RawConsumeContext context)
        {
            LastContext = context;
            return Task.CompletedTask;
        }
    }

    // Deserializer stub that also implements IRequestEnvelopeRouteReader.
    private sealed class MtCapableDeserializer : IMessageDeserializer, IRequestEnvelopeRouteReader
    {
        private readonly RequestEnvelopeContext _routingToReturn;
        private readonly bool _readResult;

        internal MtCapableDeserializer(RequestEnvelopeContext routing, bool readResult = true)
        {
            _routingToReturn = routing;
            _readResult = readResult;
        }

        public string ContentType => "application/vnd.masstransit+json";

        T? IMessageDeserializer.Deserialize<T>(ReadOnlySequence<byte> data) where T : class
            => null;

        public bool TryReadRequestEnvelope(ReadOnlySequence<byte> body, out RequestEnvelopeContext routing)
        {
            routing = _readResult ? _routingToReturn : default;
            return _readResult;
        }
    }

    private static (IServiceScopeFactory ScopeFactory, CapturingRawConsumer Consumer)
        BuildScopeFactoryWithMtWriter(IResponseEnvelopeWriter writer)
    {
        CapturingRawConsumer consumer = new();

        ServiceCollection services = new();
        services.AddSingleton(consumer);
        services.AddSingleton<CapturingRawConsumer>(consumer);
        services.AddSingleton(writer);
        ServiceProvider provider = services.BuildServiceProvider();

        IServiceScopeFactory scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        return (scopeFactory, consumer);
    }

    private static IDeserializerResolver BuildMtDeserializerResolver(
        RequestEnvelopeContext routing, bool readResult = true)
    {
        var deserializer = new MtCapableDeserializer(routing, readResult);
        IDeserializerResolver resolver = Substitute.For<IDeserializerResolver>();
        resolver.Resolve(Arg.Any<string?>()).Returns(deserializer);
        return resolver;
    }

    private static ReadOnlySequence<byte> BuildMtBody(Guid requestId, string responseAddress)
    {
        string json = "{\"requestId\":\"" + requestId.ToString() + "\",\"responseAddress\":\"" + responseAddress + "\",\"message\":{}}";
        return new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(json));
    }

    [Fact]
    public async Task InvokeRawConsumer_WhenMtContentTypeAndRouteReader_SetsInboundRequestContext()
    {
        // Arrange
        var requestId = Guid.NewGuid();
        const string responseAddress = "rabbitmq://localhost/raw-reply-queue";
        var routing = new RequestEnvelopeContext(responseAddress, null, null, requestId, null, null);

        IResponseEnvelopeWriter writer = Substitute.For<IResponseEnvelopeWriter>();
        var (scopeFactory, consumer) = BuildScopeFactoryWithMtWriter(writer);
        IDeserializerResolver resolver = BuildMtDeserializerResolver(routing);

        ConsumerInvokerFactory.RawInvokerDelegate rawInvoker =
            ConsumerInvokerFactory.CreateRaw(typeof(CapturingRawConsumer));

        var headers = new Dictionary<string, string>
        {
            ["content-type"] = "application/vnd.masstransit+json",
        };
        ReadOnlySequence<byte> body = BuildMtBody(requestId, responseAddress);

        // Act
        await rawInvoker(scopeFactory, body, headers, Guid.NewGuid().ToString(),
            Substitute.For<IPublishEndpoint>(), Substitute.For<ISendEndpointProvider>(),
            resolver, CancellationToken.None);

        // Assert
        consumer.LastContext.Should().NotBeNull();
        consumer.LastContext!.InboundRequestContext.Should().NotBeNull();
        consumer.LastContext.InboundRequestContext!.Value.RequestId.Should().Be(requestId);
        consumer.LastContext.InboundRequestContext.Value.ResponseAddress.Should().Be(responseAddress);
    }

    [Fact]
    public async Task InvokeRawConsumer_WhenMtContentTypeAndRouteReader_SetsResponseEnvelopeWriter()
    {
        // Arrange
        var requestId = Guid.NewGuid();
        var routing = new RequestEnvelopeContext("rabbitmq://localhost/q", null, null, requestId, null, null);

        IResponseEnvelopeWriter writer = Substitute.For<IResponseEnvelopeWriter>();
        var (scopeFactory, consumer) = BuildScopeFactoryWithMtWriter(writer);
        IDeserializerResolver resolver = BuildMtDeserializerResolver(routing);

        ConsumerInvokerFactory.RawInvokerDelegate rawInvoker =
            ConsumerInvokerFactory.CreateRaw(typeof(CapturingRawConsumer));

        var headers = new Dictionary<string, string>
        {
            ["content-type"] = "application/vnd.masstransit+json",
        };

        // Act
        await rawInvoker(scopeFactory, ReadOnlySequence<byte>.Empty, headers, Guid.NewGuid().ToString(),
            Substitute.For<IPublishEndpoint>(), Substitute.For<ISendEndpointProvider>(),
            resolver, CancellationToken.None);

        // Assert
        consumer.LastContext!.ResponseEnvelopeWriter.Should().Be(writer);
    }

    [Fact]
    public async Task InvokeRawConsumer_WhenNonMtContentType_DoesNotSetInboundRequestContext()
    {
        // Arrange
        IResponseEnvelopeWriter writer = Substitute.For<IResponseEnvelopeWriter>();
        var (scopeFactory, consumer) = BuildScopeFactoryWithMtWriter(writer);

        IMessageDeserializer plainDeserializer = Substitute.For<IMessageDeserializer>();
        plainDeserializer.ContentType.Returns("application/json");
        IDeserializerResolver resolver = Substitute.For<IDeserializerResolver>();
        resolver.Resolve(Arg.Any<string?>()).Returns(plainDeserializer);

        ConsumerInvokerFactory.RawInvokerDelegate rawInvoker =
            ConsumerInvokerFactory.CreateRaw(typeof(CapturingRawConsumer));

        var headers = new Dictionary<string, string>
        {
            ["content-type"] = "application/json",
        };

        // Act
        await rawInvoker(scopeFactory, ReadOnlySequence<byte>.Empty, headers, Guid.NewGuid().ToString(),
            Substitute.For<IPublishEndpoint>(), Substitute.For<ISendEndpointProvider>(),
            resolver, CancellationToken.None);

        // Assert
        consumer.LastContext!.InboundRequestContext.Should().BeNull();
        consumer.LastContext.ResponseEnvelopeWriter.Should().BeNull();
    }
}
