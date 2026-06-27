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
/// Tests for the MassTransit request routing wiring in <see cref="ConsumerInvokerFactory"/>:
/// when the resolved deserializer implements <see cref="IRequestEnvelopeRouteReader"/> (selected
/// per-consumer via the D4 precedence, independent of the inbound content-type header), the invoker
/// must set <see cref="ConsumeContext.InboundRequestContext"/> and <see cref="ConsumeContext.ResponseEnvelopeWriter"/>
/// on the context passed to the consumer.
/// </summary>
public sealed class ConsumerInvokerFactoryMtWiringTests
{
    // Consumer that captures its context for assertion.
    public sealed class CapturingTypedConsumer : IConsumer<InvokerTestMessage>
    {
        public ConsumeContext<InvokerTestMessage>? LastContext { get; private set; }

        public Task ConsumeAsync(ConsumeContext<InvokerTestMessage> context)
        {
            LastContext = context;
            return Task.CompletedTask;
        }
    }

    // A deserializer stub that also implements IRequestEnvelopeRouteReader.
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
            => new InvokerTestMessage("from-mt") as T;

        public bool TryReadRequestEnvelope(ReadOnlySequence<byte> body, out RequestEnvelopeContext routing)
        {
            routing = _readResult ? _routingToReturn : default;
            return _readResult;
        }
    }

    private static IResponseEnvelopeWriter BuildCapturingWriter()
    {
        IResponseEnvelopeWriter writer = Substitute.For<IResponseEnvelopeWriter>();
        return writer;
    }

    private static (IServiceScopeFactory ScopeFactory, CapturingTypedConsumer Consumer)
        BuildScopeFactoryWithMtWriter(IResponseEnvelopeWriter writer)
    {
        CapturingTypedConsumer consumer = new();

        ServiceCollection services = new();
        services.AddSingleton(consumer);
        services.AddSingleton<CapturingTypedConsumer>(consumer);
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

    private static IDeserializerResolver BuildPlainDeserializerResolver()
    {
        IMessageDeserializer deserializer = Substitute.For<IMessageDeserializer>();
        deserializer.ContentType.Returns("application/json");
        deserializer.Deserialize<InvokerTestMessage>(Arg.Any<ReadOnlySequence<byte>>())
                    .Returns(new InvokerTestMessage("plain"));

        IDeserializerResolver resolver = Substitute.For<IDeserializerResolver>();
        resolver.Resolve(Arg.Any<string?>()).Returns(deserializer);
        return resolver;
    }

    private static ReadOnlySequence<byte> BuildMtRequestEnvelopeBody(Guid requestId, string responseAddress)
    {
        string json = "{\"requestId\":\"" + requestId.ToString() + "\",\"responseAddress\":\"" + responseAddress + "\",\"message\":{\"value\":\"test\"}}";
        return new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(json));
    }

    // ── Typed path: MT routing wired when deserializer is IRequestEnvelopeRouteReader ──

    [Fact]
    public async Task InvokeTypedConsumer_WhenMtContentTypeAndRouteReader_SetsInboundRequestContext()
    {
        // Arrange
        var requestId = Guid.NewGuid();
        const string responseAddress = "rabbitmq://localhost/reply-queue";
        var routing = new RequestEnvelopeContext(responseAddress, null, null, requestId, null, null);

        IResponseEnvelopeWriter writer = BuildCapturingWriter();
        var (scopeFactory, consumer) = BuildScopeFactoryWithMtWriter(writer);
        IDeserializerResolver resolver = BuildMtDeserializerResolver(routing);

        ConsumerInvokerFactory.InvokerDelegate invoker =
            ConsumerInvokerFactory.Create(typeof(CapturingTypedConsumer), typeof(InvokerTestMessage));

        var headers = new Dictionary<string, string>
        {
            ["content-type"] = "application/vnd.masstransit+json",
        };
        ReadOnlySequence<byte> body = BuildMtRequestEnvelopeBody(requestId, responseAddress);

        // Act
        await invoker(scopeFactory, body, headers, Guid.NewGuid().ToString(),
            Substitute.For<IPublishEndpoint>(), Substitute.For<ISendEndpointProvider>(),
            resolver, "test-ep", CancellationToken.None);

        // Assert
        consumer.LastContext.Should().NotBeNull();
        consumer.LastContext!.InboundRequestContext.Should().NotBeNull();
        consumer.LastContext.InboundRequestContext!.Value.RequestId.Should().Be(requestId);
        consumer.LastContext.InboundRequestContext.Value.ResponseAddress.Should().Be(responseAddress);
    }

    [Fact]
    public async Task InvokeTypedConsumer_WhenMtContentTypeAndRouteReader_SetsResponseEnvelopeWriter()
    {
        // Arrange
        var requestId = Guid.NewGuid();
        var routing = new RequestEnvelopeContext("rabbitmq://localhost/q", null, null, requestId, null, null);

        IResponseEnvelopeWriter writer = BuildCapturingWriter();
        var (scopeFactory, consumer) = BuildScopeFactoryWithMtWriter(writer);
        IDeserializerResolver resolver = BuildMtDeserializerResolver(routing);

        ConsumerInvokerFactory.InvokerDelegate invoker =
            ConsumerInvokerFactory.Create(typeof(CapturingTypedConsumer), typeof(InvokerTestMessage));

        var headers = new Dictionary<string, string>
        {
            ["content-type"] = "application/vnd.masstransit+json",
        };
        ReadOnlySequence<byte> body = BuildMtRequestEnvelopeBody(requestId, "rabbitmq://localhost/q");

        // Act
        await invoker(scopeFactory, body, headers, Guid.NewGuid().ToString(),
            Substitute.For<IPublishEndpoint>(), Substitute.For<ISendEndpointProvider>(),
            resolver, "test-ep", CancellationToken.None);

        // Assert — ResponseEnvelopeWriter must be set to the resolved writer
        consumer.LastContext!.ResponseEnvelopeWriter.Should().Be(writer);
    }

    [Fact]
    public async Task InvokeTypedConsumer_WhenNonMtContentType_DoesNotSetInboundRequestContext()
    {
        // Arrange — plain JSON content type: MT path must not engage
        IResponseEnvelopeWriter writer = BuildCapturingWriter();
        var (scopeFactory, consumer) = BuildScopeFactoryWithMtWriter(writer);
        IDeserializerResolver resolver = BuildPlainDeserializerResolver();

        ConsumerInvokerFactory.InvokerDelegate invoker =
            ConsumerInvokerFactory.Create(typeof(CapturingTypedConsumer), typeof(InvokerTestMessage));

        var headers = new Dictionary<string, string>
        {
            ["content-type"] = "application/json",
        };

        // Act
        await invoker(scopeFactory, ReadOnlySequence<byte>.Empty, headers, Guid.NewGuid().ToString(),
            Substitute.For<IPublishEndpoint>(), Substitute.For<ISendEndpointProvider>(),
            resolver, "test-ep", CancellationToken.None);

        // Assert
        consumer.LastContext!.InboundRequestContext.Should().BeNull();
        consumer.LastContext.ResponseEnvelopeWriter.Should().BeNull();
    }

    [Fact]
    public async Task InvokeTypedConsumer_WhenMtContentTypeButRouteReadFails_DoesNotSetInboundRequestContext()
    {
        // Arrange — MT content type but TryReadRequestEnvelope returns false
        var routing = default(RequestEnvelopeContext);
        IResponseEnvelopeWriter writer = BuildCapturingWriter();
        var (scopeFactory, consumer) = BuildScopeFactoryWithMtWriter(writer);
        IDeserializerResolver resolver = BuildMtDeserializerResolver(routing, readResult: false);

        ConsumerInvokerFactory.InvokerDelegate invoker =
            ConsumerInvokerFactory.Create(typeof(CapturingTypedConsumer), typeof(InvokerTestMessage));

        var headers = new Dictionary<string, string>
        {
            ["content-type"] = "application/vnd.masstransit+json",
        };

        // Act
        await invoker(scopeFactory, ReadOnlySequence<byte>.Empty, headers, Guid.NewGuid().ToString(),
            Substitute.For<IPublishEndpoint>(), Substitute.For<ISendEndpointProvider>(),
            resolver, "test-ep", CancellationToken.None);

        // Assert — no routing set when read returns false
        consumer.LastContext!.InboundRequestContext.Should().BeNull();
        consumer.LastContext.ResponseEnvelopeWriter.Should().BeNull();
    }

    // ── Flag-driven wiring: gate on deserializer capability, not on content-type header ──

    [Fact]
    public async Task InvokeTypedConsumer_WhenRouteReaderSelectedAndNoMtContentTypeHeader_SetsInboundRequestContext()
    {
        // Arrange — resolver returns an MT-capable (route-reader) deserializer, but headers carry
        // no content-type key. The wiring gate must be driven by the per-consumer deserializer
        // selection (D4 precedence), not by the header value.
        var requestId = Guid.NewGuid();
        const string responseAddress = "rabbitmq://localhost/reply-no-header";
        var routing = new RequestEnvelopeContext(responseAddress, null, null, requestId, null, null);

        IResponseEnvelopeWriter writer = BuildCapturingWriter();
        var (scopeFactory, consumer) = BuildScopeFactoryWithMtWriter(writer);
        IDeserializerResolver resolver = BuildMtDeserializerResolver(routing);

        ConsumerInvokerFactory.InvokerDelegate invoker =
            ConsumerInvokerFactory.Create(typeof(CapturingTypedConsumer), typeof(InvokerTestMessage));

        // Headers deliberately contain no content-type — simulates a producer that omits the header.
        var headers = new Dictionary<string, string>();
        ReadOnlySequence<byte> body = BuildMtRequestEnvelopeBody(requestId, responseAddress);

        // Act
        await invoker(scopeFactory, body, headers, Guid.NewGuid().ToString(),
            Substitute.For<IPublishEndpoint>(), Substitute.For<ISendEndpointProvider>(),
            resolver, "test-ep", CancellationToken.None);

        // Assert — wiring must be set because the per-consumer resolver returned a route-reader.
        consumer.LastContext.Should().NotBeNull();
        consumer.LastContext!.InboundRequestContext.Should().NotBeNull();
        consumer.LastContext.InboundRequestContext!.Value.RequestId.Should().Be(requestId);
    }

    [Fact]
    public async Task InvokeTypedConsumer_WhenRouteReaderSelectedAndPlainContentTypeHeader_SetsResponseEnvelopeWriter()
    {
        // Arrange — resolver returns the MT-capable (route-reader) deserializer even though the
        // inbound content-type header says "application/json" (per-consumer D4 selection overrides).
        var requestId = Guid.NewGuid();
        const string responseAddress = "rabbitmq://localhost/reply-mismatched-header";
        var routing = new RequestEnvelopeContext(responseAddress, null, null, requestId, null, null);

        IResponseEnvelopeWriter writer = BuildCapturingWriter();
        var (scopeFactory, consumer) = BuildScopeFactoryWithMtWriter(writer);
        IDeserializerResolver resolver = BuildMtDeserializerResolver(routing);

        ConsumerInvokerFactory.InvokerDelegate invoker =
            ConsumerInvokerFactory.Create(typeof(CapturingTypedConsumer), typeof(InvokerTestMessage));

        // Mismatched header: content-type says plain JSON, but the resolver was pre-configured to
        // return the MT deserializer (simulating per-consumer UseMassTransitEnvelope opt-in).
        var headers = new Dictionary<string, string>
        {
            ["content-type"] = "application/json",
        };
        ReadOnlySequence<byte> body = BuildMtRequestEnvelopeBody(requestId, responseAddress);

        // Act
        await invoker(scopeFactory, body, headers, Guid.NewGuid().ToString(),
            Substitute.For<IPublishEndpoint>(), Substitute.For<ISendEndpointProvider>(),
            resolver, "test-ep", CancellationToken.None);

        // Assert — ResponseEnvelopeWriter must be set because the resolver returned a route-reader.
        consumer.LastContext!.ResponseEnvelopeWriter.Should().Be(writer);
    }
}
