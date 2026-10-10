using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Abstractions.Transport;
using BareWire.Transport.AWS.SQS;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Transport.Sqs;

/// <summary>
/// Guards the consumer shutdown contract of <see cref="SqsTransportAdapter"/>: once the consume token is
/// cancelled no buffered message is handed out, and every message that was received but never given to the
/// caller is returned to the broker (visibility timeout 0) and evicted from the in-flight registry.
/// </summary>
public sealed class SqsTransportAdapterShutdownTests
{
    private const string QueueName = "shutdown-queue";
    private const string QueueUrl = "https://sqs.eu-central-1.amazonaws.com/123/shutdown-queue";
    private const string SecretHandlePrefix = "SECRET-RECEIPT-";
    private const string SecretExceptionText = "SECRET-EXCEPTION-TEXT";

    private static readonly TimeSpan _guard = TimeSpan.FromSeconds(10);

    private static SqsTransportOptions Options() => new()
    {
        AuthMode = SqsAuthMode.DefaultChain,
        WaitTimeSeconds = 0,
    };

    private static FlowControlOptions Flow() => new() { InternalQueueCapacity = 100 };

    private static int _messageSeed;

    private static ReceiveMessageResponse Batch(int count)
    {
        var messages = new List<Message>(count);
        for (int i = 0; i < count; i++)
        {
            int n = Interlocked.Increment(ref _messageSeed);
            messages.Add(new Message
            {
                MessageId = $"msg-{n}",
                ReceiptHandle = $"{SecretHandlePrefix}{n}",
                Body = "{\"v\":1}",
                MessageAttributes = [],
            });
        }

        return new ReceiveMessageResponse { Messages = messages };
    }

    private static async Task<T> BlockUntilCancelledAsync<T>(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return default!;
    }

    private static IAmazonSQS CreateClient(out TaskCompletionSource idle, params int[] batchSizes)
    {
        var client = Substitute.For<IAmazonSQS>();
        client.GetQueueUrlAsync(QueueName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetQueueUrlResponse { QueueUrl = QueueUrl }));
        idle = ScriptReceive(client, batchSizes);
        client.ChangeMessageVisibilityBatchAsync(
                Arg.Any<ChangeMessageVisibilityBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new ChangeMessageVisibilityBatchResponse()));
        return client;
    }

    // Serves the scripted batches, then signals "idle" (everything is buffered) and long-polls until cancelled.
    private static TaskCompletionSource ScriptReceive(IAmazonSQS client, params int[] batchSizes)
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int call = 0;
        client.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                int index = Interlocked.Increment(ref call) - 1;
                if (index < batchSizes.Length)
                {
                    return Task.FromResult(Batch(batchSizes[index]));
                }

                idle.TrySetResult();
                return BlockUntilCancelledAsync<ReceiveMessageResponse>(ci.Arg<CancellationToken>());
            });
        return idle;
    }

    private static async Task<IAsyncEnumerator<InboundMessage>> ReadFirstAsync(
        SqsTransportAdapter adapter, TaskCompletionSource idle, CancellationToken token)
    {
        IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(QueueName, Flow(), token).GetAsyncEnumerator(token);
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        await idle.Task.WaitAsync(_guard, TestContext.Current.CancellationToken);
        return enumerator;
    }

    private static List<int> CaptureBatchSizes(IAmazonSQS client)
    {
        var sizes = new List<int>();
        client.ChangeMessageVisibilityBatchAsync(
                Arg.Any<ChangeMessageVisibilityBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var request = ci.Arg<ChangeMessageVisibilityBatchRequest>();
                lock (sizes)
                {
                    sizes.Add(request.Entries.Count);
                }

                request.QueueUrl.Should().Be(QueueUrl);
                request.Entries.Should().OnlyContain(e => e.VisibilityTimeout == 0);
                return Task.FromResult(new ChangeMessageVisibilityBatchResponse());
            });
        return sizes;
    }

    // ── Read side ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConsumeAsync_TokenCancelledWithBufferedMessages_YieldsNoFurtherMessage()
    {
        IAmazonSQS client = CreateClient(out TaskCompletionSource idle, 5);
        var adapter = new SqsTransportAdapter(Options(), new CapturingLogger(), client);
        using var cts = new CancellationTokenSource();

        IAsyncEnumerator<InboundMessage> enumerator = await ReadFirstAsync(adapter, idle, cts.Token);
        await cts.CancelAsync();

        await enumerator.Awaiting(e => e.MoveNextAsync().AsTask())
            .Should().ThrowAsync<OperationCanceledException>();

        await enumerator.DisposeAsync().AsTask().WaitAsync(_guard, TestContext.Current.CancellationToken);
    }

    // ── Drain ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConsumeAsync_StoppedWithBufferedMessages_ReleasesVisibilityAndEvictsRegistry()
    {
        IAmazonSQS client = CreateClient(out TaskCompletionSource idle, 5);
        List<int> sizes = CaptureBatchSizes(client);
        var adapter = new SqsTransportAdapter(Options(), new CapturingLogger(), client);
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
    public async Task ConsumeAsync_StoppedWithManyBufferedMessages_ReleasesInBatchesOfTen()
    {
        IAmazonSQS client = CreateClient(out TaskCompletionSource idle, 10, 10);
        List<int> sizes = CaptureBatchSizes(client);
        var adapter = new SqsTransportAdapter(Options(), new CapturingLogger(), client);
        using var cts = new CancellationTokenSource();

        IAsyncEnumerator<InboundMessage> enumerator = await ReadFirstAsync(adapter, idle, cts.Token);
        await cts.CancelAsync();
        await enumerator.DisposeAsync().AsTask().WaitAsync(_guard, TestContext.Current.CancellationToken);

        sizes.Should().Equal(10, 9);
        adapter.InFlightRegistry.Count.Should().Be(1);
    }

    [Fact]
    public async Task ConsumeAsync_ReleaseFailsDuringDrain_DisposesRemainingAndDoesNotThrow()
    {
        IAmazonSQS client = CreateClient(out TaskCompletionSource idle, 10, 10);
        client.ChangeMessageVisibilityBatchAsync(
                Arg.Any<ChangeMessageVisibilityBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<ChangeMessageVisibilityBatchResponse>>(_ =>
                throw new AmazonSQSException(SecretExceptionText) { ErrorCode = "AccessDenied" });
        var logger = new CapturingLogger();
        var adapter = new SqsTransportAdapter(Options(), logger, client);
        using var cts = new CancellationTokenSource();

        IAsyncEnumerator<InboundMessage> enumerator = await ReadFirstAsync(adapter, idle, cts.Token);
        await cts.CancelAsync();
        await enumerator.DisposeAsync().AsTask().WaitAsync(_guard, TestContext.Current.CancellationToken);

        await client.Received(1).ChangeMessageVisibilityBatchAsync(
            Arg.Any<ChangeMessageVisibilityBatchRequest>(), Arg.Any<CancellationToken>());
        adapter.InFlightRegistry.Count.Should().Be(1, "the registry is cleaned even in dispose-only mode");
        logger.Warnings.Should().ContainSingle();
        logger.Warnings[0].Should().Contain("AmazonSQSException").And.Contain("AccessDenied");
        logger.AllText.Should().NotContain(SecretHandlePrefix).And.NotContain(SecretExceptionText);
    }

    [Fact]
    public async Task ConsumeAsync_PartOfBatchFailsToRelease_LogsOnceAndStillCleansRegistry()
    {
        IAmazonSQS client = CreateClient(out TaskCompletionSource idle, 5);
        client.ChangeMessageVisibilityBatchAsync(
                Arg.Any<ChangeMessageVisibilityBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChangeMessageVisibilityBatchResponse
            {
                Failed =
                [
                    new BatchResultErrorEntry { Id = "0", Code = "ReceiptHandleIsInvalid", Message = SecretExceptionText },
                    new BatchResultErrorEntry { Id = "2", Code = "ReceiptHandleIsInvalid", Message = SecretExceptionText },
                ],
            }));
        var logger = new CapturingLogger();
        var adapter = new SqsTransportAdapter(Options(), logger, client);
        using var cts = new CancellationTokenSource();

        IAsyncEnumerator<InboundMessage> enumerator = await ReadFirstAsync(adapter, idle, cts.Token);
        await cts.CancelAsync();
        await enumerator.DisposeAsync().AsTask().WaitAsync(_guard, TestContext.Current.CancellationToken);

        await client.Received(1).ChangeMessageVisibilityBatchAsync(
            Arg.Any<ChangeMessageVisibilityBatchRequest>(), Arg.Any<CancellationToken>());
        adapter.InFlightRegistry.Count.Should().Be(1);
        logger.Warnings.Should().ContainSingle();
        logger.Warnings[0].Should().Contain("2").And.Contain("ReceiptHandleIsInvalid");
        logger.AllText.Should().NotContain(SecretHandlePrefix).And.NotContain(SecretExceptionText);
    }

    [Fact]
    public async Task ConsumeAsync_SlowBroker_DrainFinishesWithinSharedBudget()
    {
        IAmazonSQS client = CreateClient(out TaskCompletionSource idle, 10, 10);
        client.ChangeMessageVisibilityBatchAsync(
                Arg.Any<ChangeMessageVisibilityBatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => BlockUntilCancelledAsync<ChangeMessageVisibilityBatchResponse>(ci.Arg<CancellationToken>()));
        var logger = new CapturingLogger();
        var adapter = new SqsTransportAdapter(Options(), logger, client)
        {
            ShutdownDrainBudget = TimeSpan.FromMilliseconds(300),
        };
        using var cts = new CancellationTokenSource();

        IAsyncEnumerator<InboundMessage> enumerator = await ReadFirstAsync(adapter, idle, cts.Token);
        await cts.CancelAsync();

        var stopwatch = Stopwatch.StartNew();
        await enumerator.DisposeAsync().AsTask().WaitAsync(_guard, TestContext.Current.CancellationToken);
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3), "the whole drain shares one budget");
        await client.Received(1).ChangeMessageVisibilityBatchAsync(
            Arg.Any<ChangeMessageVisibilityBatchRequest>(), Arg.Any<CancellationToken>());
        adapter.InFlightRegistry.Count.Should().Be(1);
        logger.Warnings.Should().ContainSingle().Which.Should().Contain("budget");
    }

    [Fact]
    public async Task ConsumeAsync_RepeatedStartAndStop_RegistryHoldsOnlyTrulyInFlightMessages()
    {
        IAmazonSQS client = CreateClient(out _);
        var adapter = new SqsTransportAdapter(Options(), new CapturingLogger(), client);

        for (int cycle = 1; cycle <= 3; cycle++)
        {
            TaskCompletionSource idle = ScriptReceive(client, 5);
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
        var client = Substitute.For<IAmazonSQS>();
        client.GetQueueUrlAsync(QueueName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GetQueueUrlResponse { QueueUrl = QueueUrl }));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var polled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ReceiveMessageAsync(Arg.Any<ReceiveMessageRequest>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                polled.TrySetResult();
                await gate.Task; // ignores the token: the response arrives after cancellation
                return Batch(3);
            });
        List<int> sizes = CaptureBatchSizes(client);
        var adapter = new SqsTransportAdapter(Options(), new CapturingLogger(), client);
        using var cts = new CancellationTokenSource();

        IAsyncEnumerator<InboundMessage> enumerator =
            adapter.ConsumeAsync(QueueName, Flow(), cts.Token).GetAsyncEnumerator(cts.Token);
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
        IAmazonSQS client = CreateClient(out TaskCompletionSource idle, 1);
        var adapter = new SqsTransportAdapter(Options(), new CapturingLogger(), client);
        using var cts = new CancellationTokenSource();

        IAsyncEnumerator<InboundMessage> enumerator = await ReadFirstAsync(adapter, idle, cts.Token);

        // The consume token is never cancelled: disposal must stop the long-polling loop on its own.
        await enumerator.DisposeAsync().AsTask().WaitAsync(_guard, TestContext.Current.CancellationToken);

        cts.IsCancellationRequested.Should().BeFalse();
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class CapturingLogger : ILogger<SqsTransportAdapter>
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
