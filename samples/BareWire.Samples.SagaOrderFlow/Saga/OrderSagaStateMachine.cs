using BareWire.Abstractions;
using BareWire.Abstractions.Saga;
using BareWire.Samples.SagaOrderFlow.Messages;

namespace BareWire.Samples.SagaOrderFlow.Saga;

/// <summary>
/// State machine that models the complete order lifecycle:
/// creation → payment → shipment → completion, with compensation on failure or timeout.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>
///     <c>OrderCreated</c> — received in <c>Initial</c>; captures order details,
///     schedules a 30-second payment timeout, and transitions to <c>Processing</c>.
///   </item>
///   <item>
///     <c>PaymentReceived</c> — received in <c>Processing</c>; cancels the timeout,
///     records the payment ID, and transitions to <c>Shipping</c>.
///   </item>
///   <item>
///     <c>ShipmentDispatched</c> — received in <c>Shipping</c>; records the tracking number
///     and transitions to <c>Completed</c> (finalized).
///   </item>
///   <item>
///     <c>PaymentFailed</c> — received in <c>Processing</c>; records the failure reason
///     and transitions to <c>Compensating</c>.
///   </item>
///   <item>
///     <c>PaymentTimeout</c> — scheduled after 30s in <c>Processing</c>; transitions to <c>Compensating</c>.
///   </item>
///   <item>
///     <c>CompensationCompleted</c> — received in <c>Compensating</c>; transitions to <c>Failed</c> (finalized).
///   </item>
/// </list>
/// </remarks>
public sealed class OrderSagaStateMachine : BareWireStateMachine<OrderSagaState>
{
    /// <summary>
    /// Initializes a new instance of <see cref="OrderSagaStateMachine"/> and defines
    /// all states, events, and transitions.
    /// </summary>
    public OrderSagaStateMachine()
    {
        // ── Events ──────────────────────────────────────────────────────────────
        var orderCreated = Event<OrderCreated>();
        var paymentReceived = Event<PaymentReceived>();
        var paymentFailed = Event<PaymentFailed>();
        var paymentTimeout = Event<PaymentTimeout>();
        var shipmentDispatched = Event<ShipmentDispatched>();
        var compensationCompleted = Event<CompensationCompleted>();

        // ── States ───────────────────────────────────────────────────────────────
        var processing = State("Processing");
        var shipping = State("Shipping");
        var compensating = State("Compensating");
        var completed = State("Completed");
        var failed = State("Failed");

        // ── Scheduled timeout: 30s payment window ────────────────────────────────
        var paymentTimeoutSchedule = Schedule<PaymentTimeout>(cfg =>
        {
            cfg.Delay = TimeSpan.FromSeconds(30);
            cfg.Strategy = SchedulingStrategy.Auto;
        });

        // ── Correlation ──────────────────────────────────────────────────────────
        // All events correlate via OrderId parsed as a Guid.
        CorrelateBy<OrderCreated>(e => ParseOrderId(e.OrderId));
        CorrelateBy<PaymentReceived>(e => ParseOrderId(e.OrderId));
        CorrelateBy<PaymentFailed>(e => ParseOrderId(e.OrderId));
        CorrelateBy<PaymentTimeout>(e => ParseOrderId(e.OrderId));
        CorrelateBy<ShipmentDispatched>(e => ParseOrderId(e.OrderId));
        CorrelateBy<CompensationCompleted>(e => ParseOrderId(e.OrderId));

        // ── Transitions ──────────────────────────────────────────────────────────

        Initially(() =>
        {
            // Initial → OrderCreated → Processing (schedule 30s payment timeout)
            When(orderCreated, b => b
                .Then((saga, evt) =>
                {
                    saga.OrderId = evt.OrderId;
                    saga.Amount = evt.Amount;
                    saga.ShippingAddress = evt.ShippingAddress;
                    saga.CreatedAt = DateTimeOffset.UtcNow;
                    saga.UpdatedAt = DateTimeOffset.UtcNow;
                    return Task.CompletedTask;
                })
                .ScheduleTimeout<PaymentTimeout>((saga, evt) => new PaymentTimeout(evt.OrderId))
                .TransitionTo(processing.Name));
        });

        During(processing, () =>
        {
            // Processing → PaymentReceived → Shipping (cancel timeout)
            When(paymentReceived, b => b
                .Then((saga, evt) =>
                {
                    saga.PaymentId = evt.PaymentId;
                    saga.UpdatedAt = DateTimeOffset.UtcNow;
                    return Task.CompletedTask;
                })
                .CancelTimeout<PaymentTimeout>()
                .TransitionTo(shipping.Name));

            // Processing → PaymentFailed → Compensating (cancel timeout)
            When(paymentFailed, b => b
                .Then((saga, evt) =>
                {
                    saga.FailureReason = evt.Reason;
                    saga.UpdatedAt = DateTimeOffset.UtcNow;
                    return Task.CompletedTask;
                })
                .CancelTimeout<PaymentTimeout>()
                .TransitionTo(compensating.Name));

            // Processing → PaymentTimeout → Compensating (timeout fired after 30s)
            When(paymentTimeout, b => b
                .Then((saga, evt) =>
                {
                    saga.FailureReason = "Payment timeout — no payment received within 30 seconds.";
                    saga.UpdatedAt = DateTimeOffset.UtcNow;
                    return Task.CompletedTask;
                })
                .TransitionTo(compensating.Name));
        });

        During(shipping, () =>
        {
            // Shipping → ShipmentDispatched → Completed (finalize)
            When(shipmentDispatched, b => b
                .Then((saga, evt) =>
                {
                    saga.TrackingNumber = evt.TrackingNumber;
                    saga.UpdatedAt = DateTimeOffset.UtcNow;
                    return Task.CompletedTask;
                })
                .TransitionTo(completed.Name)
                .Finalize());
        });

        During(compensating, () =>
        {
            // Compensating → CompensationCompleted → Failed (finalize)
            When(compensationCompleted, b => b
                .Then((saga, evt) =>
                {
                    saga.UpdatedAt = DateTimeOffset.UtcNow;
                    return Task.CompletedTask;
                })
                .TransitionTo(failed.Name)
                .Finalize());
        });
    }

    /// <summary>
    /// Converts a string order identifier to a <see cref="Guid"/> for saga correlation.
    /// The sample API produces order IDs as <c>Guid.NewGuid().ToString()</c>, so the parse
    /// is expected to succeed. Falls back to a deterministic hash-based UUID for human-readable IDs.
    /// </summary>
    private static Guid ParseOrderId(string orderId)
    {
        if (Guid.TryParse(orderId, out Guid result))
        {
            return result;
        }

        // Deterministic fallback: derive a stable Guid from the order ID string hash.
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(orderId));

        return new Guid(hash[..16]);
    }
}
