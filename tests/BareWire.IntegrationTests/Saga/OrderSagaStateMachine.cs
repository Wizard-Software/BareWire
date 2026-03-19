using BareWire.Abstractions.Saga;

namespace BareWire.IntegrationTests.Saga;

/// <summary>
/// End-to-end test state machine that models a simple order lifecycle:
/// <c>Initial</c> → <c>Processing</c> → <c>Completed</c> or <c>Failed</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><c>OrderCreated</c> — received in <c>Initial</c>, captures order details, transitions to <c>Processing</c>.</item>
///   <item><c>PaymentReceived</c> — received in <c>Processing</c>, publishes <see cref="OrderCompleted"/>, finalizes the saga.</item>
///   <item><c>PaymentFailed</c> — received in <c>Processing</c>, transitions to <c>Failed</c>, finalizes the saga.</item>
/// </list>
/// </remarks>
public sealed class OrderSagaStateMachine : BareWireStateMachine<OrderSagaState>
{
    /// <summary>Initializes a new instance of <see cref="OrderSagaStateMachine"/> and defines all states and transitions.</summary>
    public OrderSagaStateMachine()
    {
        var orderCreated = Event<OrderCreated>();
        var paymentReceived = Event<PaymentReceived>();
        var paymentFailed = Event<PaymentFailed>();

        var processing = State("Processing");
        var completed = State("Completed");
        var failed = State("Failed");

        CorrelateBy<OrderCreated>(e => e.OrderId);
        CorrelateBy<PaymentReceived>(e => e.OrderId);
        CorrelateBy<PaymentFailed>(e => e.OrderId);

        Initially(() =>
        {
            When(orderCreated, b => b
                .Then((saga, evt) =>
                {
                    saga.OrderNumber = evt.OrderNumber;
                    saga.Amount = evt.Amount;
                    return Task.CompletedTask;
                })
                .TransitionTo(processing.Name));
        });

        During(processing, () =>
        {
            When(paymentReceived, b => b
                .Publish((saga, _) => new OrderCompleted(saga.CorrelationId))
                .TransitionTo(completed.Name)
                .Finalize());

            When(paymentFailed, b => b
                .TransitionTo(failed.Name)
                .Finalize());
        });
    }
}
