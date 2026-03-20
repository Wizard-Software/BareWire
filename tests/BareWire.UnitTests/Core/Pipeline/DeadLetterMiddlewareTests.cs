using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions.Pipeline;
using BareWire.Core.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Core.Pipeline;

public sealed class DeadLetterMiddlewareTests
{
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

    [Fact]
    public async Task InvokeAsync_SuccessfulProcessing_NoDeadLetter()
    {
        // Arrange
        bool deadLetterCalled = false;
        var sut = new DeadLetterMiddleware(
            (_, _) =>
            {
                deadLetterCalled = true;
                return Task.CompletedTask;
            },
            NullLogger<DeadLetterMiddleware>.Instance);
        var context = CreateContext();

        // Act
        await sut.InvokeAsync(context, _ => Task.CompletedTask);

        // Assert
        deadLetterCalled.Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_ExceptionAfterRetry_RoutesToDeadLetterThenRethrows()
    {
        // Arrange
        bool deadLetterCalled = false;
        var sut = new DeadLetterMiddleware(
            (_, _) =>
            {
                deadLetterCalled = true;
                return Task.CompletedTask;
            },
            NullLogger<DeadLetterMiddleware>.Instance);
        var context = CreateContext();

        // Act — simulate exception after retry exhaustion
        Func<Task> act = () => sut.InvokeAsync(context, _ =>
            throw new InvalidOperationException("retries exhausted"));

        // Assert — callback fires, then exception propagates so ReceiveEndpointRunner can NACK
        await act.Should().ThrowAsync<InvalidOperationException>();
        deadLetterCalled.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_OnDeadLetterCallback_InvokedWithContextAndException()
    {
        // Arrange
        MessageContext? capturedContext = null;
        Exception? capturedException = null;
        var sut = new DeadLetterMiddleware(
            (ctx, ex) =>
            {
                capturedContext = ctx;
                capturedException = ex;
                return Task.CompletedTask;
            },
            NullLogger<DeadLetterMiddleware>.Instance);
        var context = CreateContext();
        var expectedException = new InvalidOperationException("test error");

        // Act — DeadLetterMiddleware re-throws after invoking the callback
        Func<Task> act = () => sut.InvokeAsync(context, _ => throw expectedException);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Assert
        capturedContext.Should().BeSameAs(context);
        capturedException.Should().BeSameAs(expectedException);
    }

    [Fact]
    public async Task InvokeAsync_ExceptionThrown_RethrowsAfterCallback()
    {
        // Arrange — DeadLetterMiddleware re-throws so ReceiveEndpointRunner can NACK.
        // The broker then routes to the DLX via x-dead-letter-exchange.
        var thrownException = new InvalidOperationException("failure");
        var sut = new DeadLetterMiddleware(
            (_, _) => Task.CompletedTask,
            NullLogger<DeadLetterMiddleware>.Instance);
        var context = CreateContext();

        // Act
        Func<Task> act = () => sut.InvokeAsync(context, _ => throw thrownException);

        // Assert — exception propagates so the transport layer can NACK
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("failure");
    }
}
