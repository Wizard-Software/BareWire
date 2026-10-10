using System.Diagnostics;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.Google.PubSub;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Transport.PubSub;

/// <summary>
/// Guards the consumer shutdown contract of <see cref="PubSubTransportAdapter"/>: once the consume token is
/// cancelled no buffered message is handed out, and every message that was received but never given to the
/// caller is returned to the broker (ack deadline 0) and evicted from the in-flight registry.
/// </summary>
public sealed class PubSubTransportAdapterShutdownTests
{
    private const string ProjectId = "test-project";
    private const string SubscriptionId = "shutdown-sub";
    private const string SecretAckPrefix = "SECRET-ACK-";
    private const string SecretExceptionText = "SECRET-EXCEPTION-TEXT";

    private static readonly TimeSpan _guard = TimeSpan.FromSeconds(10);

    private static int _messageSeed;

    private static PubSubTransportOptions Options() => new()
    {
        AuthMode = PubSubAuthMode.ApplicationDefault,
        ProjectId = ProjectId,
        MaxInFlightMessages = 5_000,
    };

    private static FlowControlOptions Flow() => new() { InternalQueueCapacity = 3_000 };

    private static PullResponse Batch(int count)
    {
        var response = new PullResponse();
        for (int i = 0; i < count; i++)
        {
            int n = Interlocked.Increment(ref _messageSeed);
            response.ReceivedMessages.Add(new ReceivedMessage
            {
                AckId = $"{SecretAckPrefix}{n}",
                Message = new PubsubMessage { MessageId = $"msg-{n}", Data = ByteString.CopyFromUtf8("{\"v\":1}") },
            });
        }

        return response;
    }

    private static async Task<T> BlockUntilCancelledAsync<T>(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return default!;
    }

    private static (PubSubTransportAdapter Adapter, SubscriberServiceApiClient Subscriber, TaskCompletionSource Idle)
        CreateAdapter(CapturingLogger logger, params int[] batchSizes)
    {
        var publisher = Substitute.For<PublisherServiceApiClient>();
        var subscriber = Substitute.For<SubscriberServiceApiClient>();
        TaskCompletionSource idle = ScriptPull(subscriber, batchSizes);
        var adapter = new PubSubTransportAdapter(Options(), logger, publisher, subscriber);
        return (adapter, subscriber, idle);
    }

    // Serves the scripted batches, then signals "idle" (everything is buffered) and blocks until cancelled.
    private static TaskCompletionSource ScriptPull(SubscriberServiceApiClient subscriber, params int[] batchSizes)
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int call = 0;
        subscriber.PullAsync(Arg.Any<SubscriptionName>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                int index = Interlocked.Increment(ref call) - 1;
                if (index < batchSizes.Length)
                {
                    return Task.FromResult(Batch(batchSizes[index]));
                }

                idle.TrySetResult();
                return BlockUntilCancelledAsync<PullResponse>(ci.Arg<CancellationToken>());
            });
        return idle;
    }

    private static async Task<IAsyncEnumerator<InboundMessage>> ReadFirstAsync(
        PubSubTransportAdapter adapter, TaskCompletionSource idle, CancellationToken token)
    {
        IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(SubscriptionId, Flow(), token).GetAsyncEnumerator(token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        await idle.Task.WaitAsync(_guard, TestContext.Current.CancellationToken);
        return enumerator;
    }

    // Records the size of every ModifyAckDeadline request (and asserts the deadline is zero).
    private static List<int> CaptureChunkSizes(SubscriberServiceApiClient subscriber)
    {
        var sizes = new List<int>();
        subscriber.ModifyAckDeadlineAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                lock (sizes)
                {
                    sizes.Add(ci.Arg<IEnumerable<string>>().Count());
                }

                ci.ArgAt<string>(0).Should().Be($"projects/{ProjectId}/subscriptions/{SubscriptionId}");
                ci.ArgAt<int>(2).Should().Be(0);
                return Task.CompletedTask;
            });
        return sizes;
    }

    // ── Read side ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConsumeAsync_TokenCancelledWithBufferedMessages_YieldsNoFurtherMessage()
    {
        var logger = new CapturingLogger();
        var (adapter, subscriber, idle) = CreateAdapter(logger, 5);
        CaptureChunkSizes(subscriber);
        using var cts = new CancellationTokenSource();

        IAsyncEnumerator<InboundMessage> enumerator = await ReadFirstAsync(adapter, idle, cts.Token);
        await cts.CancelAsync();

        await enumerator.Awaiting(e => e.MoveNextAsync().AsTask())
            .Should().ThrowAsync<OperationCanceledException>();

        await enumerator.DisposeAsync().AsTask().WaitAsync(_guard, TestContext.Current.CancellationToken);
    }

    // ── Drain ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConsumeAsync_StoppedWithBufferedMessages_ModifiesAckDeadlineToZeroAndEvictsRegistry()
    {
        var logger = new CapturingLogger();
        var (adapter, subscriber, idle) = CreateAdapter(logger, 5);
        List<int> sizes = CaptureChunkSizes(subscriber);
        using var cts = new CancellationTokenSource();

        IAsyncEnumerator<InboundMessage> enumerator = await ReadFirstAsync(adapter, idle, cts.Token);
        adapter.InFlightRegistry.Count.Should().Be(5);

        await cts.CancelAsync();
        await enumerator.DisposeAsync().AsTask().WaitAsync(_guard, TestContext.Current.CancellationToken);

        sizes.Should().Equal(4);
        adapter.InFlightRegistry.Count.Should().Be(1,
            "only the message handed to the caller stays registered until the caller settles it");
    }

    [Fact]
    public async Task ConsumeAsync_StoppedWithManyBufferedMessages_ReleasesInChunksOfAtMostThousand()
    {
        var logger = new CapturingLogger();
        var (adapter, subscriber, idle) = CreateAdapter(logger, 1000, 1000, 5);
        List<int> sizes = CaptureChunkSizes(subscriber);
        using var cts = new CancellationTokenSource();

        IAsyncEnumerator<InboundMessage> enumerator = await ReadFirstAsync(adapter, idle, cts.Token);
        await cts.CancelAsync();
        await enumerator.DisposeAsync().AsTask().WaitAsync(_guard, TestContext.Current.CancellationToken);

        sizes.Should().Equal(1000, 1000, 4);
        adapter.InFlightRegistry.Count.Should().Be(1);
    }

    [Fact]
    public async Task ConsumeAsync_ReleaseFailsDuringDrain_DisposesRemainingAndDoesNotThrow()
    {
        var logger = new CapturingLogger();
        var (adapter, subscriber, idle) = CreateAdapter(logger, 1000, 1000);
        subscriber.ModifyAckDeadlineAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new RpcException(new Status(StatusCode.PermissionDenied, SecretExceptionText)));
        using var cts = new CancellationTokenSource();

        IAsyncEnumerator<InboundMessage> enumerator = await ReadFirstAsync(adapter, idle, cts.Token);
        await cts.CancelAsync();
        await enumerator.DisposeAsync().AsTask().WaitAsync(_guard, TestContext.Current.CancellationToken);

        await subscriber.Received(1).ModifyAckDeadlineAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        adapter.InFlightRegistry.Count.Should().Be(1, "the registry is cleaned even in dispose-only mode");
        logger.Warnings.Should().ContainSingle();
        logger.Warnings[0].Should().Contain("RpcException").And.Contain("PermissionDenied");
        logger.AllText.Should().NotContain(SecretAckPrefix).And.NotContain(SecretExceptionText);
    }

    [Fact]
    public async Task ConsumeAsync_SecondChunkFails_FirstChunkWasReleasedAndRestIsDisposedOnly()
    {
        var logger = new CapturingLogger();
        var (adapter, subscriber, idle) = CreateAdapter(logger, 1000, 1000, 500);
        int calls = 0;
        subscriber.ModifyAckDeadlineAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => Interlocked.Increment(ref calls) == 1
                ? Task.CompletedTask
                : Task.FromException(new RpcException(new Status(StatusCode.Unavailable, SecretExceptionText))));
        using var cts = new CancellationTokenSource();

        IAsyncEnumerator<InboundMessage> enumerator = await ReadFirstAsync(adapter, idle, cts.Token);
        await cts.CancelAsync();
        await enumerator.DisposeAsync().AsTask().WaitAsync(_guard, TestContext.Current.CancellationToken);

        calls.Should().Be(2, "after the first failure no further broker call is made");
        adapter.InFlightRegistry.Count.Should().Be(1);
        logger.Warnings.Should().ContainSingle().Which.Should().Contain("Unavailable");
        logger.AllText.Should().NotContain(SecretAckPrefix).And.NotContain(SecretExceptionText);
    }

    [Fact]
    public async Task ConsumeAsync_SlowBroker_DrainFinishesWithinSharedBudget()
    {
        var logger = new CapturingLogger();
        var (adapter, subscriber, idle) = CreateAdapter(logger, 1000, 1000);
        subscriber.ModifyAckDeadlineAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => BlockUntilCancelledAsync<bool>(ci.Arg<CancellationToken>()));
        adapter.ShutdownDrainBudget = TimeSpan.FromMilliseconds(300);
        using var cts = new CancellationTokenSource();

        IAsyncEnumerator<InboundMessage> enumerator = await ReadFirstAsync(adapter, idle, cts.Token);
        await cts.CancelAsync();

        var stopwatch = Stopwatch.StartNew();
        await enumerator.DisposeAsync().AsTask().WaitAsync(_guard, TestContext.Current.CancellationToken);
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3), "the whole drain shares one budget");
        await subscriber.Received(1).ModifyAckDeadlineAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        adapter.InFlightRegistry.Count.Should().Be(1);
        logger.Warnings.Should().ContainSingle().Which.Should().Contain("budget");
    }

    [Fact]
    public async Task ConsumeAsync_RepeatedStartAndStop_RegistryHoldsOnlyTrulyInFlightMessages()
    {
        var logger = new CapturingLogger();
        var (adapter, subscriber, _) = CreateAdapter(logger);
        CaptureChunkSizes(subscriber);

        for (int cycle = 1; cycle <= 3; cycle++)
        {
            TaskCompletionSource idle = ScriptPull(subscriber, 5);
            using var cts = new CancellationTokenSource();

            IAsyncEnumerator<InboundMessage> enumerator = await ReadFirstAsync(adapter, idle, cts.Token);
            await cts.CancelAsync();
            await enumerator.DisposeAsync().AsTask().WaitAsync(_guard, TestContext.Current.CancellationToken);

            adapter.InFlightRegistry.Count.Should().Be(cycle,
                "each stopped consumer leaves only the one message it handed to the caller");
        }
    }

    // ── Polling loop ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ConsumeAsync_ResponseArrivesAfterCancellation_ReleasesUnprocessedMessages()
    {
        var logger = new CapturingLogger();
        var publisher = Substitute.For<PublisherServiceApiClient>();
        var subscriber = Substitute.For<SubscriberServiceApiClient>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var polled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.PullAsync(Arg.Any<SubscriptionName>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                polled.TrySetResult();
                await gate.Task; // ignores the token: the response arrives after cancellation
                return Batch(3);
            });
        List<int> sizes = CaptureChunkSizes(subscriber);
        var adapter = new PubSubTransportAdapter(Options(), logger, publisher, subscriber);
        using var cts = new CancellationTokenSource();

        IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(SubscriptionId, Flow(), cts.Token).GetAsyncEnumerator(cts.Token);
        ValueTask<bool> pending = enumerator.MoveNextAsync();
        await polled.Task.WaitAsync(_guard, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        gate.SetResult();
        await pending.AsTask().Awaiting(t => t).Should().ThrowAsync<OperationCanceledException>();
        await enumerator.DisposeAsync().AsTask().WaitAsync(_guard, TestContext.Current.CancellationToken);

        sizes.Should().Equal(3);
        adapter.InFlightRegistry.Count.Should().Be(0, "messages received after cancellation are never registered");
    }

    [Fact]
    public async Task ConsumeAsync_EnumeratorDisposedWithoutCancellingToken_CompletesPollingTask()
    {
        var logger = new CapturingLogger();
        var (adapter, subscriber, idle) = CreateAdapter(logger, 1);
        CaptureChunkSizes(subscriber);
        using var cts = new CancellationTokenSource();

        IAsyncEnumerator<InboundMessage> enumerator = await ReadFirstAsync(adapter, idle, cts.Token);

        // The consume token is never cancelled: disposal must stop the polling loop on its own.
        await enumerator.DisposeAsync().AsTask().WaitAsync(_guard, TestContext.Current.CancellationToken);

        cts.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task ConsumeAsync_PollingFails_LogsExceptionTypeAndStatusCodeButNotMessage()
    {
        var logger = new CapturingLogger();
        var publisher = Substitute.For<PublisherServiceApiClient>();
        var subscriber = Substitute.For<SubscriberServiceApiClient>();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.PullAsync(Arg.Any<SubscriptionName>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                failed.TrySetResult();
                return Task.FromException<PullResponse>(
                    new RpcException(new Status(StatusCode.Unavailable, SecretExceptionText)));
            });
        var adapter = new PubSubTransportAdapter(Options(), logger, publisher, subscriber);
        using var cts = new CancellationTokenSource();

        IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(SubscriptionId, Flow(), cts.Token).GetAsyncEnumerator(cts.Token);
        ValueTask<bool> pending = enumerator.MoveNextAsync();
        await failed.Task.WaitAsync(_guard, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await pending.AsTask().Awaiting(t => t).Should().ThrowAsync<OperationCanceledException>();
        await enumerator.DisposeAsync().AsTask().WaitAsync(_guard, TestContext.Current.CancellationToken);

        logger.Warnings.Should().NotBeEmpty();
        logger.Warnings[0].Should().Contain("RpcException").And.Contain("Unavailable");
        logger.AllText.Should().NotContain(SecretExceptionText);
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class CapturingLogger : ILogger<PubSubTransportAdapter>
    {
        private readonly List<(LogLevel Level, string Text)> _entries = [];

        public List<string> Warnings
        {
            get
            {
                lock (_entries)
                {
                    return _entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Text).ToList();
                }
            }
        }

        public string AllText
        {
            get
            {
                lock (_entries)
                {
                    return string.Join('\n', _entries.Select(e => e.Text));
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception) + (exception?.ToString() ?? string.Empty)));
            }
        }
    }
}
