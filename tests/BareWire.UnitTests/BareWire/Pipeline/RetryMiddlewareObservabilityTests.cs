using System.Buffers;
using AwesomeAssertions;
using BareWire.Abstractions.Observability;
using BareWire.Abstractions.Pipeline;
using BareWire.Pipeline;
using BareWire.Pipeline.Retry;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BareWire.UnitTests.Core.Pipeline;

/// <summary>
/// Verifies the retry observability contract: <see cref="RetryMiddleware"/> records a retry
/// attempt via <see cref="IBareWireInstrumentation.RecordRetryAttempt"/> once per retry, and
/// never touches instrumentation on the zero-allocation success path.
/// </summary>
public sealed class RetryMiddlewareObservabilityTests
{
    private const string MessageTypeTag = "OrderCreated";

    private static MessageContext CreateContext()
    {
        IServiceProvider serviceProvider = Substitute.For<IServiceProvider>();
        return new MessageContext(
            messageId: Guid.NewGuid(),
            headers: new Dictionary<string, string>(),
            rawBody: ReadOnlySequence<byte>.Empty,
            serviceProvider: serviceProvider,
            cancellationToken: default);
    }

    private static IntervalRetryPolicy CreatePolicy(int retryCount = 5) =>
        new(retryCount, TimeSpan.Zero, [], []);

    private static RetryMiddleware CreateSut(IBareWireInstrumentation instrumentation, int retryCount = 5) =>
        new(CreatePolicy(retryCount), NullLogger<RetryMiddleware>.Instance, instrumentation, MessageTypeTag);

    [Fact]
    public async Task InvokeAsync_WhenTwoTransientFailuresThenSucceeds_RecordsRetryAttemptPerRetry()
    {
        // Arrange
        var instrumentation = Substitute.For<IBareWireInstrumentation>();
        var sut = CreateSut(instrumentation);
        var context = CreateContext();
        int callCount = 0;

        // Act
        await sut.InvokeAsync(context, _ =>
        {
            callCount++;
            if (callCount < 3)
                throw new InvalidOperationException();
            return Task.CompletedTask;
        });

        // Assert — 2 retries -> 2 recorded retry attempts with the precompiled message-type tag.
        instrumentation.Received(2).RecordRetryAttempt(
            Arg.Any<string>(),
            MessageTypeTag,
            nameof(InvalidOperationException));
    }

    [Fact]
    public async Task InvokeAsync_WhenFirstAttemptSucceeds_DoesNotRecordRetryAttempt()
    {
        // Arrange — the 0-B/op success path must never invoke instrumentation.
        var instrumentation = Substitute.For<IBareWireInstrumentation>();
        var sut = CreateSut(instrumentation);
        var context = CreateContext();

        // Act
        await sut.InvokeAsync(context, _ => Task.CompletedTask);

        // Assert
        instrumentation.DidNotReceive().RecordRetryAttempt(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>());
    }

    [Fact]
    public async Task InvokeAsync_WhenRetriesExhausted_RecordsRetryAttemptForEachRetryThenRethrows()
    {
        // Arrange — always fails; with retryCount = 2 the policy grants 2 retries then rethrows.
        var instrumentation = Substitute.For<IBareWireInstrumentation>();
        var sut = CreateSut(instrumentation, retryCount: 2);
        var context = CreateContext();

        // Act
        Func<Task> act = () => sut.InvokeAsync(context, _ => throw new InvalidOperationException());

        // Assert — original exception propagates (settlement unchanged) and each retry was recorded.
        await act.Should().ThrowAsync<InvalidOperationException>();
        instrumentation.Received(2).RecordRetryAttempt(
            Arg.Any<string>(),
            MessageTypeTag,
            nameof(InvalidOperationException));
    }
}
