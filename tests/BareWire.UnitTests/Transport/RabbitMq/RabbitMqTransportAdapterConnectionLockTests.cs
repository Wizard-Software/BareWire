#pragma warning disable CA2012 // NSubstitute .Returns() on ValueTask is a known false positive
using System.Reflection;
using AwesomeAssertions;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// Verifies that disposing the adapter while callers hold or wait for the connection lock neither leaks the
/// connection nor surfaces an exception from the disposed lock, and that connecting keeps its behaviour otherwise.
/// </summary>
public sealed class RabbitMqTransportAdapterConnectionLockTests
{
    // Upper bound for an assertion that must not hang; never a fixed delay.
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    private static RabbitMqTransportAdapter CreateAdapter() =>
        new(
            new RabbitMqTransportOptions { ConnectionString = "amqp://guest:guest@localhost:5672/" },
            NullLogger<RabbitMqTransportAdapter>.Instance);

    // EnsureConnectedAsync is private; the call is made through reflection, like the private fields in the
    // neighbouring adapter tests. The method runs synchronously up to its first await.
    private static Task EnsureConnectedAsync(RabbitMqTransportAdapter adapter, CancellationToken cancellationToken) =>
        (Task)typeof(RabbitMqTransportAdapter)
            .GetMethod("EnsureConnectedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(adapter, [cancellationToken])!;

    private static IConnection CreateOpenConnection()
    {
        IConnection connection = Substitute.For<IConnection>();
        connection.IsOpen.Returns(true);
        connection.Endpoint.Returns(new AmqpTcpEndpoint("localhost", 5672));
        connection.CloseAsync(
                Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        connection.DisposeAsync().Returns(callInfo => ValueTask.CompletedTask);
        return connection;
    }

    private static async Task AssertClosedAndDisposedOnceAsync(IConnection connection)
    {
        await connection.Received(1).CloseAsync(
            Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await connection.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task EnsureConnectedAsync_WhenDisposedWhileCreatingConnection_ThrowsAdapterObjectDisposedExceptionAndClosesConnection()
    {
        // Arrange — the factory ignores the token and returns only when the test releases it.
        RabbitMqTransportAdapter adapter = CreateAdapter();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.CreateConnection = (_, _) =>
        {
            started.TrySetResult();
            return release.Task;
        };
        IConnection connection = CreateOpenConnection();

        Task connecting = EnsureConnectedAsync(adapter, CancellationToken.None);
        await started.Task.WaitAsync(Guard);

        // Act — dispose while the caller holds the lock, then let the connection arrive.
        await adapter.DisposeAsync();
        release.SetResult(connection);

        // Assert
        ObjectDisposedException ex = await Assert.ThrowsAsync<ObjectDisposedException>(
            () => connecting.WaitAsync(Guard));
        ex.ObjectName.Should().Be(typeof(RabbitMqTransportAdapter).FullName);
        await AssertClosedAndDisposedOnceAsync(connection);
    }

    [Fact]
    public async Task EnsureConnectedAsync_WhenWaitingForLockDuringDispose_ThrowsAdapterObjectDisposedException()
    {
        // Arrange — the first caller holds the lock inside the factory; the second waits with an uncancellable token.
        RabbitMqTransportAdapter adapter = CreateAdapter();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.CreateConnection = (_, _) =>
        {
            started.TrySetResult();
            return release.Task;
        };
        IConnection connection = CreateOpenConnection();

        Task first = EnsureConnectedAsync(adapter, CancellationToken.None);
        await started.Task.WaitAsync(Guard);
        Task second = EnsureConnectedAsync(adapter, CancellationToken.None);
        second.IsCompleted.Should().BeFalse("the lock is held by the first caller");

        // Act
        await adapter.DisposeAsync();

        // Assert — the waiter is woken by the dispose, although the lock holder has not returned yet.
        ObjectDisposedException ex = await Assert.ThrowsAsync<ObjectDisposedException>(
            () => second.WaitAsync(Guard));
        ex.ObjectName.Should().Be(typeof(RabbitMqTransportAdapter).FullName);

        // Cleanup — release the first caller; it closes the connection it created and faults with the same exception.
        release.SetResult(connection);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.WaitAsync(Guard));
        await AssertClosedAndDisposedOnceAsync(connection);
    }

    [Fact]
    public async Task EnsureConnectedAsync_WhenCallerTokenCancelledWhileConnecting_ThrowsOperationCanceledException()
    {
        // Arrange — the factory honours the token it is given and otherwise never completes.
        RabbitMqTransportAdapter adapter = CreateAdapter();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.CreateConnection = async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await new TaskCompletionSource<IConnection>().Task.WaitAsync(cancellationToken);
            return null!;
        };
        using var callerCts = new CancellationTokenSource();

        Task connecting = EnsureConnectedAsync(adapter, callerCts.Token);
        await started.Task.WaitAsync(Guard);

        // Act
        await callerCts.CancelAsync();

        // Assert — the caller's own cancellation is not turned into an ObjectDisposedException.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting.WaitAsync(Guard));

        // The adapter is still usable: a following attempt reaches the factory again.
        IConnection connection = CreateOpenConnection();
        adapter.CreateConnection = (_, _) => Task.FromResult(connection);
        await EnsureConnectedAsync(adapter, CancellationToken.None).WaitAsync(Guard);

        await adapter.DisposeAsync();
        await AssertClosedAndDisposedOnceAsync(connection);
    }

    [Fact]
    public async Task EnsureConnectedAsync_WhenCalledConcurrentlyWithoutDispose_CreatesConnectionOnce()
    {
        // Arrange — both callers are in EnsureConnectedAsync before the factory returns.
        RabbitMqTransportAdapter adapter = CreateAdapter();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        int created = 0;
        adapter.CreateConnection = (_, _) =>
        {
            Interlocked.Increment(ref created);
            started.TrySetResult();
            return release.Task;
        };
        IConnection connection = CreateOpenConnection();

        Task first = EnsureConnectedAsync(adapter, CancellationToken.None);
        await started.Task.WaitAsync(Guard);
        Task second = EnsureConnectedAsync(adapter, CancellationToken.None);

        // Assert — the second caller is blocked on the lock, not creating a second connection.
        second.IsCompleted.Should().BeFalse("the first caller still holds the connection lock");
        Volatile.Read(ref created).Should().Be(1);

        // Act
        release.SetResult(connection);
        await Task.WhenAll(first, second).WaitAsync(Guard);

        // Assert
        Volatile.Read(ref created).Should().Be(1, "the second caller must reuse the connection of the first");

        await adapter.DisposeAsync();
        await AssertClosedAndDisposedOnceAsync(connection);
    }
}
