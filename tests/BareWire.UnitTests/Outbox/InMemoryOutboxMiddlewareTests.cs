// NSubstitute's Returns() for ValueTask-returning mocks triggers CA2012 as a false positive.
// The ValueTask is consumed internally by NSubstitute and never double-consumed.
#pragma warning disable CA2012

using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Observability;
using BareWire.Abstractions.Pipeline;
using BareWire.Abstractions.Transport;
using BareWire.Outbox;
using BareWire.Pipeline;
using BareWire.Pipeline.Retry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Outbox;

public sealed class InMemoryOutboxMiddlewareTests
{
    private readonly IOutboxStore _outboxStore;
    private readonly ILogger<InMemoryOutboxMiddleware> _logger;
    private readonly InMemoryOutboxMiddleware _sut;

    public InMemoryOutboxMiddlewareTests()
    {
        _outboxStore = Substitute.For<IOutboxStore>();
        _logger = Substitute.For<ILogger<InMemoryOutboxMiddleware>>();
        _sut = new InMemoryOutboxMiddleware(_outboxStore, _logger);
    }

    private static MessageContext CreateContext(CancellationToken cancellationToken = default)
    {
        IServiceProvider serviceProvider = Substitute.For<IServiceProvider>();
        return new MessageContext(
            messageId: Guid.NewGuid(),
            headers: new Dictionary<string, string>(),
            rawBody: ReadOnlySequence<byte>.Empty,
            serviceProvider: serviceProvider,
            cancellationToken: cancellationToken);
    }

    private static OutboundMessage CreateOutboundMessage(string routingKey = "test.routing.key") =>
        new(
            routingKey: routingKey,
            headers: new Dictionary<string, string>(),
            body: ReadOnlyMemory<byte>.Empty,
            contentType: "application/json");

    [Fact]
    public async Task InvokeAsync_HandlerSuccess_FlushesBufferedMessages()
    {
        // Arrange
        var context = CreateContext();
        IReadOnlyList<OutboundMessage>? capturedMessages = null;

        _outboxStore
            .SaveMessagesAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedMessages = call.Arg<IReadOnlyList<OutboundMessage>>();
                return ValueTask.CompletedTask;
            });

        NextMiddleware next = _ =>
        {
            var buffer = InMemoryOutboxMiddleware.Current;
            buffer!.Add(CreateOutboundMessage("key.1"));
            buffer.Add(CreateOutboundMessage("key.2"));
            buffer.Add(CreateOutboundMessage("key.3"));
            return Task.CompletedTask;
        };

        // Act
        await _sut.InvokeAsync(context, next);

        // Assert
        await _outboxStore.Received(1).SaveMessagesAsync(
            Arg.Any<IReadOnlyList<OutboundMessage>>(),
            Arg.Any<CancellationToken>());

        capturedMessages.Should().NotBeNull();
        capturedMessages!.Count.Should().Be(3);
        capturedMessages[0].RoutingKey.Should().Be("key.1");
        capturedMessages[1].RoutingKey.Should().Be("key.2");
        capturedMessages[2].RoutingKey.Should().Be("key.3");
    }

    [Fact]
    public async Task InvokeAsync_HandlerThrows_DiscardsBuffer()
    {
        // Arrange
        var context = CreateContext();

        NextMiddleware next = _ =>
        {
            var buffer = InMemoryOutboxMiddleware.Current;
            buffer!.Add(CreateOutboundMessage());
            throw new InvalidOperationException("handler failure");
        };

        // Act
        Func<Task> act = () => _sut.InvokeAsync(context, next);

        // Assert
        await act.Should().ThrowExactlyAsync<InvalidOperationException>()
            .WithMessage("handler failure");

        await _outboxStore.DidNotReceive().SaveMessagesAsync(
            Arg.Any<IReadOnlyList<OutboundMessage>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_NoPublish_NoFlush()
    {
        // Arrange
        var context = CreateContext();

        NextMiddleware next = _ => Task.CompletedTask;

        // Act
        await _sut.InvokeAsync(context, next);

        // Assert
        await _outboxStore.DidNotReceive().SaveMessagesAsync(
            Arg.Any<IReadOnlyList<OutboundMessage>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_NestedPublish_AllBuffered()
    {
        // Arrange
        var context = CreateContext();
        IReadOnlyList<OutboundMessage>? capturedMessages = null;

        _outboxStore
            .SaveMessagesAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedMessages = call.Arg<IReadOnlyList<OutboundMessage>>();
                return ValueTask.CompletedTask;
            });

        NextMiddleware next = async _ =>
        {
            var buffer = InMemoryOutboxMiddleware.Current;
            buffer!.Add(CreateOutboundMessage("outer.1"));

            // Simulate nested async call that also publishes to same buffer
            await Task.Yield();
            var bufferFromNested = InMemoryOutboxMiddleware.Current;
            bufferFromNested!.Add(CreateOutboundMessage("nested.1"));
            bufferFromNested.Add(CreateOutboundMessage("nested.2"));
        };

        // Act
        await _sut.InvokeAsync(context, next);

        // Assert
        await _outboxStore.Received(1).SaveMessagesAsync(
            Arg.Any<IReadOnlyList<OutboundMessage>>(),
            Arg.Any<CancellationToken>());

        capturedMessages.Should().NotBeNull();
        capturedMessages!.Count.Should().Be(3);
    }

    [Fact]
    public async Task InvokeAsync_CancellationRequested_PropagatesWithoutFlush()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var context = CreateContext(cts.Token);

        NextMiddleware next = _ => Task.FromCanceled(cts.Token);

        // Act
        Func<Task> act = () => _sut.InvokeAsync(context, next);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();

        await _outboxStore.DidNotReceive().SaveMessagesAsync(
            Arg.Any<IReadOnlyList<OutboundMessage>>(),
            Arg.Any<CancellationToken>());
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Events.Add((logLevel, formatter(state, exception)));
    }

    private static RetryMiddleware CreateRetryMiddleware(int retryCount = 1) =>
        new(
            new IntervalRetryPolicy(retryCount, TimeSpan.Zero, [], []),
            NullLogger<RetryMiddleware>.Instance,
            Substitute.For<IBareWireInstrumentation>(),
            "TestMessage");

    [Fact]
    public async Task InvokeAsync_RetryAfterFailedAttempt_FlushesOnlySuccessfulAttemptMessages()
    {
        // Arrange
        var retry = CreateRetryMiddleware(retryCount: 1);
        var context = CreateContext();
        int attempt = 0;
        List<OutboundMessage>? saved = null;
        _outboxStore
            .SaveMessagesAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                saved = [.. call.Arg<IReadOnlyList<OutboundMessage>>()];
                return ValueTask.CompletedTask;
            });

        // Act
        await _sut.InvokeAsync(context, ctx => retry.InvokeAsync(ctx, _ =>
        {
            attempt++;
            InMemoryOutboxMiddleware.Current!.Add(CreateOutboundMessage($"attempt.{attempt}"));
            return attempt == 1 ? throw new InvalidOperationException("first attempt fails") : Task.CompletedTask;
        }));

        // Assert
        attempt.Should().Be(2);
        saved.Should().ContainSingle().Which.RoutingKey.Should().Be("attempt.2");
    }

    [Fact]
    public async Task InvokeAsync_DuringHandler_RegistersRetryCallbackAndRemovesItAfterwards()
    {
        // Arrange
        var context = CreateContext();
        object? slotDuringHandler = null;

        // Act
        await _sut.InvokeAsync(context, ctx =>
        {
            ctx.Items.TryGetValue(WellKnownItemKeys.RetryAttemptStarting, out slotDuringHandler);
            return Task.CompletedTask;
        });

        // Assert
        slotDuringHandler.Should().BeOfType<Action>();
        context.Items.ContainsKey(WellKnownItemKeys.RetryAttemptStarting).Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_RetryCallbackOverwrittenByInnerMiddleware_LogsWarningAndRemovesSlot()
    {
        // Arrange
        var logger = new CapturingLogger<InMemoryOutboxMiddleware>();
        var sut = new InMemoryOutboxMiddleware(_outboxStore, logger);
        var context = CreateContext();

        // Act
        await sut.InvokeAsync(context, ctx =>
        {
            ctx.Items[WellKnownItemKeys.RetryAttemptStarting] = (Action)(() => { });
            return Task.CompletedTask;
        });

        // Assert
        logger.Events.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains("overwritten"));
        context.Items.ContainsKey(WellKnownItemKeys.RetryAttemptStarting).Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_RetriesExhausted_PropagatesWithoutFlushAndRemovesSlot()
    {
        // Arrange
        var retry = CreateRetryMiddleware(retryCount: 1);
        var context = CreateContext();
        int attempts = 0;

        // Act
        Func<Task> act = () => _sut.InvokeAsync(context, ctx => retry.InvokeAsync(ctx, _ =>
        {
            attempts++;
            InMemoryOutboxMiddleware.Current!.Add(CreateOutboundMessage($"attempt.{attempts}"));
            throw new InvalidOperationException("always fails");
        }));

        // Assert
        await act.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage("always fails");
        attempts.Should().Be(2);
        await _outboxStore.DidNotReceive().SaveMessagesAsync(
            Arg.Any<IReadOnlyList<OutboundMessage>>(),
            Arg.Any<CancellationToken>());
        context.Items.ContainsKey(WellKnownItemKeys.RetryAttemptStarting).Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_BufferLimitExceeded_ThrowsBareWireExceptionAndSavesNothing()
    {
        // Arrange
        var sut = new InMemoryOutboxMiddleware(
            _outboxStore,
            _logger,
            new OutboxOptions { MaxBufferedMessagesPerConsume = 2 });
        var context = CreateContext();

        // Act
        Func<Task> act = () => sut.InvokeAsync(context, _ =>
        {
            var buffer = InMemoryOutboxMiddleware.Current!;
            buffer.Add(CreateOutboundMessage("key.1"));
            buffer.Add(CreateOutboundMessage("key.2"));
            buffer.Add(CreateOutboundMessage("key.3"));
            return Task.CompletedTask;
        });

        // Assert
        await act.Should().ThrowExactlyAsync<BareWireException>();
        await _outboxStore.DidNotReceive().SaveMessagesAsync(
            Arg.Any<IReadOnlyList<OutboundMessage>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_MessageAddedAfterSeal_DoesNotReachStore()
    {
        // Arrange
        var context = CreateContext();
        OutboxBuffer? captured = null;
        bool addedDuringFlush = true;
        List<OutboundMessage>? saved = null;
        _outboxStore
            .SaveMessagesAsync(Arg.Any<IReadOnlyList<OutboundMessage>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                saved = [.. call.Arg<IReadOnlyList<OutboundMessage>>()];
                addedDuringFlush = captured!.TryAdd(CreateOutboundMessage("late.during-flush"));
                return ValueTask.CompletedTask;
            });

        // Act
        await _sut.InvokeAsync(context, _ =>
        {
            captured = InMemoryOutboxMiddleware.Current;
            captured!.Add(CreateOutboundMessage("key.1"));
            return Task.CompletedTask;
        });
        bool addedAfterInvoke = captured!.TryAdd(CreateOutboundMessage("late.after-invoke"));

        // Assert
        addedDuringFlush.Should().BeFalse();
        addedAfterInvoke.Should().BeFalse();
        saved.Should().ContainSingle().Which.RoutingKey.Should().Be("key.1");
        await _outboxStore.Received(1).SaveMessagesAsync(
            Arg.Any<IReadOnlyList<OutboundMessage>>(),
            Arg.Any<CancellationToken>());
    }
}
