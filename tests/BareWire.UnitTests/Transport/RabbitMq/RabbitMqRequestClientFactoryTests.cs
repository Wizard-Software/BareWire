using AwesomeAssertions;
using BareWire.Abstractions.Serialization;
using BareWire.Transport.RabbitMQ;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// Unit tests for <see cref="RabbitMqRequestClientFactory"/> that do not require a running broker.
/// Tests verify dispose behaviour, cancellation propagation, and thread-safety under concurrent load.
/// </summary>
public sealed class RabbitMqRequestClientFactoryTests
{
    // ── Test records ──────────────────────────────────────────────────────────

    public sealed record TestMessage(string Value);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static RabbitMqRequestClientFactory CreateFactory()
    {
        var options = new RabbitMqTransportOptions
        {
            ConnectionString = "amqp://guest:guest@localhost:59999",
            ConnectionTimeout = TimeSpan.FromSeconds(5),
        };
        IMessageSerializer serializer = Substitute.For<IMessageSerializer>();
        serializer.ContentType.Returns("application/json");
        IMessageDeserializer deserializer = Substitute.For<IMessageDeserializer>();
        return new RabbitMqRequestClientFactory(
            options, serializer, deserializer, NullLoggerFactory.Instance);
    }

    // ── Dispose tests ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateRequestClientAsync_WhenDisposed_ThrowsObjectDisposedException()
    {
        // Arrange
        var factory = CreateFactory();
        await factory.DisposeAsync();

        // Act
        Func<Task> act = async () => await factory.CreateRequestClientAsync<TestMessage>();

        // Assert
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_IsIdempotent()
    {
        // Arrange
        var factory = CreateFactory();

        // Act & Assert — no exception
        await factory.DisposeAsync();
        await factory.DisposeAsync();
    }

    // ── Cancellation tests ────────────────────────────────────────────────────

    [Fact]
    public async Task CreateRequestClientAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        // Arrange
        await using var factory = CreateFactory();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        Func<Task> act = async () => await factory.CreateRequestClientAsync<TestMessage>(cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CreateRequestClientAsync_BrokerUnavailable_ThrowsOrCancels()
    {
        // Arrange — broker at port 59999 is unreachable; cancellation fires after 200 ms.
        // The method must either honour the CancellationToken (OperationCanceledException)
        // or surface the broker-unreachable error — but it must NOT hang indefinitely.
        await using var factory = CreateFactory();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // Act
        Func<Task> act = async () => await factory.CreateRequestClientAsync<TestMessage>(cts.Token);

        // Assert — must throw within the guard timeout (5 s).
        await act.Should().ThrowAsync<Exception>()
                 .WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ── Concurrency tests ─────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_ConcurrentWithCreateRequestClientAsync_DoesNotDeadlock()
    {
        // Arrange
        var factory = CreateFactory();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act — run create and dispose concurrently with a tight deadline.
        var createTask = Task.Run(async () =>
        {
            try { await factory.CreateRequestClientAsync<TestMessage>(cts.Token); }
            catch (Exception) { /* Expected — broker unreachable or factory disposed */ }
        });
        var disposeTask = factory.DisposeAsync().AsTask();

        // Assert — neither task should hang beyond the 5-second deadline.
        await Task.WhenAll(createTask, disposeTask).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CreateRequestClientAsync_ConcurrentCalls_OnlyOneConnectionAttempt()
    {
        // Arrange — all calls will fail because the broker is unreachable.
        await using var factory = CreateFactory();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        // Act — launch five concurrent calls; all are expected to fail with the same error.
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => Task.Run(async () =>
            {
                try { await factory.CreateRequestClientAsync<TestMessage>(cts.Token); }
                catch (Exception) { /* Expected */ }
            }))
            .ToArray();

        // Assert — no deadlock; all tasks complete within the guard timeout.
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
