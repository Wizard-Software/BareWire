using System.Buffers;
using BareWire.Abstractions;
using BareWire.Abstractions.Saga;
using NSubstitute;

namespace BareWire.UnitTests.Saga;

// ── Shared saga state types ───────────────────────────────────────────────────
// These must be public so that NSubstitute can proxy ISagaRepository<T>.

/// <summary>Saga state for StateMachineDefinition tests.</summary>
public sealed class OrderSagaState : ISagaState
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = "Initial";
    public int Version { get; set; }
}

/// <summary>Saga state for StateMachineExecutor tests.</summary>
public sealed class ExecutorSagaState : ISagaState
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = "Initial";
    public int Version { get; set; }
}

/// <summary>Saga state for SagaEventRouter tests.</summary>
public sealed class RouterSagaState : ISagaState
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = "Initial";
    public int Version { get; set; }
}

/// <summary>Saga state for CorrelationProvider tests.</summary>
public sealed class CorrelationSagaState : ISagaState
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = "Initial";
    public int Version { get; set; }
}

// ── Shared event/message types ────────────────────────────────────────────────

public sealed record OrderCreated(Guid OrderId, string Customer);
public sealed record OrderCompleted(Guid OrderId);
public sealed record OrderCancelled(Guid OrderId);

// ── ConsumeContext helper ─────────────────────────────────────────────────────

/// <summary>
/// A concrete subclass of <see cref="ConsumeContext"/> that exposes the internal constructor
/// for saga unit testing purposes.
/// </summary>
internal sealed class SagaTestConsumeContext(
    Guid messageId,
    Guid? correlationId,
    Guid? conversationId,
    Uri? sourceAddress,
    Uri? destinationAddress,
    DateTimeOffset? sentTime,
    IReadOnlyDictionary<string, string> headers,
    string? contentType,
    ReadOnlySequence<byte> rawBody,
    IPublishEndpoint publishEndpoint,
    ISendEndpointProvider sendEndpointProvider,
    CancellationToken cancellationToken = default)
    : ConsumeContext(messageId, correlationId, conversationId, sourceAddress, destinationAddress,
                     sentTime, headers, contentType, rawBody, publishEndpoint, sendEndpointProvider,
                     cancellationToken);

internal static class SagaTestHelpers
{
    internal static ConsumeContext CreateConsumeContext(
        Guid? messageId = null,
        IPublishEndpoint? publishEndpoint = null,
        ISendEndpointProvider? sendEndpointProvider = null)
    {
        return new SagaTestConsumeContext(
            messageId ?? Guid.NewGuid(),
            correlationId: null,
            conversationId: null,
            sourceAddress: null,
            destinationAddress: null,
            sentTime: null,
            headers: new Dictionary<string, string>(),
            contentType: null,
            rawBody: ReadOnlySequence<byte>.Empty,
            publishEndpoint: publishEndpoint ?? Substitute.For<IPublishEndpoint>(),
            sendEndpointProvider: sendEndpointProvider ?? Substitute.For<ISendEndpointProvider>());
    }
}
