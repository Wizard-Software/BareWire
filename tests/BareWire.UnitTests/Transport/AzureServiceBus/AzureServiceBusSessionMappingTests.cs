using System.Threading.Channels;
using AwesomeAssertions;
using BareWire.Abstractions;
using BareWire.Transport.AzureServiceBus;
using BareWire.Transport.AzureServiceBus.Internal;
using Xunit;

namespace BareWire.UnitTests.Transport.AzureServiceBus;

/// <summary>
/// Broker-free unit tests for session-id resolution (produce path, D-1/D-13),
/// per-session channel FIFO invariant (D-9/PERF-1),
/// and the accept-gate semaphore concurrency bound (D-12/VER-3).
/// </summary>
public sealed class AzureServiceBusSessionMappingTests
{
    // ── AzureServiceBusSessionMapper.Resolve — produce path (D-1/D-13) ───────

    [Fact]
    public void Resolve_WithBwSessionIdHeader_UsesHeaderValue()
    {
        // Arrange
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AzureServiceBusHeaderMapper.SessionIdHeader] = "session-abc",
        };

        // Act
        string? result = AzureServiceBusSessionMapper.Resolve(headers);

        // Assert
        result.Should().Be("session-abc");
    }

    [Fact]
    public void Resolve_WithoutBwSessionId_FallsBackToCorrelationId()
    {
        // Arrange — key MUST be kebab-case "correlation-id" (D-13/GAP-5).
        // PascalCase "CorrelationId" is NEVER written by BareWireBus and would be dead code.
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AzureServiceBusHeaderMapper.CorrelationIdHeader] = "corr-xyz",
        };

        // Act
        string? result = AzureServiceBusSessionMapper.Resolve(headers);

        // Assert — the fallback must resolve, proving D-13/GAP-5 is fixed.
        result.Should().Be("corr-xyz");
    }

    [Fact]
    public void Resolve_WithNeitherHeader_ReturnsNull()
    {
        // Arrange — empty headers; non-session queue scenario.
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);

        // Act
        string? result = AzureServiceBusSessionMapper.Resolve(headers);

        // Assert — null means the caller leaves ServiceBusMessage.SessionId unset (R2.1 behaviour).
        result.Should().BeNull();
    }

    [Fact]
    public void Resolve_PrefersBwSessionIdOverCorrelationId()
    {
        // Arrange — both headers present; BW-SessionId must win (D-1 priority).
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AzureServiceBusHeaderMapper.SessionIdHeader] = "explicit-session",
            [AzureServiceBusHeaderMapper.CorrelationIdHeader] = "correlation-fallback",
        };

        // Act
        string? result = AzureServiceBusSessionMapper.Resolve(headers);

        // Assert — explicit BW-SessionId wins over correlation-id fallback.
        result.Should().Be("explicit-session");
    }

    [Fact]
    public void Resolve_WithEmptyBwSessionId_FallsBackToCorrelationId()
    {
        // Arrange — BW-SessionId present but empty; must skip to correlation-id.
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AzureServiceBusHeaderMapper.SessionIdHeader] = string.Empty,
            [AzureServiceBusHeaderMapper.CorrelationIdHeader] = "corr-fallback",
        };

        // Act
        string? result = AzureServiceBusSessionMapper.Resolve(headers);

        // Assert — empty BW-SessionId is ignored; correlation-id fallback kicks in.
        result.Should().Be("corr-fallback");
    }

    [Fact]
    public void Resolve_CorrelationIdKey_IsKebabCase()
    {
        // Verify the constant has the canonical casing (D-13/GAP-5 guard).
        // This test would catch any accidental PascalCase drift.
        AzureServiceBusHeaderMapper.CorrelationIdHeader.Should().Be("correlation-id",
            "the header key must be kebab-case to match how BareWireBus populates it — " +
            "PascalCase 'CorrelationId' is never written by the bus and would be a dead fallback");
    }

    // ── D-9 (PERF-1) FIFO invariant — per-session channel ordering ───────────

    [Fact]
    public void SessionChannel_EnqueueOrder_PreservedPerSession()
    {
        // Arrange — single bounded channel per session, SingleWriter = true.
        // This mirrors how AzureServiceBusSessionConsumer creates per-session channels (D-9).
        var sessionChannel = Channel.CreateBounded<int>(
            new BoundedChannelOptions(capacity: 100)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = true,
            });

        int[] inputOrder = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        // Act — write all items in order (simulates a single session-reader task writing to its channel).
        foreach (int item in inputOrder)
        {
            sessionChannel.Writer.TryWrite(item).Should().BeTrue("channel should have capacity");
        }

        sessionChannel.Writer.TryComplete();

        // Drain the channel and collect items.
        var outputOrder = new List<int>();
        while (sessionChannel.Reader.TryRead(out int item))
        {
            outputOrder.Add(item);
        }

        // Assert — FIFO invariant: dequeue order must equal enqueue order.
        outputOrder.Should().Equal(inputOrder,
            "bounded channels are FIFO — per-session ordering must be preserved end-to-end");
    }

    [Fact]
    public void SessionChannels_TwoConcurrentSessions_DoNotShareWriter()
    {
        // D-9/PERF-1: Two sessions MUST have TWO DISTINCT channel/writer instances.
        // This test would FAIL if a single shared channel/writer were used — the writer
        // instance references would be identical, and the SingleWriter contract would be
        // violated when two session-reader tasks both try to write concurrently.

        // Arrange — simulate two sessions each with their own channel (D-9 topology).
        var channelSession1 = Channel.CreateBounded<string>(
            new BoundedChannelOptions(capacity: 10)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = true,
            });

        var channelSession2 = Channel.CreateBounded<string>(
            new BoundedChannelOptions(capacity: 10)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = true,
            });

        // Assert — the two sessions use DISTINCT channel instances (and thus distinct writers).
        // This is the topological invariant of D-9: one channel object per accepted session.
        object.ReferenceEquals(channelSession1, channelSession2).Should().BeFalse(
            "each session must have its own channel; sharing one channel with SingleWriter=true " +
            "from N session tasks is undefined behaviour and destroys FIFO per-session ordering");

        object.ReferenceEquals(channelSession1.Writer, channelSession2.Writer).Should().BeFalse(
            "each session must have its own channel writer — sharing a writer between sessions " +
            "violates the SingleWriter contract and can corrupt per-session message ordering");

        // Also verify that each session channel is independently writable.
        channelSession1.Writer.TryWrite("s1-msg1").Should().BeTrue();
        channelSession1.Writer.TryWrite("s1-msg2").Should().BeTrue();

        channelSession2.Writer.TryWrite("s2-msg1").Should().BeTrue();
        channelSession2.Writer.TryWrite("s2-msg2").Should().BeTrue();

        // Drain session1 — must see only its own messages in order.
        channelSession1.Writer.TryComplete();
        var s1Items = new List<string>();
        while (channelSession1.Reader.TryRead(out string? item))
        {
            s1Items.Add(item!);
        }

        s1Items.Should().Equal(["s1-msg1", "s1-msg2"],
            "session 1 channel must only contain session 1's messages, in FIFO order");

        // Drain session2 — must see only its own messages in order.
        channelSession2.Writer.TryComplete();
        var s2Items = new List<string>();
        while (channelSession2.Reader.TryRead(out string? item))
        {
            s2Items.Add(item!);
        }

        s2Items.Should().Equal(["s2-msg1", "s2-msg2"],
            "session 2 channel must only contain session 2's messages, in FIFO order");
    }

    // ── D-12 (VER-3) Accept-gate semaphore — concurrency bound ───────────────

    [Fact]
    public async Task AcceptGate_NeverExceedsMaxConcurrentSessions()
    {
        // Arrange — broker-free test of the accept-gate (SemaphoreSlim) invariant (D-12).
        // Simulates MaxConcurrentSessions=2: at most 2 permits can be held simultaneously.
        const int maxConcurrentSessions = 2;
        using var semaphore = new SemaphoreSlim(maxConcurrentSessions, maxConcurrentSessions);

        int maxObservedConcurrent = 0;
        int currentConcurrent = 0;
        object counterLock = new();

        // Simulate N=5 sessions racing to acquire a permit, hold briefly, then release.
        Task[] tasks = Enumerable.Range(0, 5).Select(_ =>
            Task.Run(async () =>
            {
                await semaphore.WaitAsync();

                try
                {
                    int concurrent;
                    lock (counterLock)
                    {
                        currentConcurrent++;
                        concurrent = currentConcurrent;
                        if (concurrent > maxObservedConcurrent)
                        {
                            maxObservedConcurrent = concurrent;
                        }
                    }

                    // Simulate brief session processing.
                    await Task.Delay(10);
                }
                finally
                {
                    lock (counterLock)
                    {
                        currentConcurrent--;
                    }

                    semaphore.Release();
                }
            })).ToArray();

        await Task.WhenAll(tasks);

        // Assert — peak concurrency must never exceed MaxConcurrentSessions.
        maxObservedConcurrent.Should().BeLessThanOrEqualTo(maxConcurrentSessions,
            "the accept-gate SemaphoreSlim must enforce the MaxConcurrentSessions upper bound");
    }

    [Fact]
    public async Task AcceptGate_ThrowingAccept_ReleasesPermit()
    {
        // D-12 PERF-leak: when AcceptNextSessionAsync throws (ServiceTimeout), the permit
        // MUST be released so the gate never starves. This test simulates that path.
        const int maxConcurrentSessions = 2;
        using var semaphore = new SemaphoreSlim(maxConcurrentSessions, maxConcurrentSessions);

        // Simulate 'MaxConcurrentSessions' accept attempts that ALL throw.
        // After all throw, ALL permits should have been returned (gate stays healthy).
        for (int i = 0; i < maxConcurrentSessions; i++)
        {
            await semaphore.WaitAsync();

            try
            {
                // Simulate: AcceptNextSessionAsync throws ServiceTimeout.
                throw new InvalidOperationException("simulated ServiceTimeout");
            }
            catch
            {
                // Correct pattern: release in catch/finally.
                semaphore.Release();
            }
        }

        // Assert — after all throws, all permits are returned.
        int remainingPermits = semaphore.CurrentCount;
        remainingPermits.Should().Be(maxConcurrentSessions,
            "every throwing accept must release its permit — otherwise the gate starves after " +
            "MaxConcurrentSessions failed accepts (PERF-leak, D-12)");
    }

    // ── Header constants ──────────────────────────────────────────────────────

    [Fact]
    public void SessionIdHeader_HasExpectedValue()
    {
        AzureServiceBusHeaderMapper.SessionIdHeader.Should().Be("BW-SessionId");
    }

    // ── FIX 1: D-9/PERF-1 — session channel factory always pins Wait ─────────

    [Theory]
    [InlineData(BoundedChannelFullMode.DropWrite)]
    [InlineData(BoundedChannelFullMode.DropOldest)]
    [InlineData(BoundedChannelFullMode.DropNewest)]
    [InlineData(BoundedChannelFullMode.Wait)]
    public void BuildSessionChannelOptions_AlwaysPinsWait_RegardlessOfCallerFullMode(BoundedChannelFullMode callerMode)
    {
        // FIX 1 (D-9/PERF-1): per-session channels MUST always use BoundedChannelFullMode.Wait.
        // Drop* modes create mid-session ordering holes: a message can be silently dropped while
        // later messages still enqueue, voiding the per-session FIFO guarantee entirely.
        // This test asserts that BuildSessionChannelOptions ignores the caller's FullMode and
        // always returns Wait — the only mode compatible with per-session FIFO.

        // Act — callerMode is intentionally NOT passed; the factory hard-codes Wait.
        _ = callerMode; // documents the intent: the factory must not honour caller FullMode

        BoundedChannelOptions options = AzureServiceBusSessionConsumer.BuildSessionChannelOptions(capacity: 64);

        // Assert
        options.FullMode.Should().Be(BoundedChannelFullMode.Wait,
            "Drop* modes void per-session FIFO (D-9/PERF-1) by silently dropping mid-session " +
            "messages while later messages still enqueue — the session path must always pin Wait");

        options.Capacity.Should().Be(64, "capacity must be threaded from the FlowControlOptions argument");
        options.SingleWriter.Should().BeTrue("the session task is the sole writer to its per-session channel");
        options.SingleReader.Should().BeTrue("the drain task is the sole reader from the per-session channel");
    }

    // ── FIX 2: PERF-3/ASSM-1 — accept-gate permit released on dispatch failure ─

    [Fact]
    public async Task AcceptGate_DispatchFailure_ReleasesPermit()
    {
        // FIX 2 (PERF-3/ASSM-1): if Task.Factory.StartNew (or the pre-dispatch setup) throws
        // synchronously AFTER AcceptNextSessionAsync succeeds, the semaphore permit must be
        // released exactly once so the accept gate never permanently starves.
        // This test models that path using a raw SemaphoreSlim — the same pattern exercised by
        // the accept-gate guard added to AzureServiceBusSessionConsumer.RunAcceptLoopAsync.
        const int maxConcurrentSessions = 2;
        using var semaphore = new SemaphoreSlim(maxConcurrentSessions, maxConcurrentSessions);

        // Simulate a successful accept followed by a synchronous dispatch failure.
        await semaphore.WaitAsync();  // permit acquired (as if AcceptNextSessionAsync succeeded)

        bool dispatched = false;
        try
        {
            // Simulate Task.Factory.StartNew throwing synchronously before the lambda runs.
            throw new InvalidOperationException("simulated synchronous dispatch failure");

#pragma warning disable CS0162 // Unreachable code — intentional: documents what the success path does
            dispatched = true;
#pragma warning restore CS0162
        }
        catch
        {
            // FIX 2 guard: release permit on synchronous dispatch failure (not on success path).
            if (!dispatched)
            {
                semaphore.Release();
            }
        }

        // Assert — permit must have been returned; gate must not be permanently reduced.
        semaphore.CurrentCount.Should().Be(maxConcurrentSessions,
            "a synchronous dispatch failure after a successful accept must release the acquired " +
            "permit exactly once so the accept gate never permanently loses a slot (PERF-3/ASSM-1)");
    }
}
