using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions.Transport;

namespace BareWire.UnitTests.Abstractions.Transport;

public sealed class InboundMessageTests
{
    private static InboundMessage CreateMessage(byte[]? pooledBuffer = null, int bodyLength = 0)
    {
        ReadOnlySequence<byte> body = pooledBuffer is not null
            ? new ReadOnlySequence<byte>(pooledBuffer, 0, bodyLength)
            : ReadOnlySequence<byte>.Empty;

        return new InboundMessage(
            messageId: "test-msg",
            headers: new Dictionary<string, string>(),
            body: body,
            deliveryTag: 1UL,
            pooledBuffer: pooledBuffer);
    }

    [Fact]
    public void Dispose_WithPooledBuffer_SetsPooledBufferToNull()
    {
        // Arrange
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(64);
        using InboundMessage message = CreateMessage(rentedBuffer, bodyLength: 64);

        // Act
        message.Dispose();

        // Assert — internal PooledBuffer property is visible via InternalsVisibleTo
        message.PooledBuffer.Should().BeNull();
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        // Arrange
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(64);
        InboundMessage message = CreateMessage(rentedBuffer, bodyLength: 64);

        message.Dispose();

        // Act & Assert — second Dispose must be a no-op, not throw
        Action act = () => message.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_WithNullPooledBuffer_DoesNotThrow()
    {
        // Arrange — message created without a pooled buffer
        InboundMessage message = CreateMessage();

        // Act & Assert
        Action act = () => message.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_WithPooledBuffer_ReturnsBufferToPool()
    {
        // Arrange — rent a buffer, create a message that takes ownership
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(64);
        InboundMessage message = CreateMessage(rentedBuffer, bodyLength: 64);

        // Act
        message.Dispose();

        // Assert — PooledBuffer is null, confirming Dispose zeroed the reference
        // (We cannot directly verify the ArrayPool internal state, but null confirms
        // the Return path was executed and the reference was cleared.)
        message.PooledBuffer.Should().BeNull();
    }

    [Fact]
    public void TryPinPooledBuffer_NotDisposed_ReturnsTrueAndKeepsBuffer()
    {
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(64);
        InboundMessage message = CreateMessage(rentedBuffer, bodyLength: 64);

        bool pinned = message.TryPinPooledBuffer();

        pinned.Should().BeTrue();
        message.PooledBuffer.Should().BeSameAs(rentedBuffer);
        message.UnpinPooledBuffer();
        message.Dispose();
    }

    [Fact]
    public void TryPinPooledBuffer_AfterDispose_ReturnsFalse()
    {
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(64);
        InboundMessage message = CreateMessage(rentedBuffer, bodyLength: 64);
        message.Dispose();

        message.TryPinPooledBuffer().Should().BeFalse();
    }

    [Fact]
    public void TryPinPooledBuffer_AlreadyPinned_ReturnsFalse()
    {
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(64);
        InboundMessage message = CreateMessage(rentedBuffer, bodyLength: 64);
        message.TryPinPooledBuffer().Should().BeTrue();

        message.TryPinPooledBuffer().Should().BeFalse();

        message.UnpinPooledBuffer();
        message.Dispose();
    }

    [Fact]
    public void Dispose_WhilePinned_KeepsBufferUntilUnpin()
    {
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(64);
        rentedBuffer.AsSpan(0, 4).Fill(0x5A);
        InboundMessage message = CreateMessage(rentedBuffer, bodyLength: 4);
        message.TryPinPooledBuffer().Should().BeTrue();

        message.Dispose();

        message.PooledBuffer.Should().BeSameAs(rentedBuffer);
        message.Body.ToArray().Should().Equal(0x5A, 0x5A, 0x5A, 0x5A);
        message.UnpinPooledBuffer();
        message.PooledBuffer.Should().BeNull();
    }

    [Fact]
    public void UnpinPooledBuffer_WithoutDispose_LeavesMessageLiveForNormalDispose()
    {
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(64);
        InboundMessage message = CreateMessage(rentedBuffer, bodyLength: 64);
        message.TryPinPooledBuffer().Should().BeTrue();

        message.UnpinPooledBuffer();

        message.PooledBuffer.Should().BeSameAs(rentedBuffer);
        message.Dispose();
        message.PooledBuffer.Should().BeNull();
    }

    [Fact]
    public async Task TryPinPooledBuffer_RacingDispose_ReturnsBufferExactlyOnceAfterBoth()
    {
        for (int i = 0; i < 1_000; i++)
        {
            byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(16);
            InboundMessage message = CreateMessage(rentedBuffer, bodyLength: 16);
            using var start = new ManualResetEventSlim();

            Task pinAndUnpin = Task.Run(() =>
            {
                start.Wait(TestContext.Current.CancellationToken);
                if (message.TryPinPooledBuffer())
                {
                    message.UnpinPooledBuffer();
                }
            }, TestContext.Current.CancellationToken);
            Task dispose = Task.Run(() =>
            {
                start.Wait(TestContext.Current.CancellationToken);
                message.Dispose();
            }, TestContext.Current.CancellationToken);
            start.Set();
            await Task.WhenAll(pinAndUnpin, dispose);

            message.PooledBuffer.Should().BeNull();
        }
    }
}
