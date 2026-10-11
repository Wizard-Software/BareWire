using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using BareWire;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Observability;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Bus;
using BareWire.FlowControl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Core.Bus;

/// <summary>
/// Regression guard for the window between "message read from the transport" and "message handed to its
/// owner" in <c>ReceiveEndpointRunner.RunAsync</c> for exceptions OTHER than cancellation. A fault in that
/// window (ordering-key resolution, credit wait) must still settle the message, return its pooled buffer
/// and release any credit it was granted, and the original exception must still propagate.
/// </summary>
public sealed class ReceiveEndpointRunnerHandoffFaultTests
{
    private const string QueueName = "handoff-fault";
    private const int MessageLength = 16;
    private const string SecretSeqValue = "secret-seq-value-7f3a";

    private const string ChannelIdHeader = "BW-ConsumerChannelId";

    [Theory]
    [InlineData(false)] // sequential path
    [InlineData(true)] // ordered path
    public async Task RunAsync_MessageSpecificFaultThenValidMessage_NacksFaultedAndProcessesNext(bool ordered)
    {
        // Arrange — m-0: the channel-id header read throws; m-1: valid headers.
        var logger = new CapturingLogger();
        var fault = new InvalidOperationException("header read failed");
        var adapter = new FaultFakeAdapter(
        [
            new FaultingHeaders(ChannelIdHeader, fault, SecretSeqValue),
            new Dictionary<string, string>(StringComparer.Ordinal),
        ]);
        var processed = new CountingRawConsumerState(expected: 1);
        EndpointBinding binding = BuildBinding(prefetch: 4, orderingConcurrency: ordered ? 2 : null);
        (ReceiveEndpointRunner runner, CreditManager credit) = CreateRunner(binding, adapter, logger, processed);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act
        Task run = runner.RunAsync(cts.Token);
        await processed.Completed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await run; // must complete without the original fault

        // Assert
        adapter.Settlements.Should().Contain(s => s.MessageId == "m-0" && s.Action == SettlementAction.Nack);
        adapter.Settlements.Should().Contain(s => s.MessageId == "m-1" && s.Action == SettlementAction.Ack);
        adapter.Yielded.Should().OnlyContain(m => IsDisposed(m));
        credit.InflightCount.Should().Be(0);
        logger.Entries.Should().Contain(e => e.Text.Contains("faulted before handoff"));
        logger.Entries.Should().NotContain(e => e.Text.Contains("The endpoint has stopped consuming"));
    }

    [Fact]
    public async Task RunAsync_OrderingKeyResolutionThrows_NacksDisposesReleasesCreditAndContinues()
    {
        // Arrange — ordered path (2 lanes), single message whose "seq" header lookup throws.
        var logger = new CapturingLogger();
        var instrumentation = Substitute.For<IBareWireInstrumentation>();
        var fault = new InvalidOperationException("ordering key resolution failed");
        var adapter = new FaultFakeAdapter(new FaultingHeaders("seq", fault, SecretSeqValue));
        EndpointBinding binding = BuildBinding(prefetch: 4, orderingConcurrency: 2);
        (ReceiveEndpointRunner runner, CreditManager credit) =
            CreateRunner(binding, adapter, logger, instrumentation: instrumentation);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act
        Task run = runner.RunAsync(cts.Token);
        await WaitUntilAsync(() => !adapter.Settlements.IsEmpty);
        await cts.CancelAsync();
        await run; // the loop survived the fault

        // Assert
        IsDisposed(adapter.Yielded.Single()).Should().BeTrue("the faulted message must have its buffer returned");
        adapter.Settlements.Should().ContainSingle().Which.Action.Should().Be(SettlementAction.Nack);
        credit.InflightCount.Should().Be(0);
        credit.InflightBytes.Should().Be(0);
        instrumentation.Received(1).RecordFailure(QueueName, "unknown", "HandoffFault");

        // The binding has no dead-letter exchange, so the Nack must be reported as potentially lost,
        // and no log entry may expose the ordering-key (header) value.
        logger.Entries.Should().Contain(e => e.Text.Contains("negatively acknowledged with no dead-letter"));
        logger.Entries.Should().NotContain(e => e.Text.Contains(SecretSeqValue));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ConsecutiveMessageFaultsReachLimit_StopsLoopWithOriginalException(bool ordered)
    {
        // Arrange — MaxConsecutiveHandoffFaults distinct faulted messages in a row.
        int limit = ReceiveEndpointRunner.MaxConsecutiveHandoffFaults;
        var logger = new CapturingLogger();
        var faults = Enumerable.Range(0, limit)
            .Select(i => new InvalidOperationException($"header read failed {i}"))
            .ToArray();
        var adapter = new FaultFakeAdapter(
            faults.Select(f => (IReadOnlyDictionary<string, string>)new FaultingHeaders(
                ChannelIdHeader, f, SecretSeqValue)).ToArray());
        EndpointBinding binding = BuildBinding(prefetch: 4, orderingConcurrency: ordered ? 2 : null);
        (ReceiveEndpointRunner runner, CreditManager credit) = CreateRunner(binding, adapter, logger);

        // Act
        Func<Task> act = () => runner.RunAsync(TestContext.Current.CancellationToken);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(faults[^1]);
        adapter.Settlements.Should().HaveCount(limit).And.OnlyContain(s => s.Action == SettlementAction.Nack);
        adapter.Yielded.Should().OnlyContain(m => IsDisposed(m));
        credit.InflightCount.Should().Be(0);
        logger.Entries.Should().Contain(e => e.Text.Contains("The endpoint has stopped consuming"));

        // Continuation warnings (1..limit-1) carry no exception; only the loop-faulted entry does.
        logger.Entries.Count(e => e.Text.Contains("faulted before handoff")).Should().Be(limit - 1);
        logger.Entries.Where(e => e.Exception is not null).Should().ContainSingle()
            .Which.Text.Should().Contain("The endpoint has stopped consuming");
        logger.Entries.Should().NotContain(e => e.Text.Contains(SecretSeqValue));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ValidMessageBetweenFaults_ResetsConsecutiveFaultCounter(bool ordered)
    {
        // Arrange — (limit-1) faults, valid, (limit-1) faults, valid: never `limit` faults in a row.
        int limit = ReceiveEndpointRunner.MaxConsecutiveHandoffFaults;
        List<IReadOnlyDictionary<string, string>> headers = [];
        for (int round = 0; round < 2; round++)
        {
            for (int i = 0; i < limit - 1; i++)
            {
                headers.Add(new FaultingHeaders(
                    ChannelIdHeader, new InvalidOperationException("header read failed"), SecretSeqValue));
            }

            headers.Add(new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var adapter = new FaultFakeAdapter(headers);
        var processed = new CountingRawConsumerState(expected: 2);
        EndpointBinding binding = BuildBinding(prefetch: 4, orderingConcurrency: ordered ? 2 : null);
        (ReceiveEndpointRunner runner, CreditManager credit) =
            CreateRunner(binding, adapter, NullLogger<ReceiveEndpointRunner>.Instance, processed);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act
        Task run = runner.RunAsync(cts.Token);
        await processed.Completed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await run;

        // Assert
        adapter.Settlements.Count(s => s.Action == SettlementAction.Nack).Should().Be(2 * (limit - 1));
        adapter.Settlements.Count(s => s.Action == SettlementAction.Ack).Should().Be(2);
        credit.InflightCount.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_SameMessageRedeliveredRepeatedly_DoesNotCountTowardLimit(bool ordered)
    {
        // Arrange — the same poison message ("poison-1") is redelivered limit + 2 times, then a valid one.
        int limit = ReceiveEndpointRunner.MaxConsecutiveHandoffFaults;
        var logger = new CapturingLogger();
        List<(string MessageId, IReadOnlyDictionary<string, string> Headers)> deliveries = [];
        for (int i = 0; i < limit + 2; i++)
        {
            deliveries.Add((
                "poison-1",
                new FaultingHeaders(ChannelIdHeader, new InvalidOperationException("header read failed"), SecretSeqValue)));
        }

        deliveries.Add(("valid-1", new Dictionary<string, string>(StringComparer.Ordinal)));
        var adapter = new FaultFakeAdapter(deliveries);
        var processed = new CountingRawConsumerState(expected: 1);
        EndpointBinding binding = BuildBinding(prefetch: 4, orderingConcurrency: ordered ? 2 : null);
        (ReceiveEndpointRunner runner, CreditManager credit) = CreateRunner(binding, adapter, logger, processed);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act
        Task run = runner.RunAsync(cts.Token);
        await processed.Completed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await run; // no exception: redeliveries of one message never reach the limit

        // Assert
        adapter.Settlements.Count(s => s.Action == SettlementAction.Nack).Should().Be(limit + 2);
        adapter.Settlements.Should().Contain(s => s.MessageId == "valid-1" && s.Action == SettlementAction.Ack);
        credit.InflightCount.Should().Be(0);
        logger.Entries.Should().NotContain(e => e.Text.Contains("The endpoint has stopped consuming"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ConsecutiveFaultsWithEmptyMessageId_CountEachAndStopAtLimit(bool ordered)
    {
        // Arrange — an empty MessageId cannot be compared, so every delivery counts as a new fault.
        int limit = ReceiveEndpointRunner.MaxConsecutiveHandoffFaults;
        var faults = Enumerable.Range(0, limit)
            .Select(i => new InvalidOperationException($"header read failed {i}"))
            .ToArray();
        var adapter = new FaultFakeAdapter(
            faults.Select(f => (string.Empty, (IReadOnlyDictionary<string, string>)new FaultingHeaders(
                ChannelIdHeader, f, SecretSeqValue))).ToArray());
        EndpointBinding binding = BuildBinding(prefetch: 4, orderingConcurrency: ordered ? 2 : null);
        (ReceiveEndpointRunner runner, _) =
            CreateRunner(binding, adapter, NullLogger<ReceiveEndpointRunner>.Instance);

        // Act
        Func<Task> act = () => runner.RunAsync(TestContext.Current.CancellationToken);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(faults[^1]);
        adapter.Settlements.Should().HaveCount(limit).And.OnlyContain(s => s.Action == SettlementAction.Nack);
    }

    [Theory]
    [InlineData(false)] // sequential path
    [InlineData(true)] // ordered path
    public async Task RunAsync_CreditWaitThrows_RequeuesDisposesAndRethrows(bool ordered)
    {
        // Arrange — PrefetchCount 1, the only slot pre-occupied by the test and the CreditManager disposed:
        // TryGrantCredits returns 0 and WaitForCreditAsync throws ObjectDisposedException (non-OCE).
        var adapter = new FaultFakeAdapter(new Dictionary<string, string>(StringComparer.Ordinal));
        EndpointBinding binding = BuildBinding(prefetch: 1, orderingConcurrency: ordered ? 2 : null);
        (ReceiveEndpointRunner runner, CreditManager credit) =
            CreateRunner(binding, adapter, NullLogger<ReceiveEndpointRunner>.Instance);
        credit.TryGrantCredits(1).Should().Be(1);
        credit.Dispose();

        // Act
        Func<Task> act = () => runner.RunAsync(TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<ObjectDisposedException>();
        IsDisposed(adapter.Yielded.Single()).Should().BeTrue();
        adapter.Settlements.Should().ContainSingle().Which.Action.Should().Be(SettlementAction.Requeue);
        credit.InflightCount.Should().Be(1, "credit never granted to the message must not be released by the runner");
    }

    [Fact]
    public async Task RunAsync_CreditReleaseThrowsDuringCleanup_StillDisposesMessageAndKeepsOriginalFaultHandling()
    {
        // Arrange — the key lookup disposes the CreditManager and then throws, so the cleanup's
        // ReleaseInflight (credit already granted) itself throws ObjectDisposedException.
        var fault = new InvalidOperationException("ordering key resolution failed");
        CreditManager? creditRef = null;
        var headers = new FaultingHeaders("seq", fault, SecretSeqValue, onFault: () => creditRef!.Dispose());
        var adapter = new FaultFakeAdapter(headers);
        EndpointBinding binding = BuildBinding(prefetch: 4, orderingConcurrency: 2);
        (ReceiveEndpointRunner runner, CreditManager credit) =
            CreateRunner(binding, adapter, NullLogger<ReceiveEndpointRunner>.Instance);
        creditRef = credit;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act
        Task run = runner.RunAsync(cts.Token);
        await WaitUntilAsync(() => !adapter.Settlements.IsEmpty);
        await cts.CancelAsync();
        await run;

        // Assert — the failing credit release neither masks the settlement nor skips the buffer return;
        // the message-specific fault is handled as usual (Nack, loop continues until cancelled).
        IsDisposed(adapter.Yielded.Single()).Should().BeTrue("Dispose must run even when ReleaseInflight throws");
        adapter.Settlements.Should().ContainSingle().Which.Action.Should().Be(SettlementAction.Nack);
    }

    // ── Test harness ─────────────────────────────────────────────────────────────────────────────

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met within 5 seconds.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private static EndpointBinding BuildBinding(int prefetch, int? orderingConcurrency) => new()
    {
        EndpointName = QueueName,
        PrefetchCount = prefetch,
        ConcurrentMessageLimit = orderingConcurrency ?? 1,
        Ordering = orderingConcurrency is { } concurrency ? new FaultTestOrdering(concurrency) : null,
        RawConsumers = [typeof(CountingRawConsumer)],
    };

    private static (ReceiveEndpointRunner Runner, CreditManager Credit) CreateRunner(
        EndpointBinding binding,
        ITransportAdapter adapter,
        ILogger<ReceiveEndpointRunner> logger,
        CountingRawConsumerState? processed = null,
        IBareWireInstrumentation? instrumentation = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(processed ?? new CountingRawConsumerState(expected: int.MaxValue));
        services.AddScoped<CountingRawConsumer>();
        ServiceProvider provider = services.BuildServiceProvider();

        var deserializerResolver = Substitute.For<IDeserializerResolver>();
        deserializerResolver.Resolve(Arg.Any<string?>()).Returns(Substitute.For<IMessageDeserializer>());

        var flowController = new FlowController(NullLogger<FlowController>.Instance);

        var runner = new ReceiveEndpointRunner(
            binding,
            adapter,
            deserializerResolver,
            Substitute.For<IPublishEndpoint>(),
            Substitute.For<ISendEndpointProvider>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            flowController,
            instrumentation ?? new NullInstrumentation(),
            logger);

        // Same options the runner passes to GetOrCreateManager, so both resolve the same CreditManager.
        CreditManager credit = flowController.GetOrCreateManager(
            binding.EndpointName,
            new FlowControlOptions { MaxInFlightMessages = binding.PrefetchCount });

        return (runner, credit);
    }

    /// <summary>
    /// A message is disposed when its pooled buffer can no longer be pinned. If the pin succeeds the
    /// message is still alive: the pin is released immediately and <c>false</c> is returned.
    /// </summary>
    private static bool IsDisposed(InboundMessage message)
    {
        if (message.TryPinPooledBuffer())
        {
            message.UnpinPooledBuffer();
            return false;
        }

        return true;
    }

    /// <summary>
    /// Headers that throw only for the ordering key, so the loop's other header lookups (outside the
    /// handoff window) still succeed and the fault lands inside ordering-key resolution.
    /// </summary>
    private sealed class FaultingHeaders(
        string throwOnKey, Exception fault, string keyValue, Action? onFault = null)
        : IReadOnlyDictionary<string, string>
    {
        public string this[string key] =>
            TryGetValue(key, out string? value) ? value : throw new KeyNotFoundException(key);

        public IEnumerable<string> Keys => [];

        public IEnumerable<string> Values => [];

        public int Count => 0;

        public bool ContainsKey(string key) => false;

        public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value)
        {
            if (key == throwOnKey)
            {
                onFault?.Invoke();
                _ = keyValue; // the would-be key value is never exposed through the exception
                throw fault;
            }

            value = null;
            return false;
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
            Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CapturingLogger : ILogger<ReceiveEndpointRunner>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Text, Exception? Exception)> _entries = new();

        internal IReadOnlyCollection<(LogLevel Level, string Text, Exception? Exception)> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string text = formatter(state, exception);
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                text += " " + string.Join(' ', pairs.Select(p => $"{p.Key}={p.Value}"));
            }

            if (exception is not null)
            {
                text += " " + exception;
            }

            _entries.Enqueue((logLevel, text, exception));
        }
    }

    private sealed class CountingRawConsumerState(int expected)
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        internal Task Completed => _completed.Task;

        internal void Record()
        {
            if (Interlocked.Increment(ref _count) >= expected)
            {
                _completed.TrySetResult();
            }
        }
    }

    private sealed class CountingRawConsumer(CountingRawConsumerState state) : IRawConsumer
    {
        public Task ConsumeAsync(RawConsumeContext context)
        {
            state.Record();
            return Task.CompletedTask;
        }
    }

    /// <summary>Minimal read-only ordering carrier with a configurable lane count.</summary>
    private sealed class FaultTestOrdering(int concurrency) : IConsumerOrderingConfiguration
    {
        public string? HeaderName => "seq";
        public Delegate? Selector => null;
        public Type? SelectorMessageType => null;
        public bool UseCorrelationId => false;
        public int? Concurrency => concurrency;
        public ConsumerOrderingStrategy Strategy => ConsumerOrderingStrategy.LocalPartitioned;
        public TransportAffinity TransportAffinity => TransportAffinity.None;
        public int MaxDeliveryAttempts => 0;
    }

    /// <summary>
    /// Yields one pooled-buffer message per configured delivery (default ids <c>m-0</c>, <c>m-1</c>, ...),
    /// then waits for cancellation. Each message is recorded in <see cref="Yielded"/> BEFORE
    /// <c>yield return</c>.
    /// </summary>
    private sealed class FaultFakeAdapter : ITransportAdapter
    {
        private readonly IReadOnlyList<(string MessageId, IReadOnlyDictionary<string, string> Headers)> _deliveries;
        private readonly ConcurrentQueue<InboundMessage> _yielded = new();
        private readonly ConcurrentQueue<(SettlementAction Action, string MessageId)> _settlements = new();

        internal FaultFakeAdapter(IReadOnlyDictionary<string, string> headers)
            : this([headers])
        {
        }

        internal FaultFakeAdapter(IReadOnlyList<IReadOnlyDictionary<string, string>> headers)
            : this(headers.Select((h, i) => ($"m-{i}", h)).ToArray())
        {
        }

        internal FaultFakeAdapter(
            IReadOnlyList<(string MessageId, IReadOnlyDictionary<string, string> Headers)> deliveries) =>
            _deliveries = deliveries;

        internal ConcurrentQueue<InboundMessage> Yielded => _yielded;

        internal ConcurrentQueue<(SettlementAction Action, string MessageId)> Settlements => _settlements;

        public string TransportName => "FaultFake";

        public TransportCapabilities Capabilities => TransportCapabilities.None;

        public async IAsyncEnumerable<InboundMessage> ConsumeAsync(
            string endpointName,
            FlowControlOptions flowControl,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ulong deliveryTag = 0;
            foreach ((string messageId, IReadOnlyDictionary<string, string> headers) in _deliveries)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(MessageLength);
                var message = new InboundMessage(
                    messageId: messageId,
                    headers: headers,
                    body: new ReadOnlySequence<byte>(buffer, 0, MessageLength),
                    deliveryTag: deliveryTag++,
                    pooledBuffer: buffer);

                _yielded.Enqueue(message);
                yield return message;
            }

            // Block until cancellation so a missing fault handler cannot be masked by a clean end of stream.
            var tcs = new TaskCompletionSource();
            using (cancellationToken.Register(() => tcs.TrySetResult()))
            {
                await tcs.Task.ConfigureAwait(false);
            }
        }

        public Task SettleAsync(
            SettlementAction action, InboundMessage message, CancellationToken cancellationToken = default)
        {
            _settlements.Enqueue((action, message.MessageId));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SendResult>> SendBatchAsync(
            IReadOnlyList<OutboundMessage> messages, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeployTopologyAsync(
            TopologyDeclaration topology, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
