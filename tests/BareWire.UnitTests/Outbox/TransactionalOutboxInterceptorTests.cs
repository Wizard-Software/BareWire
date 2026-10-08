// NSubstitute's Returns() for ValueTask-returning mocks triggers CA2012 as a known false positive.
#pragma warning disable CA2012

using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Observability;
using BareWire.Abstractions.Pipeline;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.FlowControl;
using BareWire.Outbox;
using BareWire.Outbox.EntityFramework;
using BareWire.Outbox.EntityFramework.Internal;
using BareWire.Pipeline;
using BareWire.Pipeline.Retry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Outbox;

public sealed class TransactionalOutboxInterceptorTests
{
    private sealed record TestEvent(string Id);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly object _gate = new();
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_gate)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static OutboundMessage CreateMessage(string messageId = "m-1", IDictionary<string, string>? extra = null)
    {
        Dictionary<string, string> headers = new() { ["message-id"] = messageId, ["BW-MessageType"] = "TestEvent" };
        if (extra is not null)
        {
            foreach (KeyValuePair<string, string> kv in extra)
                headers[kv.Key] = kv.Value;
        }

        return new OutboundMessage("rk", headers, new byte[] { 1, 2, 3 }, "application/json");
    }

    private static MessageContext CreateContext()
        => new(
            messageId: Guid.NewGuid(),
            headers: new Dictionary<string, string>(),
            rawBody: ReadOnlySequence<byte>.Empty,
            serviceProvider: Substitute.For<IServiceProvider>(),
            endpointName: "test-endpoint");

    private static (TransactionalOutboxMiddleware Middleware, IOutboxStore Store) CreateMiddleware(
        bool useAmbientTransaction,
        OutboxOptions? options = null,
        ILogger<TransactionalOutboxMiddleware>? logger = null)
    {
        IInboxStore inboxStore = Substitute.For<IInboxStore>();
        inboxStore
            .TryLockAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(true));
        inboxStore
            .MarkProcessedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);

        InboxFilter inboxFilter = new(inboxStore, OutboxOptions.Default, NullLogger<InboxFilter>.Instance);

        // Same pattern as TransactionalOutboxMiddlewareTests: SQLite in-memory only to instantiate EF Core.
        DbContextOptions<OutboxDbContext> dbOptions = new DbContextOptionsBuilder<OutboxDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        OutboxDbContext dbContext = new(dbOptions);

        IOutboxStore outboxStore = Substitute.For<IOutboxStore>();
        TransactionalOutboxMiddleware middleware = new(
            dbContext,
            outboxStore,
            inboxFilter,
            logger ?? NullLogger<TransactionalOutboxMiddleware>.Instance,
            new OutboxTransactionMode(useAmbientTransaction),
            options);

        return (middleware, outboxStore);
    }

    private static (BareWireBus Bus, ITransportAdapter Adapter) CreateBus(IOutboundMessageInterceptor interceptor)
    {
        ITransportAdapter adapter = Substitute.For<ITransportAdapter>();
        adapter.TransportName.Returns("test");
        adapter.SendBatchAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<SendResult>>(
                call.ArgAt<IReadOnlyList<OutboundMessage>>(0).Select(static _ => new SendResult(true, 0UL)).ToList()));

        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");
        ISerializerResolver serializerResolver = Substitute.For<ISerializerResolver>();
        serializerResolver.Resolve<TestEvent>().Returns(serializer);

        IBareWireInstrumentation instrumentation = new NullInstrumentation();
        MessagePipeline pipeline = new(
            new MiddlewareChain([]),
            Substitute.For<IDeserializerResolver>(),
            NullLogger<MessagePipeline>.Instance,
            instrumentation);

        BareWireBus bus = new(
            adapter,
            serializerResolver,
            pipeline,
            new FlowController(NullLogger<FlowController>.Instance),
            new PublishFlowControlOptions(),
            NullLogger<BareWireBus>.Instance,
            instrumentation,
            outboundInterceptor: interceptor);

        bus.StartPublishing();
        return (bus, adapter);
    }

    private static TransactionalOutboxInterceptor CreateInterceptor(
        ILogger<TransactionalOutboxInterceptor>? logger = null)
        => new(logger ?? NullLogger<TransactionalOutboxInterceptor>.Instance);

    // ── Interceptor + middleware ──────────────────────────────────────────────

    [Fact]
    public void TryIntercept_WhenNoOutboxScopeActive_ReturnsFalse()
    {
        TransactionalOutboxInterceptor sut = CreateInterceptor();

        sut.TryIntercept(CreateMessage()).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvokeAsync_WhenHandlerPublishesAndSucceeds_SavesInterceptedMessageToOutboxStore(bool useAmbientTransaction)
    {
        var (middleware, store) = CreateMiddleware(useAmbientTransaction);
        TransactionalOutboxInterceptor sut = CreateInterceptor();
        OutboundMessage message = CreateMessage();
        bool captured = false;

        await middleware.InvokeAsync(CreateContext(), _ =>
        {
            captured = sut.TryIntercept(message);
            return Task.CompletedTask;
        });

        captured.Should().BeTrue();
        await store.Received(1).SaveMessagesAsync(
            Arg.Is<IReadOnlyList<OutboundMessage>>(l => l.Count == 1 && ReferenceEquals(l[0], message)),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvokeAsync_WhenHandlerPublishesThenThrows_DoesNotSaveInterceptedMessage(bool useAmbientTransaction)
    {
        var (middleware, store) = CreateMiddleware(useAmbientTransaction);
        TransactionalOutboxInterceptor sut = CreateInterceptor();

        Func<Task> act = () => middleware.InvokeAsync(CreateContext(), _ =>
        {
            sut.TryIntercept(CreateMessage());
            throw new InvalidOperationException("boom");
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
        await store.DidNotReceiveWithAnyArgs().SaveMessagesAsync(default!, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TryIntercept_FromFlowCapturedInHandlerAfterMiddlewareCompleted_ReturnsFalseAndLogsWarning(bool useAmbientTransaction)
    {
        var (middleware, store) = CreateMiddleware(useAmbientTransaction);
        RecordingLogger<TransactionalOutboxInterceptor> logger = new();
        TransactionalOutboxInterceptor sut = CreateInterceptor(logger);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? late = null;

        await middleware.InvokeAsync(CreateContext(), _ =>
        {
            sut.TryIntercept(CreateMessage("on-time")).Should().BeTrue();

            // Fire-and-forget work that outlives the handler: Task.Run captures the AsyncLocal buffer.
            late = Task.Run(async () =>
            {
                await release.Task;
                return sut.TryIntercept(CreateMessage("late-id"));
            });
            return Task.CompletedTask;
        });

        release.SetResult();
        bool intercepted = await late!;

        intercepted.Should().BeFalse("a sealed buffer must refuse the message so it goes to the transport");
        await store.Received(1).SaveMessagesAsync(
            Arg.Is<IReadOnlyList<OutboundMessage>>(l => l.Count == 1),
            Arg.Any<CancellationToken>());
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains("late-id"));
        logger.Entries.Single(e => e.Level == LogLevel.Warning).Message
            .Should().Contain("TestEvent").And.NotContain("BW-MessageType");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishAsync_FromFlowCapturedInHandlerAfterMiddlewareCompleted_ReachesTransport(bool useAmbientTransaction)
    {
        var (middleware, store) = CreateMiddleware(useAmbientTransaction);
        RecordingLogger<TransactionalOutboxInterceptor> logger = new();
        TransactionalOutboxInterceptor interceptor = CreateInterceptor(logger);
        var (bus, adapter) = CreateBus(interceptor);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? late = null;

        await middleware.InvokeAsync(CreateContext(), _ =>
        {
            late = Task.Run(async () =>
            {
                await release.Task;
                await bus.PublishAsync(new TestEvent("late"), TestContext.Current.CancellationToken);
            });
            return Task.CompletedTask;
        });

        release.SetResult();
        await late!;
        await Task.Delay(150, TestContext.Current.CancellationToken);

        await adapter.ReceivedWithAnyArgs().SendBatchAsync(default!, default);
        await store.DidNotReceiveWithAnyArgs().SaveMessagesAsync(default!, default);

        // The sealed buffer is still ambient, so IsCapturing is true, the bus calls TryIntercept, the
        // interceptor declines and logs a Warning, and the message then goes to the transport.
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);

        await bus.DisposeAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvokeAsync_WhenHandlerExceedsMaxBufferedMessages_ThrowsBareWireExceptionAndSavesNothing(bool useAmbientTransaction)
    {
        var (middleware, store) = CreateMiddleware(useAmbientTransaction, new OutboxOptions { MaxBufferedMessagesPerConsume = 2 });
        TransactionalOutboxInterceptor sut = CreateInterceptor();

        Func<Task> act = () => middleware.InvokeAsync(CreateContext(), _ =>
        {
            for (int i = 0; i < 3; i++)
                sut.TryIntercept(CreateMessage($"m-{i}"));
            return Task.CompletedTask;
        });

        await act.Should().ThrowAsync<BareWireException>().WithMessage("*MaxBufferedMessagesPerConsume*");
        await store.DidNotReceiveWithAnyArgs().SaveMessagesAsync(default!, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvokeAsync_WhenHandlerBuffersExactlyMaxMessages_SavesThemAll(bool useAmbientTransaction)
    {
        var (middleware, store) = CreateMiddleware(useAmbientTransaction, new OutboxOptions { MaxBufferedMessagesPerConsume = 2 });
        TransactionalOutboxInterceptor sut = CreateInterceptor();

        await middleware.InvokeAsync(CreateContext(), _ =>
        {
            sut.TryIntercept(CreateMessage("a")).Should().BeTrue();
            sut.TryIntercept(CreateMessage("b")).Should().BeTrue();
            return Task.CompletedTask;
        });

        await store.Received(1).SaveMessagesAsync(
            Arg.Is<IReadOnlyList<OutboundMessage>>(l => l.Count == 2),
            Arg.Any<CancellationToken>());
    }

    private static OutboundMessage CreateSizedMessage(int size)
        => new("rk", new Dictionary<string, string> { ["message-id"] = "m" }, new byte[size], "application/json");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvokeAsync_WhenHandlerExceedsMaxBufferedBytes_ThrowsBareWireExceptionAndSavesNothing(bool useAmbientTransaction)
    {
        var (middleware, store) = CreateMiddleware(useAmbientTransaction, new OutboxOptions { MaxBufferedBytesPerConsume = 10 });
        TransactionalOutboxInterceptor sut = CreateInterceptor();

        Func<Task> act = () => middleware.InvokeAsync(CreateContext(), _ =>
        {
            sut.TryIntercept(CreateSizedMessage(6));
            sut.TryIntercept(CreateSizedMessage(5));
            return Task.CompletedTask;
        });

        await act.Should().ThrowAsync<BareWireException>().WithMessage("*MaxBufferedBytesPerConsume*");
        await store.DidNotReceiveWithAnyArgs().SaveMessagesAsync(default!, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvokeAsync_WhenHandlerBuffersExactlyMaxBytes_SavesThemAll(bool useAmbientTransaction)
    {
        var (middleware, store) = CreateMiddleware(useAmbientTransaction, new OutboxOptions { MaxBufferedBytesPerConsume = 10 });
        TransactionalOutboxInterceptor sut = CreateInterceptor();

        await middleware.InvokeAsync(CreateContext(), _ =>
        {
            sut.TryIntercept(CreateSizedMessage(6)).Should().BeTrue();
            sut.TryIntercept(CreateSizedMessage(4)).Should().BeTrue();
            return Task.CompletedTask;
        });

        await store.Received(1).SaveMessagesAsync(
            Arg.Is<IReadOnlyList<OutboundMessage>>(l => l.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void OutboxOptions_Validate_WhenMaxBufferedBytesNotPositive_Throws()
    {
        OutboxOptions options = new() { MaxBufferedBytesPerConsume = 0 };

        Action act = options.Validate;

        act.Should().Throw<BareWireConfigurationException>().WithMessage("*MaxBufferedBytesPerConsume*");
        OutboxOptions.Default.MaxBufferedBytesPerConsume.Should().Be(67_108_864);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IsCapturing_ReflectsAmbientBuffer_IncludingSealedOne(bool useAmbientTransaction)
    {
        var (middleware, _) = CreateMiddleware(useAmbientTransaction);
        TransactionalOutboxInterceptor sut = CreateInterceptor();
        bool during = false;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? afterSeal = null;

        sut.IsCapturing.Should().BeFalse();
        await middleware.InvokeAsync(CreateContext(), _ =>
        {
            during = sut.IsCapturing;

            // Captures the ambient buffer, which is sealed by the time the task resumes.
            afterSeal = Task.Run(async () =>
            {
                await release.Task;
                return sut.IsCapturing;
            });
            return Task.CompletedTask;
        });

        release.SetResult();

        during.Should().BeTrue();
        (await afterSeal!).Should().BeTrue("a sealed buffer is still ambient so the bus reaches TryIntercept and the Warning");
        sut.IsCapturing.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvokeAsync_WhenInnerMiddlewareOverwritesRetryCallback_LogsWarning(bool useAmbientTransaction)
    {
        RecordingLogger<TransactionalOutboxMiddleware> logger = new();
        var (middleware, _) = CreateMiddleware(useAmbientTransaction, logger: logger);

        await middleware.InvokeAsync(CreateContext(), ctx =>
        {
            ctx.Items["retry:attempt-starting"] = (Action)(() => { });
            return Task.CompletedTask;
        });

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains("retry-attempt callback"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvokeAsync_WhenRetryCallbackUntouched_DoesNotLogWarning(bool useAmbientTransaction)
    {
        RecordingLogger<TransactionalOutboxMiddleware> logger = new();
        var (middleware, _) = CreateMiddleware(useAmbientTransaction, logger: logger);

        await middleware.InvokeAsync(CreateContext(), _ => Task.CompletedTask);

        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void OutboxOptions_Validate_WhenMaxBufferedMessagesNotPositive_Throws()
    {
        OutboxOptions options = new() { MaxBufferedMessagesPerConsume = 0 };

        Action act = options.Validate;

        act.Should().Throw<BareWireConfigurationException>().WithMessage("*MaxBufferedMessagesPerConsume*");
        OutboxOptions.Default.MaxBufferedMessagesPerConsume.Should().Be(10_000);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvokeAsync_WithRealRetryMiddlewareInside_SavesOnlyTheSuccessfulAttemptsMessages(bool useAmbientTransaction)
    {
        var (middleware, store) = CreateMiddleware(useAmbientTransaction);
        TransactionalOutboxInterceptor sut = CreateInterceptor();
        RetryMiddleware retry = new(
            new IntervalRetryPolicy(maxRetries: 3, interval: TimeSpan.Zero, handledExceptions: [], ignoredExceptions: []),
            NullLogger<RetryMiddleware>.Instance,
            Substitute.For<IBareWireInstrumentation>(),
            "TestEvent");
        int attempt = 0;
        OutboundMessage firstAttempt = CreateMessage("attempt-1");
        OutboundMessage secondAttempt = CreateMessage("attempt-2");

        // Chain order mirrors production: outbox middleware outermost, retry inside it.
        await middleware.InvokeAsync(CreateContext(), ctx => retry.InvokeAsync(ctx, _ =>
        {
            attempt++;
            if (attempt == 1)
            {
                sut.TryIntercept(firstAttempt);
                throw new InvalidOperationException("transient");
            }

            sut.TryIntercept(secondAttempt);
            return Task.CompletedTask;
        }));

        attempt.Should().Be(2);
        await store.Received(1).SaveMessagesAsync(
            Arg.Is<IReadOnlyList<OutboundMessage>>(l => l.Count == 1 && ReferenceEquals(l[0], secondAttempt)),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvokeAsync_AfterCompletion_RetryCallbackIsNoLongerRegistered(bool useAmbientTransaction)
    {
        var (middleware, _) = CreateMiddleware(useAmbientTransaction);
        MessageContext context = CreateContext();

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        context.Items.ContainsKey(WellKnownItemKeys.RetryAttemptStarting).Should().BeFalse(
            "the retry hook must not leak past the middleware");
    }

    // ── Real bus + outbox ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishAsync_ViaBusInsideOutboxMiddleware_IsNotSentToTransportAndIsSavedToOutbox(bool useAmbientTransaction)
    {
        var (middleware, store) = CreateMiddleware(useAmbientTransaction);
        var (bus, adapter) = CreateBus(CreateInterceptor());

        await middleware.InvokeAsync(CreateContext(), _ =>
            bus.PublishAsync(new TestEvent("1"), TestContext.Current.CancellationToken));
        await Task.Delay(150, TestContext.Current.CancellationToken);

        await adapter.DidNotReceiveWithAnyArgs().SendBatchAsync(default!, default);
        await store.Received(1).SaveMessagesAsync(
            Arg.Is<IReadOnlyList<OutboundMessage>>(l => l.Count == 1 && l[0].Headers["BW-MessageType"] == "TestEvent"),
            Arg.Any<CancellationToken>());

        await bus.DisposeAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishAsync_ViaBusWhenHandlerPublishesThenThrows_ReachesNeitherTransportNorOutbox(bool useAmbientTransaction)
    {
        var (middleware, store) = CreateMiddleware(useAmbientTransaction);
        var (bus, adapter) = CreateBus(CreateInterceptor());

        Func<Task> act = () => middleware.InvokeAsync(CreateContext(), async _ =>
        {
            await bus.PublishAsync(new TestEvent("1"), TestContext.Current.CancellationToken);
            throw new InvalidOperationException("boom");
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
        await Task.Delay(150, TestContext.Current.CancellationToken);

        await adapter.DidNotReceiveWithAnyArgs().SendBatchAsync(default!, default);
        await store.DidNotReceiveWithAnyArgs().SaveMessagesAsync(default!, default);

        await bus.DisposeAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RespondAsync_InsideOutboxMiddleware_IsBufferedWithCorrelationIdAndNotSentDirectly(bool useAmbientTransaction)
    {
        var (middleware, store) = CreateMiddleware(useAmbientTransaction);
        var (bus, adapter) = CreateBus(CreateInterceptor());
        ConsumeContext<TestEvent> consumeContext = new(
            new TestEvent("request"),
            Guid.NewGuid(),
            correlationId: null,
            conversationId: null,
            sourceAddress: null,
            destinationAddress: null,
            sentTime: null,
            headers: new Dictionary<string, string>
            {
                ["ReplyTo"] = "reply-queue",
                ["correlation-id"] = "corr-42",
            },
            contentType: "application/json",
            rawBody: default,
            publishEndpoint: bus,
            sendEndpointProvider: bus);

        await middleware.InvokeAsync(CreateContext(), _ =>
            consumeContext.RespondAsync(new TestEvent("response"), TestContext.Current.CancellationToken));
        await Task.Delay(150, TestContext.Current.CancellationToken);

        await adapter.DidNotReceiveWithAnyArgs().SendBatchAsync(default!, default);
        await store.Received(1).SaveMessagesAsync(
            Arg.Is<IReadOnlyList<OutboundMessage>>(l =>
                l.Count == 1
                && l[0].RoutingKey == "reply-queue"
                && l[0].Headers["correlation-id"] == "corr-42"),
            Arg.Any<CancellationToken>());

        await bus.DisposeAsync();
    }

    // ── Registration ──────────────────────────────────────────────────────────

    private sealed class UserInterceptor : IOutboundMessageInterceptor
    {
        public bool IsCapturing => false;

        public bool TryIntercept(OutboundMessage message) => false;
    }

    private static IOutboundMessageInterceptor? ResolveInterceptor(Action<IServiceCollection>? preRegister = null)
    {
        ServiceCollection services = new();
        services.AddLogging();
        preRegister?.Invoke(services);
        services.AddBareWireOutbox(o => o.UseSqlite("DataSource=:memory:"), c => c.AllowNonAtomicProvider = true);
        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetService<IOutboundMessageInterceptor>();
    }

    [Fact]
    public void AddBareWireOutbox_RegistersTransactionalOutboxInterceptor()
        => ResolveInterceptor().Should().BeOfType<TransactionalOutboxInterceptor>();

    [Fact]
    public void AddBareWireOutbox_WhenUserRegisteredInterceptorEarlier_ReplacesItWithOutboxInterceptor()
        => ResolveInterceptor(s => s.AddSingleton<IOutboundMessageInterceptor, UserInterceptor>())
            .Should().BeOfType<TransactionalOutboxInterceptor>();
}
