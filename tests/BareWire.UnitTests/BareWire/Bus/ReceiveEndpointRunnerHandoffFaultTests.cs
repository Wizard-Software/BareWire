using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using BareWire;
using BareWire.Abstractions;
using BareWire.Abstractions.Configuration;
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

    [Fact]
    public async Task RunAsync_OrderingKeyResolutionThrows_NacksDisposesReleasesCreditAndRethrows()
    {
        // Arrange — ordered path (2 lanes), single message whose "seq" header lookup throws.
        var logger = new CapturingLogger();
        var fault = new InvalidOperationException("ordering key resolution failed");
        var adapter = new FaultFakeAdapter(new FaultingHeaders("seq", fault, SecretSeqValue));
        EndpointBinding binding = BuildBinding(prefetch: 4, orderingConcurrency: 2);
        (ReceiveEndpointRunner runner, CreditManager credit) = CreateRunner(binding, adapter, logger);

        // Act
        Func<Task> act = () => runner.RunAsync(TestContext.Current.CancellationToken);

        // Assert
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(fault);
        IsDisposed(adapter.Yielded.Single()).Should().BeTrue("the faulted message must have its buffer returned");
        adapter.Settlements.Should().ContainSingle().Which.Action.Should().Be(SettlementAction.Nack);
        credit.InflightCount.Should().Be(0);
        credit.InflightBytes.Should().Be(0);

        // The binding has no dead-letter exchange, so the Nack must be reported as potentially lost,
        // and no log entry may expose the ordering-key (header) value.
        logger.Entries.Should().Contain(e => e.Text.Contains("negatively acknowledged with no dead-letter"));
        logger.Entries.Should().NotContain(e => e.Text.Contains(SecretSeqValue));
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
    public async Task RunAsync_CreditReleaseThrowsDuringCleanup_StillDisposesMessageAndRethrowsOriginal()
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

        // Act
        Func<Task> act = () => runner.RunAsync(TestContext.Current.CancellationToken);

        // Assert — the original fault is not masked and the pooled buffer is returned regardless.
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(fault);
        IsDisposed(adapter.Yielded.Single()).Should().BeTrue("Dispose must run even when ReleaseInflight throws");
    }

    // ── Test harness ─────────────────────────────────────────────────────────────────────────────

    private static EndpointBinding BuildBinding(int prefetch, int? orderingConcurrency) => new()
    {
        EndpointName = QueueName,
        PrefetchCount = prefetch,
        ConcurrentMessageLimit = orderingConcurrency ?? 1,
        Ordering = orderingConcurrency is { } concurrency ? new FaultTestOrdering(concurrency) : null,
        RawConsumers = [typeof(StalledRawConsumer)],
    };

    private static (ReceiveEndpointRunner Runner, CreditManager Credit) CreateRunner(
        EndpointBinding binding, ITransportAdapter adapter, ILogger<ReceiveEndpointRunner> logger)
    {
        var services = new ServiceCollection();
        services.AddScoped<StalledRawConsumer>();
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
            new NullInstrumentation(),
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
        private readonly ConcurrentQueue<(LogLevel Level, string Text)> _entries = new();

        internal IReadOnlyCollection<(LogLevel Level, string Text)> Entries => _entries;

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

            _entries.Enqueue((logLevel, text));
        }
    }

    private sealed class StalledRawConsumer : IRawConsumer
    {
        public Task ConsumeAsync(RawConsumeContext context) => Task.CompletedTask;
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
    /// Yields a single pooled-buffer message carrying the supplied headers, then waits for cancellation.
    /// The message is recorded in <see cref="Yielded"/> BEFORE <c>yield return</c>.
    /// </summary>
    private sealed class FaultFakeAdapter(IReadOnlyDictionary<string, string> headers) : ITransportAdapter
    {
        private readonly ConcurrentQueue<InboundMessage> _yielded = new();
        private readonly ConcurrentQueue<(SettlementAction Action, string MessageId)> _settlements = new();

        internal ConcurrentQueue<InboundMessage> Yielded => _yielded;

        internal ConcurrentQueue<(SettlementAction Action, string MessageId)> Settlements => _settlements;

        public string TransportName => "FaultFake";

        public TransportCapabilities Capabilities => TransportCapabilities.None;

        public async IAsyncEnumerable<InboundMessage> ConsumeAsync(
            string endpointName,
            FlowControlOptions flowControl,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(MessageLength);
            var message = new InboundMessage(
                messageId: "m-0",
                headers: headers,
                body: new ReadOnlySequence<byte>(buffer, 0, MessageLength),
                deliveryTag: 0,
                pooledBuffer: buffer);

            _yielded.Enqueue(message);
            yield return message;

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
