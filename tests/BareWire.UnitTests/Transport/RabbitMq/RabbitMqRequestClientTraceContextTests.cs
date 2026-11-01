using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using BareWire.Abstractions.Observability;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Interop.MassTransit;
using BareWire.Observability;
using BareWire.Transport.RabbitMQ;
using BareWire.UnitTests.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// Verifies trace-context propagation and span hygiene of <see cref="RabbitMqRequestClient{TRequest}"/>.
/// </summary>
[Collection(ActivityListenerFixtureGroup.Name)]
public sealed class RabbitMqRequestClientTraceContextTests : IDisposable
{
    private sealed record TestRequest(string Value);

    private sealed record TestResponse(string Result);

    private static readonly Uri FakeConnectionUri = new("amqp://localhost");

    private readonly List<Activity> _stopped = [];
    private readonly ServiceProvider _services;
    private ActivityListener? _listener;

    public RabbitMqRequestClientTraceContextTests()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IMeterFactory, TestMeterFactory>();
        services.AddBareWireObservability(static o => o.EnableOpenTelemetry = false);
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _listener?.Dispose();
        _services.Dispose();
    }

    private IBareWireInstrumentation Instrumentation => _services.GetRequiredService<IBareWireInstrumentation>();

    private void StartListening()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "BareWire",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (_stopped)
                {
                    _stopped.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    [Fact]
    public async Task GetResponseAsync_WithInstrumentationAndActiveListener_PublishesTraceparent()
    {
        // Arrange
        StartListening();
        var captured = new List<BasicProperties>();
        var client = CreateClient(captured, SubstituteSerializer(), Instrumentation);
        await client.InitializeAsync(TestContext.Current.CancellationToken);

        // Act
        _ = await client.GetResponseAsync<TestResponse>(new TestRequest("a"), TestContext.Current.CancellationToken);

        // Assert
        BasicProperties props = captured.Should().ContainSingle().Subject;
        props.Headers.Should().NotBeNull();
        props.Headers!.Should().ContainKey("traceparent");
        props.Headers.Should().NotContainKey("MT-Activity-Id");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task GetResponseAsync_WithMassTransitSerializer_PublishesMtActivityIdEqualToTraceparent()
    {
        // Arrange
        StartListening();
        var captured = new List<BasicProperties>();
        var client = CreateClient(captured, new MassTransitEnvelopeSerializer(), Instrumentation);
        await client.InitializeAsync(TestContext.Current.CancellationToken);

        // Act
        _ = await client.GetResponseAsync<TestResponse>(new TestRequest("a"), TestContext.Current.CancellationToken);

        // Assert
        BasicProperties props = captured.Should().ContainSingle().Subject;
        props.Headers.Should().NotBeNull();
        props.Headers!.Should().ContainKey("traceparent");
        props.Headers["MT-Activity-Id"].Should().Be(props.Headers["traceparent"]);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task GetResponseAsync_WithoutInstrumentation_PublishesNoHeaders()
    {
        // Arrange
        StartListening();
        var captured = new List<BasicProperties>();
        var client = CreateClient(captured, SubstituteSerializer(), instrumentation: null);
        await client.InitializeAsync(TestContext.Current.CancellationToken);

        // Act
        _ = await client.GetResponseAsync<TestResponse>(new TestRequest("a"), TestContext.Current.CancellationToken);

        // Assert
        captured.Should().ContainSingle().Which.Headers.Should().BeNull();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task GetResponseAsync_WithMassTransitSerializerAndNoListener_PublishesNoHeaders()
    {
        // Arrange — instrumentation present, but nobody listens to the activity source: no span, no trace headers.
        var captured = new List<BasicProperties>();
        var client = CreateClient(captured, new MassTransitEnvelopeSerializer(), Instrumentation);
        await client.InitializeAsync(TestContext.Current.CancellationToken);

        // Act
        _ = await client.GetResponseAsync<TestResponse>(new TestRequest("a"), TestContext.Current.CancellationToken);

        // Assert
        captured.Should().ContainSingle().Which.Headers.Should().BeNull();

        await client.DisposeAsync();
    }

    [Fact]
    public async Task GetResponseAsync_WithIgnoreUnmappedHeaders_StillPublishesTraceHeaders()
    {
        // Arrange
        StartListening();
        var config = new RabbitMqHeaderMappingConfigurator();
        config.IgnoreUnmappedHeaders(true);
        var captured = new List<BasicProperties>();
        var client = CreateClient(
            captured, new MassTransitEnvelopeSerializer(), Instrumentation, new RabbitMqHeaderMapper(config));
        await client.InitializeAsync(TestContext.Current.CancellationToken);

        // Act
        _ = await client.GetResponseAsync<TestResponse>(new TestRequest("a"), TestContext.Current.CancellationToken);

        // Assert — trace headers are exempt from the whitelist, exactly as on the publish path
        IDictionary<string, object?>? headers = captured.Should().ContainSingle().Which.Headers;
        headers.Should().NotBeNull();
        headers!.Should().ContainKey("traceparent");
        headers["MT-Activity-Id"].Should().Be(headers["traceparent"]);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task GetResponseAsync_WithActiveListener_SpanTagsDoNotContainCorrelationId()
    {
        // Arrange
        StartListening();
        var captured = new List<BasicProperties>();
        var client = CreateClient(captured, SubstituteSerializer(), Instrumentation);
        await client.InitializeAsync(TestContext.Current.CancellationToken);

        // Act
        _ = await client.GetResponseAsync<TestResponse>(new TestRequest("a"), TestContext.Current.CancellationToken);

        // Assert
        string correlationId = captured.Should().ContainSingle().Subject.CorrelationId!;
        Activity span = StoppedPublishSpans().Should().ContainSingle().Subject;
        span.GetTagItem("messaging.message.id").Should().Be(Guid.Empty.ToString());
        span.TagObjects.Select(static t => t.Value?.ToString()).Should().NotContain(correlationId);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task GetResponseAsync_WhenPublishThrows_DisposesPublishSpan()
    {
        // Arrange
        StartListening();
        var captured = new List<BasicProperties>();
        var client = CreateClient(
            captured, SubstituteSerializer(), Instrumentation, publishFailure: new InvalidOperationException("boom"));
        await client.InitializeAsync(TestContext.Current.CancellationToken);

        // Act
        Func<Task> act = async () => await client.GetResponseAsync<TestResponse>(
            new TestRequest("a"), TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        StoppedPublishSpans().Should().ContainSingle("the span must be stopped even when publishing fails");

        await client.DisposeAsync();
    }

    private List<Activity> StoppedPublishSpans()
    {
        lock (_stopped)
        {
            return _stopped.Where(static a => a.Kind == ActivityKind.Producer).ToList();
        }
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────

    private static IMessageSerializer SubstituteSerializer()
    {
        var serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");
        return serializer;
    }

    private static RabbitMqRequestClient<TestRequest> CreateClient(
        List<BasicProperties> captured,
        IMessageSerializer serializer,
        IBareWireInstrumentation? instrumentation,
        RabbitMqHeaderMapper? headerMapper = null,
        Exception? publishFailure = null)
    {
        var responseDeserializer = Substitute.For<IMessageDeserializer>();
        responseDeserializer
            .Deserialize<TestResponse>(Arg.Any<ReadOnlySequence<byte>>())
            .Returns(new TestResponse("ok"));

        var deserializerResolver = Substitute.For<IDeserializerResolver>();
        deserializerResolver.Resolve(Arg.Any<string?>()).Returns(responseDeserializer);

        IChannel responseChannel = Substitute.For<IChannel>();
        IChannel publishChannel = Substitute.For<IChannel>();

        responseChannel
            .QueueDeclareAsync(
                queue: Arg.Any<string>(),
                durable: Arg.Any<bool>(),
                exclusive: Arg.Any<bool>(),
                autoDelete: Arg.Any<bool>(),
                arguments: Arg.Any<IDictionary<string, object?>?>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new QueueDeclareOk("amq.gen-test-queue", 0, 0)));

        responseChannel
            .BasicConsumeAsync(
                queue: Arg.Any<string>(),
                autoAck: Arg.Any<bool>(),
                consumerTag: Arg.Any<string>(),
                noLocal: Arg.Any<bool>(),
                exclusive: Arg.Any<bool>(),
                arguments: Arg.Any<IDictionary<string, object?>?>(),
                consumer: Arg.Any<IAsyncBasicConsumer>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("consumer-tag"));

        IConnection connection = Substitute.For<IConnection>();
        connection
            .CreateChannelAsync(
                Arg.Is<CreateChannelOptions?>(o => o != null && !o.PublisherConfirmationsEnabled),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(responseChannel));
        connection
            .CreateChannelAsync(
                Arg.Is<CreateChannelOptions?>(o => o != null && o.PublisherConfirmationsEnabled),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(publishChannel));

        var client = new RabbitMqRequestClient<TestRequest>(
            connection: connection,
            serializer: serializer,
            deserializerResolver: deserializerResolver,
            logger: NullLogger.Instance,
            targetExchange: string.Empty,
            routingKey: "test-queue",
            timeout: TimeSpan.FromSeconds(5),
            connectionUri: FakeConnectionUri,
            vhost: null,
            headerMapper: headerMapper,
            instrumentation: instrumentation);

#pragma warning disable CA2012 // NSubstitute fluent setup — ValueTask intentionally not awaited here
        publishChannel
            .BasicPublishAsync<BasicProperties>(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<bool>(),
                Arg.Any<BasicProperties>(),
                Arg.Any<ReadOnlyMemory<byte>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var props = (BasicProperties)call[3];
                captured.Add(props);

                if (publishFailure is not null)
                {
                    throw publishFailure;
                }

                if (client!.TryResolvePending(
                        amqpCorrelationId: props.CorrelationId,
                        contentType: "application/json",
                        body: ReadOnlySequence<byte>.Empty,
                        out TaskCompletionSource<InboundMessage>? tcs)
                    && tcs is not null)
                {
                    tcs.TrySetResult(new InboundMessage(
                        messageId: Guid.NewGuid().ToString(),
                        headers: new Dictionary<string, string> { ["content-type"] = "application/json" },
                        body: ReadOnlySequence<byte>.Empty,
                        deliveryTag: 1));
                }

                return ValueTask.CompletedTask;
            });
#pragma warning restore CA2012

        return client;
    }
}
