using BareWire.Abstractions.Saga;
using BareWire.Samples.ObservabilityShowcase.Messages;

namespace BareWire.Samples.ObservabilityShowcase.Saga;

/// <summary>
/// State machine that models the demo observability pipeline:
/// <c>Initial</c> → <c>Processing</c> → <c>Completed</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>
///     <c>DemoOrderCreated</c> — received in <c>Initial</c>; captures order details
///     and transitions to <c>Processing</c>.
///   </item>
///   <item>
///     <c>DemoPaymentProcessed</c> — received in <c>Processing</c>; transitions to
///     <c>Completed</c> and finalizes the saga instance.
///   </item>
/// </list>
/// This simplified three-state machine is intentional — the purpose of this sample is to
/// demonstrate distributed tracing across saga transitions, not saga complexity.
/// </remarks>
public sealed class DemoSagaStateMachine : BareWireStateMachine<DemoSagaState>
{
    /// <summary>
    /// Initializes a new instance of <see cref="DemoSagaStateMachine"/> and defines
    /// all states and transitions.
    /// </summary>
    public DemoSagaStateMachine()
    {
        var orderCreated = Event<DemoOrderCreated>();
        var paymentProcessed = Event<DemoPaymentProcessed>();

        var processing = State("Processing");
        var completed = State("Completed");

        // Correlate each event to the saga instance via the OrderId parsed as a Guid.
        // The sample API generates OrderIds as Guid.NewGuid().ToString(), so the parse
        // always succeeds for orders created by POST /demo/run.
        CorrelateBy<DemoOrderCreated>(e => ParseOrderId(e.OrderId));
        CorrelateBy<DemoPaymentProcessed>(e => ParseOrderId(e.OrderId));

        Initially(() =>
        {
            When(orderCreated, b => b
                .Then((saga, evt) =>
                {
                    saga.OrderId = evt.OrderId;
                    saga.Amount = evt.Amount;
                    saga.CreatedAt = evt.CreatedAt;
                    saga.UpdatedAt = DateTime.UtcNow;
                    return Task.CompletedTask;
                })
                .TransitionTo(processing.Name));
        });

        During(processing, () =>
        {
            When(paymentProcessed, b => b
                .Then((saga, _) =>
                {
                    saga.UpdatedAt = DateTime.UtcNow;
                    return Task.CompletedTask;
                })
                .TransitionTo(completed.Name)
                .Finalize());
        });
    }

    /// <summary>
    /// Converts a string order identifier to a <see cref="Guid"/> for saga correlation.
    /// Falls back to a deterministic hash-based UUID for non-Guid order identifiers.
    /// </summary>
    private static Guid ParseOrderId(string orderId)
    {
        if (Guid.TryParse(orderId, out Guid result))
        {
            return result;
        }

        // Deterministic fallback: derive a stable Guid from the order ID string hash
        // so that the same string always maps to the same correlation ID.
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(orderId));

        return new Guid(hash[..16]);
    }
}
