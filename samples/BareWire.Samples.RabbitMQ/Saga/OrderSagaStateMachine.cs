using BareWire.Abstractions.Saga;
using BareWire.Samples.RabbitMQ.Messages;

namespace BareWire.Samples.RabbitMQ.Saga;

/// <summary>
/// State machine that models the order payment lifecycle:
/// <c>Initial</c> → <c>Processing</c> → <c>Completed</c> or <c>Failed</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>
///     <c>OrderCreated</c> — received in <c>Initial</c>; captures order details and
///     transitions the saga to <c>Processing</c>.
///   </item>
///   <item>
///     <c>PaymentReceived</c> — received in <c>Processing</c>; transitions to <c>Completed</c>
///     and finalizes the saga instance.
///   </item>
///   <item>
///     <c>PaymentFailed</c> — received in <c>Processing</c>; transitions to <c>Failed</c>
///     and finalizes the saga instance.
///   </item>
/// </list>
/// </remarks>
public sealed class OrderSagaStateMachine : BareWireStateMachine<OrderSagaState>
{
    /// <summary>
    /// Initializes a new instance of <see cref="OrderSagaStateMachine"/> and defines
    /// all states and transitions.
    /// </summary>
    public OrderSagaStateMachine()
    {
        var orderCreated = Event<OrderCreated>();
        var paymentReceived = Event<PaymentReceived>();
        var paymentFailed = Event<PaymentFailed>();

        var processing = State("Processing");
        var completed = State("Completed");
        var failed = State("Failed");

        // CorrelateBy extracts the CorrelationId (Guid) from each event type.
        // OrderId strings are parsed as Guids; the sample API generates them as Guid.NewGuid().ToString().
        CorrelateBy<OrderCreated>(e => ParseOrderId(e.OrderId));
        CorrelateBy<PaymentReceived>(e => ParseOrderId(e.OrderId));
        CorrelateBy<PaymentFailed>(e => ParseOrderId(e.OrderId));

        Initially(() =>
        {
            When(orderCreated, b => b
                .Then((saga, evt) =>
                {
                    saga.OrderNumber = evt.OrderId;
                    saga.Amount = evt.Amount;
                    saga.Currency = evt.Currency;
                    return Task.CompletedTask;
                })
                .TransitionTo(processing.Name));
        });

        During(processing, () =>
        {
            When(paymentReceived, b => b
                .TransitionTo(completed.Name)
                .Finalize());

            When(paymentFailed, b => b
                .TransitionTo(failed.Name)
                .Finalize());
        });
    }

    /// <summary>
    /// Converts a string order identifier to a <see cref="Guid"/> for saga correlation.
    /// The sample API produces order IDs as <c>Guid.NewGuid().ToString()</c>, so the parse
    /// is expected to succeed. Falls back to a deterministic hash-based UUID when the string
    /// is not a well-formed Guid (e.g. a human-readable order number like <c>"ORD-0042"</c>).
    /// </summary>
    private static Guid ParseOrderId(string orderId)
    {
        if (Guid.TryParse(orderId, out Guid result))
        {
            return result;
        }

        // Deterministic fallback: derive a stable Guid from the order ID string hash
        // so that the same string always maps to the same correlation ID.
        // SHA256 is deterministic; we take the first 16 bytes as the UUID bytes.
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(orderId));

        return new Guid(hash[..16]);
    }
}
