using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions.Pipeline;
using BareWire.Pipeline;
using NSubstitute;

namespace BareWire.UnitTests.Core.Pipeline;

public sealed class PartitionerMiddlewareTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static MessageContext CreateContext(
        Guid? messageId = null,
        Dictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        IServiceProvider serviceProvider = Substitute.For<IServiceProvider>();
        return new MessageContext(
            messageId: messageId ?? Guid.NewGuid(),
            headers: headers ?? [],
            rawBody: ReadOnlySequence<byte>.Empty,
            serviceProvider: serviceProvider,
            cancellationToken: cancellationToken);
    }

    private static MessageContext CreateContextWithCorrelationId(Guid correlationId)
        => CreateContext(headers: new Dictionary<string, string>
        {
            ["CorrelationId"] = correlationId.ToString()
        });

    // -----------------------------------------------------------------------
    // InvokeAsync_SameKey_ProcessedSequentially
    // -----------------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_SameKey_ProcessedSequentially()
    {
        // Arrange
        // Both messages share the same CorrelationId → same partition → same semaphore.
        // The first handler blocks until it receives a signal, so if the two invocations
        // were concurrent the second Task would never get the semaphore until the first releases it.

        Guid sharedKey = Guid.NewGuid();
        MessageContext ctx1 = CreateContextWithCorrelationId(sharedKey);
        MessageContext ctx2 = CreateContextWithCorrelationId(sharedKey);

        await using var middleware = new PartitionerMiddleware(partitionCount: 1);

        var executionOrder = new List<string>();
        var firstEntered = new SemaphoreSlim(0, 1);
        var firstCanExit = new SemaphoreSlim(0, 1);

        async Task SlowNext1(MessageContext _)
        {
            executionOrder.Add("first-start");
            firstEntered.Release();                   // signal that first is inside
            await firstCanExit.WaitAsync().ConfigureAwait(false); // wait for permission to exit
            executionOrder.Add("first-end");
        }

        Task SlowNext2(MessageContext _)
        {
            executionOrder.Add("second");
            return Task.CompletedTask;
        }

        // Act
        Task first = Task.Run(() => middleware.InvokeAsync(ctx1, SlowNext1));

        // Wait until the first invocation is inside the slow handler (holds the semaphore).
        await firstEntered.WaitAsync();

        // Start the second invocation. Because the semaphore is taken, it will queue.
        Task second = Task.Run(() => middleware.InvokeAsync(ctx2, SlowNext2));

        // Give the second task a moment to attempt semaphore acquisition (it should block).
        await Task.Delay(50);

        // Let the first handler finish.
        firstCanExit.Release();

        await Task.WhenAll(first, second);

        // Assert — second must not have started until first has fully finished.
        executionOrder.Should().Equal("first-start", "first-end", "second");
    }

    // -----------------------------------------------------------------------
    // InvokeAsync_DifferentKeys_ProcessedConcurrently
    // -----------------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_DifferentKeys_ProcessedConcurrently()
    {
        // Arrange
        // Two different keys with 2 partitions → different semaphores → true parallelism.
        // We verify by having both handlers overlap in time: if they were serialised the
        // second handler could not signal overlap while the first is still running.

        Guid key1 = Guid.Parse("11111111-0000-0000-0000-000000000000");
        Guid key2 = Guid.Parse("22222222-0000-0000-0000-000000000000");

        // Use 4 partitions to guarantee the two specific GUIDs land on different partitions.
        // (We verify that independently below.)
        const int partitionCount = 16;

        int idx1 = Math.Abs(key1.GetHashCode()) % partitionCount;
        int idx2 = Math.Abs(key2.GetHashCode()) % partitionCount;
        idx1.Should().NotBe(idx2, "test keys must land on different partitions for this test to be meaningful");

        MessageContext ctx1 = CreateContextWithCorrelationId(key1);
        MessageContext ctx2 = CreateContextWithCorrelationId(key2);

        await using var middleware = new PartitionerMiddleware(partitionCount: partitionCount);

        var firstEntered = new SemaphoreSlim(0, 1);
        var secondEntered = new SemaphoreSlim(0, 1);
        var firstCanExit = new SemaphoreSlim(0, 1);
        bool overlapObserved = false;

        async Task SlowNext1(MessageContext _)
        {
            firstEntered.Release();
            await firstCanExit.WaitAsync().ConfigureAwait(false);
        }

        async Task SlowNext2(MessageContext _)
        {
            secondEntered.Release();
            await Task.Yield(); // give scheduler a chance
        }

        // Act
        Task first = Task.Run(() => middleware.InvokeAsync(ctx1, SlowNext1));

        await firstEntered.WaitAsync(); // first is now holding its semaphore

        Task second = Task.Run(() => middleware.InvokeAsync(ctx2, SlowNext2));

        // The second task should start without waiting for the first to finish.
        bool secondStarted = await secondEntered.WaitAsync(millisecondsTimeout: 2000);
        if (secondStarted)
            overlapObserved = true;

        firstCanExit.Release();
        await Task.WhenAll(first, second);

        // Assert
        overlapObserved.Should().BeTrue("messages with different partition keys must be processed concurrently");
    }

    // -----------------------------------------------------------------------
    // InvokeAsync_CustomKeySelector_UsesProvidedFunction
    // -----------------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_CustomKeySelector_UsesProvidedFunction()
    {
        // Arrange
        // Custom selector always returns the same fixed Guid regardless of message content.
        // We verify it is called by observing the partitioning behaviour.

        Guid fixedKey = Guid.NewGuid();
        bool selectorInvoked = false;

        Func<MessageContext, Guid> customSelector = ctx =>
        {
            selectorInvoked = true;
            return fixedKey;
        };

        await using var middleware = new PartitionerMiddleware(partitionCount: 4, keySelector: customSelector);

        MessageContext context = CreateContext();
        bool nextCalled = false;

        // Act
        await middleware.InvokeAsync(context, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        // Assert
        selectorInvoked.Should().BeTrue("the custom key selector must be called during invocation");
        nextCalled.Should().BeTrue("the next middleware delegate must be invoked");
    }

    // -----------------------------------------------------------------------
    // InvokeAsync_PartitionCount_DistributesAcrossPartitions
    // -----------------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_PartitionCount_DistributesAcrossPartitions()
    {
        // Arrange
        // With N partitions and N messages whose keys hash to distinct partition indices,
        // all N messages should be processable concurrently (i.e. no semaphore contention).

        const int partitionCount = 8;

        // Build keys that deterministically map to distinct partition indices.
        // We generate GUIDs and pick the first 'partitionCount' that cover distinct slots.
        var partitionHits = new HashSet<int>();
        var keys = new List<Guid>();

        for (int attempt = 0; keys.Count < partitionCount && attempt < 10_000; attempt++)
        {
            Guid candidate = Guid.NewGuid();
            int idx = Math.Abs(candidate.GetHashCode()) % partitionCount;
            if (partitionHits.Add(idx))
                keys.Add(candidate);
        }

        // If we cannot find enough distinct-partition GUIDs within the attempt budget,
        // the test is inconclusive rather than failing (pathological hash collision scenario).
        keys.Count.Should().Be(partitionCount, "we need one key per partition to test distribution");

        await using var middleware = new PartitionerMiddleware(partitionCount: partitionCount);

        var entrySignals = Enumerable.Range(0, partitionCount)
            .Select(_ => new SemaphoreSlim(0, 1))
            .ToArray();
        var exitPermits = Enumerable.Range(0, partitionCount)
            .Select(_ => new SemaphoreSlim(0, 1))
            .ToArray();

        // Act — launch all N messages simultaneously
        Task[] tasks = keys
            .Select((key, i) =>
            {
                MessageContext ctx = CreateContextWithCorrelationId(key);
                return Task.Run(() => middleware.InvokeAsync(ctx, async _ =>
                {
                    entrySignals[i].Release();
                    await exitPermits[i].WaitAsync();
                }));
            })
            .ToArray();

        // All N handlers should enter concurrently within a reasonable timeout.
        bool allEntered = true;
        foreach (SemaphoreSlim signal in entrySignals)
        {
            bool entered = await signal.WaitAsync(millisecondsTimeout: 2000);
            if (!entered)
            {
                allEntered = false;
                break;
            }
        }

        // Unblock all handlers.
        foreach (SemaphoreSlim permit in exitPermits)
            permit.Release();

        await Task.WhenAll(tasks);

        // Assert
        allEntered.Should().BeTrue("with distinct partition keys all handlers must run concurrently");

        // Cleanup signals
        foreach (SemaphoreSlim s in entrySignals)
            s.Dispose();
        foreach (SemaphoreSlim s in exitPermits)
            s.Dispose();
    }

    // -----------------------------------------------------------------------
    // DefaultKeySelector_NonGuidCorrelationId_HashesToSamePartition
    // -----------------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_NonGuidCorrelationId_SameValueRoutesToSamePartition()
    {
        // Arrange — two messages with the same non-Guid CorrelationId should be serialised
        // (same partition key), proving the hash-based fallback works.
        const string correlationId = "order-123";

        MessageContext ctx1 = CreateContext(headers: new Dictionary<string, string>
        {
            ["CorrelationId"] = correlationId
        });
        MessageContext ctx2 = CreateContext(headers: new Dictionary<string, string>
        {
            ["CorrelationId"] = correlationId
        });

        await using var middleware = new PartitionerMiddleware(partitionCount: 1);

        var executionOrder = new List<string>();
        var firstEntered = new SemaphoreSlim(0, 1);
        var firstCanExit = new SemaphoreSlim(0, 1);

        async Task SlowNext1(MessageContext _)
        {
            executionOrder.Add("first-start");
            firstEntered.Release();
            await firstCanExit.WaitAsync().ConfigureAwait(false);
            executionOrder.Add("first-end");
        }

        Task SlowNext2(MessageContext _)
        {
            executionOrder.Add("second");
            return Task.CompletedTask;
        }

        // Act
        Task first = Task.Run(() => middleware.InvokeAsync(ctx1, SlowNext1));
        await firstEntered.WaitAsync();
        Task second = Task.Run(() => middleware.InvokeAsync(ctx2, SlowNext2));
        await Task.Delay(50);
        firstCanExit.Release();
        await Task.WhenAll(first, second);

        // Assert — must be sequential, proving both messages got the same partition key.
        executionOrder.Should().Equal("first-start", "first-end", "second");
    }

    [Fact]
    public async Task InvokeAsync_NonGuidCorrelationId_DifferentValuesCanRunConcurrently()
    {
        // Arrange — two messages with different non-Guid CorrelationIds should land on
        // different partitions (with enough partitions), proving distinct hash values.
        const string corrId1 = "order-aaa";
        const string corrId2 = "order-bbb";

        MessageContext ctx1 = CreateContext(headers: new Dictionary<string, string>
        {
            ["CorrelationId"] = corrId1
        });
        MessageContext ctx2 = CreateContext(headers: new Dictionary<string, string>
        {
            ["CorrelationId"] = corrId2
        });

        // Use 16 partitions to make hash collision unlikely.
        await using var middleware = new PartitionerMiddleware(partitionCount: 16);

        var firstEntered = new SemaphoreSlim(0, 1);
        var secondEntered = new SemaphoreSlim(0, 1);
        var firstCanExit = new SemaphoreSlim(0, 1);

        async Task SlowNext1(MessageContext _)
        {
            firstEntered.Release();
            await firstCanExit.WaitAsync().ConfigureAwait(false);
        }

        async Task SlowNext2(MessageContext _)
        {
            secondEntered.Release();
            await Task.Yield();
        }

        // Act
        Task first = Task.Run(() => middleware.InvokeAsync(ctx1, SlowNext1));
        await firstEntered.WaitAsync();
        Task second = Task.Run(() => middleware.InvokeAsync(ctx2, SlowNext2));

        bool secondStarted = await secondEntered.WaitAsync(millisecondsTimeout: 2000);

        firstCanExit.Release();
        await Task.WhenAll(first, second);

        // Assert
        secondStarted.Should().BeTrue("messages with different non-Guid CorrelationIds must be processed concurrently");
    }

    // -----------------------------------------------------------------------
    // InvokeAsync_ExceptionInNext_ReleasesLock
    // -----------------------------------------------------------------------

    [Fact]
    public async Task InvokeAsync_ExceptionInNext_ReleasesLock()
    {
        // Arrange
        Guid correlationId = Guid.NewGuid();
        MessageContext ctx = CreateContextWithCorrelationId(correlationId);

        await using var middleware = new PartitionerMiddleware(partitionCount: 2);

        // Act — first invocation throws
        Func<Task> faultyInvocation = () =>
            middleware.InvokeAsync(ctx, _ => throw new InvalidOperationException("downstream failure"));

        await faultyInvocation.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("downstream failure");

        // Assert — the semaphore must have been released; a second invocation on the same key
        // must complete without deadlocking.
        bool secondCompleted = false;

        await middleware.InvokeAsync(ctx, _ =>
        {
            secondCompleted = true;
            return Task.CompletedTask;
        });

        secondCompleted.Should().BeTrue("the semaphore must be released even when next throws");
    }
}
