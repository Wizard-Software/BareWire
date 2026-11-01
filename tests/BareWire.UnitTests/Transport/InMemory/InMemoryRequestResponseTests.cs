using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Routing;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.InMemory;
using BareWire.Serialization.Json;
using BareWire.Testing;
using BareWire.Transport.InMemory;
using BareWire.Transport.InMemory.Configuration;
using BareWire.Transport.InMemory.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class InMemoryRequestResponseTests
{
    private static async Task<(ServiceProvider Provider, IBus Bus)> StartBusAsync(
        Action<IInMemoryConfigurator>? extra = null,
        Action<IBusConfigurator>? bus = null,
        Action<ServiceCollection>? configureServices = null,
        Func<ITransportAdapter, ITransportAdapter>? decorator = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBareWireJsonSerializer();
        services.AddTransient<PingConsumer>();
        services.AddTransient<SilentConsumer>();
        configureServices?.Invoke(services);
        services.AddBareWireWithInMemory(
            t =>
            {
                t.DefaultExchange(string.Empty);
                t.AutoDeclareEndpointQueues();
                t.MapRoutingKey<PingRequest>("ping");
                t.MapRoutingKey<SilentRequest>("silent");
                t.ReceiveEndpoint("ping", e => e.Consumer<PingConsumer, PingRequest>());
                t.ReceiveEndpoint("silent", e => e.Consumer<SilentConsumer, SilentRequest>());
                extra?.Invoke(t);
            },
            bus);

        if (decorator is not null)
        {
            Decorate(services, decorator);
        }

        ServiceProvider provider = services.BuildServiceProvider();
        try
        {
            var control = provider.GetRequiredService<IBusControl>();
            await control.StartAsync(TestContext.Current.CancellationToken);
            return (provider, (IBus)control);
        }
        catch
        {
            await provider.DisposeAsync();
            throw;
        }
    }

    private static InMemoryRequestClientFactory NewFactory(
        IServiceProvider sp, TimeSpan timeout, ILoggerFactory? loggerFactory = null) =>
        new(
            () => sp.GetRequiredService<ITransportAdapter>(),
            sp.GetRequiredService<InMemoryReplyRegistry>(),
            sp.GetRequiredService<InMemoryTransportOptions>(),
            sp.GetRequiredService<ISerializerResolver>(),
            sp.GetRequiredService<IDeserializerResolver>(),
            sp.GetRequiredService<IExchangeResolver>(),
            sp.GetRequiredService<IRoutingKeyResolver>(),
            loggerFactory ?? sp.GetRequiredService<ILoggerFactory>(),
            timeout);

    // Wraps the ITransportAdapter descriptor registered by AddBareWireWithInMemory with the supplied decorator,
    // the same way BareWireTestHarness does: the decorator owns an inner adapter built from the ORIGINAL
    // descriptor, so the registry shared through DI is the one the inner adapter intercepts replies with.
    private static void Decorate(ServiceCollection services, Func<ITransportAdapter, ITransportAdapter> decorator)
    {
        ServiceDescriptor original = services.Last(d => d.ServiceType == typeof(ITransportAdapter) && !d.IsKeyedService);
        services.Remove(original);
        services.AddSingleton<ITransportAdapter>(sp => decorator(
            (ITransportAdapter)original.ImplementationFactory!(sp)));
    }

    [Fact]
    public async Task GetResponseAsync_WhenConsumerResponds_ReturnsResponse()
    {
        (ServiceProvider provider, IBus bus) = await StartBusAsync();
        await using (provider)
        {
            await using IRequestClient<PingRequest> client =
                await bus.CreateRequestClientAsync<PingRequest>(TestContext.Current.CancellationToken);

            Response<PongResponse> response = await client.GetResponseAsync<PongResponse>(
                new PingRequest("hi"), TestContext.Current.CancellationToken);

            response.Message.Text.Should().Be("pong:hi");
        }
    }

    [Fact]
    public async Task GetResponseAsync_WhenNoConsumerResponds_ThrowsRequestTimeoutException()
    {
        (ServiceProvider provider, _) = await StartBusAsync();
        await using (provider)
        {
            InMemoryRequestClientFactory factory = NewFactory(provider, TimeSpan.FromMilliseconds(200));
            await using IRequestClient<SilentRequest> client =
                await factory.CreateRequestClientAsync<SilentRequest>(TestContext.Current.CancellationToken);

            Func<Task> act = () => client.GetResponseAsync<PongResponse>(
                new SilentRequest("x"), TestContext.Current.CancellationToken);

            await act.Should().ThrowAsync<RequestTimeoutException>();
            ((InMemoryRequestClient<SilentRequest>)client).PendingCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task GetResponseAsync_WhenManyRequestsInParallel_CorrelatesEachResponse()
    {
        (ServiceProvider provider, IBus bus) = await StartBusAsync();
        await using (provider)
        {
            await using IRequestClient<PingRequest> client =
                await bus.CreateRequestClientAsync<PingRequest>(TestContext.Current.CancellationToken);
            string[] texts = [.. Enumerable.Range(0, 50).Select(i => i.ToString(CultureInfo.InvariantCulture))];

            Response<PongResponse>[] responses = await Task.WhenAll(texts.Select(t =>
                client.GetResponseAsync<PongResponse>(new PingRequest(t), TestContext.Current.CancellationToken)));

            responses.Select(r => r.Message.Text).Should().Equal(texts.Select(t => "pong:" + t));
            ((InMemoryRequestClient<PingRequest>)client).PendingCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task GetResponseAsync_WhenCallerCancels_ThrowsOperationCanceledExceptionNotTimeout()
    {
        (ServiceProvider provider, IBus bus) = await StartBusAsync();
        await using (provider)
        {
            await using IRequestClient<SilentRequest> client =
                await bus.CreateRequestClientAsync<SilentRequest>(TestContext.Current.CancellationToken);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));

            Func<Task> act = () => client.GetResponseAsync<PongResponse>(new SilentRequest("x"), cts.Token);

            (await act.Should().ThrowAsync<OperationCanceledException>()).And
                .Should().NotBeOfType<RequestTimeoutException>();
            ((InMemoryRequestClient<SilentRequest>)client).PendingCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task GetResponseAsync_WhenRequestNotAcceptedByTransport_ThrowsTransportExceptionWithoutWaiting()
    {
        (ServiceProvider provider, IBus bus) = await StartBusAsync(t =>
        {
            t.GuaranteedRouting();
            t.MapRoutingKey<UnroutedRequest>("nowhere");
        });
        await using (provider)
        {
            await using IRequestClient<UnroutedRequest> client =
                await bus.CreateRequestClientAsync<UnroutedRequest>(TestContext.Current.CancellationToken);
            var started = System.Diagnostics.Stopwatch.StartNew();

            Func<Task> act = () => client.GetResponseAsync<PongResponse>(
                new UnroutedRequest("x"), TestContext.Current.CancellationToken);

            await act.Should().ThrowAsync<BareWireTransportException>();
            started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
            ((InMemoryRequestClient<UnroutedRequest>)client).PendingCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task GetResponseAsync_WhenPendingLimitReached_ThrowsTransportException()
    {
        (ServiceProvider provider, _) = await StartBusAsync();
        await using (provider)
        {
            var client = new InMemoryRequestClient<SilentRequest>(
                provider.GetRequiredService<ITransportAdapter>(),
                provider.GetRequiredService<InMemoryReplyRegistry>(),
                provider.GetRequiredService<ISerializerResolver>().Resolve<SilentRequest>(),
                provider.GetRequiredService<IDeserializerResolver>(),
                provider.GetRequiredService<ILoggerFactory>().CreateLogger("test"),
                targetExchange: string.Empty,
                routingKey: "silent",
                timeout: TimeSpan.FromSeconds(30),
                maxPendingRequests: 2);
            await using (client)
            {
                Task<Response<PongResponse>> first = client.GetResponseAsync<PongResponse>(
                    new SilentRequest("1"), TestContext.Current.CancellationToken);
                Task<Response<PongResponse>> second = client.GetResponseAsync<PongResponse>(
                    new SilentRequest("2"), TestContext.Current.CancellationToken);
                await WaitUntilAsync(() => client.PendingCount == 2);

                Func<Task> act = () => client.GetResponseAsync<PongResponse>(
                    new SilentRequest("3"), TestContext.Current.CancellationToken);

                await act.Should().ThrowAsync<BareWireTransportException>().WithMessage("*limit*");

                await client.DisposeAsync();
                await FluentTaskCancelled(first);
                await FluentTaskCancelled(second);
            }
        }
    }

    [Fact]
    public async Task DisposeAsync_WhenRequestPending_CancelsItAndUnregistersAddress()
    {
        (ServiceProvider provider, _) = await StartBusAsync();
        await using (provider)
        {
            InMemoryRequestClientFactory factory = NewFactory(provider, TimeSpan.FromSeconds(30));
            var client = (InMemoryRequestClient<SilentRequest>)await factory
                .CreateRequestClientAsync<SilentRequest>(TestContext.Current.CancellationToken);
            Task<Response<PongResponse>> pending = client.GetResponseAsync<PongResponse>(
                new SilentRequest("x"), TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => client.PendingCount == 1);

            await client.DisposeAsync();

            await FluentTaskCancelled(pending);
            Func<Task> after = () => client.GetResponseAsync<PongResponse>(
                new SilentRequest("y"), TestContext.Current.CancellationToken);
            await after.Should().ThrowAsync<ObjectDisposedException>();
        }
    }

    [Fact]
    public async Task DisposeAsync_WhenCalled_UnregistersReplyAddressAndLateReplyReachesNoQueue()
    {
        (ServiceProvider provider, _) = await StartBusAsync();
        await using (provider)
        {
            var registry = provider.GetRequiredService<InMemoryReplyRegistry>();
            var adapter = (InMemoryTransportAdapter)provider.GetRequiredService<ITransportAdapter>();
            int before = registry.Count;
            InMemoryRequestClientFactory factory = NewFactory(provider, TimeSpan.FromSeconds(30));
            var client = (InMemoryRequestClient<PingRequest>)await factory
                .CreateRequestClientAsync<PingRequest>(TestContext.Current.CancellationToken);
            string address = client.ReplyAddress;
            registry.Count.Should().Be(before + 1);

            await client.DisposeAsync();

            registry.Count.Should().Be(before);
            var late = new OutboundMessage(
                address,
                new Dictionary<string, string> { ["BW-Exchange"] = "", ["correlation-id"] = Guid.NewGuid().ToString() },
                """{"text":"late"}"""u8.ToArray(),
                "application/json");
            IReadOnlyList<SendResult> results = await adapter.SendBatchAsync(
                [late], TestContext.Current.CancellationToken);
            results[0].IsConfirmed.Should().BeTrue();
            adapter.Broker.Queues.Should().OnlyContain(q => q.Occupancy == 0);
        }
    }

    [Fact]
    public async Task GetResponseAsync_WhenReplyHasUnknownCorrelation_DropsItAndKeepsWaiting()
    {
        var observer = new CountingBufferPoolObserver();
        var c = new InMemoryConfigurator();
        c.DefaultExchange(string.Empty);
        c.ConfigureTopology(t => t.DeclareQueue("silent"));
        InMemoryTransportOptions options = c.Build();
        var registry = new InMemoryReplyRegistry(Microsoft.Extensions.Logging.Abstractions.NullLogger<InMemoryReplyRegistry>.Instance);
        await using var adapter = new InMemoryTransportAdapter(
            options, new InMemoryBroker(options), bufferPoolObserver: observer, replies: registry);
        var client = new InMemoryRequestClient<SilentRequest>(
            adapter, registry, new SystemTextJsonSerializer(), new ContentTypeDeserializerRouter(new SystemTextJsonRawDeserializer()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, string.Empty, "silent", TimeSpan.FromSeconds(30));
        await using (client)
        {
            Task<Response<PongResponse>> pending = client.GetResponseAsync<PongResponse>(
                new SilentRequest("x"), TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => client.PendingCount == 1);
            int rentedBefore = observer.Rented;

            IReadOnlyList<SendResult> results = await adapter.SendBatchAsync(
                [new OutboundMessage(
                    client.ReplyAddress,
                    new Dictionary<string, string> { ["BW-Exchange"] = "", ["correlation-id"] = Guid.NewGuid().ToString() },
                    """{"text":"stray"}"""u8.ToArray(),
                    "application/json")],
                TestContext.Current.CancellationToken);

            results[0].IsConfirmed.Should().BeTrue();
            observer.Rented.Should().Be(rentedBefore, "a reply with an unknown correlation must not rent a buffer");
            pending.IsCompleted.Should().BeFalse();
            client.PendingCount.Should().Be(1);

            await client.DisposeAsync();
            await FluentTaskCancelled(pending);
        }
    }

    [Fact]
    public async Task CreateRequestClientAsync_WhenMapExchangeAndRoutingKeyConfigured_UsesPerTypeMappings()
    {
        (ServiceProvider provider, _) = await StartBusAsync(
            t =>
            {
                t.ConfigureTopology(x => x.DeclareExchange("req-ex", ExchangeType.Direct));
                t.MapExchange<MappedRequest>("req-ex");
                t.MapRoutingKey<MappedRequest>("mapped-key");
            },
            bus: b => b.MapSerializer<MappedRequest, MarkerSerializer>());
        await using (provider)
        {
            InMemoryRequestClientFactory factory = NewFactory(provider, TimeSpan.FromSeconds(30));

            (IMessageSerializer serializer, string exchange, string routingKey) = factory.ResolveDispatch<MappedRequest>();
            (IMessageSerializer defaultSerializer, string defaultExchange, string defaultKey) =
                factory.ResolveDispatch<PingRequest>();

            serializer.Should().BeOfType<MarkerSerializer>();
            exchange.Should().Be("req-ex");
            routingKey.Should().Be("mapped-key");
            defaultSerializer.Should().NotBeOfType<MarkerSerializer>();
            defaultExchange.Should().BeEmpty();
            defaultKey.Should().Be("ping");
        }
    }

    [Fact]
    public async Task ResolveDispatch_WhenNoExchangeConfigured_ThrowsConfigurationException()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBareWireJsonSerializer();
        services.AddBareWireWithInMemory(t => t.ConfigureTopology(x => x.DeclareQueue("q")));
        await using ServiceProvider provider = services.BuildServiceProvider();
        InMemoryRequestClientFactory factory = NewFactory(provider, TimeSpan.FromSeconds(30));

        Action act = () => factory.ResolveDispatch<PingRequest>();

        act.Should().Throw<BareWireConfigurationException>().WithMessage("*MapExchange*");
    }

    [Fact]
    public async Task GetResponseAsync_WhenTransportAdapterIsDecorated_ReturnsResponse()
    {
        RecordingAdapter? recording = null;
        (ServiceProvider provider, IBus bus) = await StartBusAsync(
            decorator: inner => recording = new RecordingAdapter(inner));
        await using (provider)
        {
            await using IRequestClient<PingRequest> client =
                await bus.CreateRequestClientAsync<PingRequest>(TestContext.Current.CancellationToken);

            Response<PongResponse> response = await client.GetResponseAsync<PongResponse>(
                new PingRequest("deco"), TestContext.Current.CancellationToken);

            response.Message.Text.Should().Be("pong:deco");
            provider.GetRequiredService<ITransportAdapter>().Should().BeOfType<RecordingAdapter>();
            recording!.Sent.Should().Contain(m => m.Headers.ContainsKey("ReplyTo"));
        }
    }

    [Fact]
    public async Task BareWireTestHarness_WhenCreated_StillBuildsAndStarts()
    {
        await using BareWireTestHarness harness = await BareWireTestHarness.CreateAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        harness.Bus.Should().NotBeNull();
    }

    [Fact]
    public async Task GetResponseAsync_WhenFailuresOccur_LogsAndExceptionsNeverContainCorrelationOrBody()
    {
        var clientLogger = new CapturingLogger<InMemoryRequestClient<SilentRequest>>();
        var registryLogger = new CapturingLogger<InMemoryReplyRegistry>();
        RecordingAdapter? recording = null;
        (ServiceProvider provider, _) = await StartBusAsync(
            t =>
            {
                t.GuaranteedRouting();
                t.MapRoutingKey<UnroutedRequest>("nowhere");
            },
            configureServices: s => s.AddSingleton<ILogger<InMemoryReplyRegistry>>(registryLogger),
            decorator: inner => recording = new RecordingAdapter(inner));
        await using (provider)
        {
            const string secretBody = "SECRET-BODY-TEXT";
            var factory = NewFactory(provider, TimeSpan.FromMilliseconds(150), new SingleLoggerFactory(clientLogger));
            var messages = new List<string>();

            // Timeout path.
            await using (IRequestClient<SilentRequest> silent =
                await factory.CreateRequestClientAsync<SilentRequest>(TestContext.Current.CancellationToken))
            {
                Exception timeout = (await Assert.ThrowsAsync<RequestTimeoutException>(() =>
                    silent.GetResponseAsync<PongResponse>(new SilentRequest(secretBody), TestContext.Current.CancellationToken)));
                messages.Add(timeout.Message);
            }

            // Rejected-send path.
            await using (IRequestClient<UnroutedRequest> unrouted =
                await factory.CreateRequestClientAsync<UnroutedRequest>(TestContext.Current.CancellationToken))
            {
                Exception rejected = await Assert.ThrowsAsync<BareWireTransportException>(() =>
                    unrouted.GetResponseAsync<PongResponse>(new UnroutedRequest(secretBody), TestContext.Current.CancellationToken));
                messages.Add(rejected.Message);
            }

            // Late reply path (address unregistered by the dispose above).
            var adapter = provider.GetRequiredService<ITransportAdapter>();
            string lateAddress = recording!.Sent.Last(m => m.Headers.ContainsKey("ReplyTo")).Headers["ReplyTo"];
            string correlation = recording.Sent.Last(m => m.Headers.ContainsKey("correlation-id")).Headers["correlation-id"];
            await adapter.SendBatchAsync(
                [new OutboundMessage(
                    lateAddress,
                    new Dictionary<string, string> { ["BW-Exchange"] = "", ["correlation-id"] = correlation },
                    System.Text.Encoding.UTF8.GetBytes(secretBody),
                    "application/json")],
                TestContext.Current.CancellationToken);

            string[] correlations = [.. recording.Sent.Select(m => m.Headers.GetValueOrDefault("correlation-id") ?? "x")
                .Where(c => c.Length > 8)];
            IEnumerable<string> everything = messages
                .Concat(clientLogger.Entries.SelectMany(Flatten))
                .Concat(registryLogger.Entries.SelectMany(Flatten));
            foreach (string text in everything)
            {
                text.Should().NotContain(secretBody);
                foreach (string c in correlations)
                {
                    text.Should().NotContain(c);
                }
            }

            registryLogger.Entries.Should().Contain(e => e.Level == LogLevel.Debug);
        }

        static IEnumerable<string> Flatten(LogEntry e) =>
            new[] { e.Message }.Concat(e.State.Select(p => Convert.ToString(p.Value, CultureInfo.InvariantCulture) ?? string.Empty));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(1, cts.Token);
        }
    }

    private static async Task FluentTaskCancelled<T>(Task<T> task)
    {
        Func<Task> act = () => task;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class SingleLoggerFactory(ILogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => logger;

        public void Dispose()
        {
        }
    }

    private sealed class RecordingAdapter(ITransportAdapter inner) : ITransportAdapter, IAsyncDisposable
    {
        private readonly ConcurrentQueue<OutboundMessage> _sent = new();

        internal IReadOnlyCollection<OutboundMessage> Sent => _sent;

        public string TransportName => inner.TransportName;

        public TransportCapabilities Capabilities => inner.Capabilities;

        public Task<IReadOnlyList<SendResult>> SendBatchAsync(
            IReadOnlyList<OutboundMessage> messages, CancellationToken cancellationToken = default)
        {
            foreach (OutboundMessage message in messages)
            {
                _sent.Enqueue(message);
            }

            return inner.SendBatchAsync(messages, cancellationToken);
        }

        public IAsyncEnumerable<InboundMessage> ConsumeAsync(
            string endpointName, FlowControlOptions flowControl, CancellationToken cancellationToken = default) =>
            inner.ConsumeAsync(endpointName, flowControl, cancellationToken);

        public Task SettleAsync(
            SettlementAction action, InboundMessage message, CancellationToken cancellationToken = default) =>
            inner.SettleAsync(action, message, cancellationToken);

        public Task DeployTopologyAsync(TopologyDeclaration topology, CancellationToken cancellationToken = default) =>
            inner.DeployTopologyAsync(topology, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (inner is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }
        }
    }
}

public sealed record PingRequest(string Text);

public sealed record PongResponse(string Text);

public sealed record SilentRequest(string Text);

public sealed record UnroutedRequest(string Text);

public sealed record MappedRequest(string Text);

internal sealed class PingConsumer : IConsumer<PingRequest>
{
    public Task ConsumeAsync(ConsumeContext<PingRequest> context) =>
        context.RespondAsync(new PongResponse("pong:" + context.Message.Text));
}

internal sealed class SilentConsumer : IConsumer<SilentRequest>
{
    public Task ConsumeAsync(ConsumeContext<SilentRequest> context) => Task.CompletedTask;
}

public sealed class MarkerSerializer : IMessageSerializer
{
    public string ContentType => "application/json";

    public void Serialize<T>(T message, IBufferWriter<byte> output) where T : class
    {
        using var writer = new Utf8JsonWriter(output);
        JsonSerializer.Serialize(writer, message);
    }
}
