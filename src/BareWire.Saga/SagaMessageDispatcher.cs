using System.Buffers;
using System.Reflection;
using BareWire.Abstractions;
using BareWire.Abstractions.Saga;
using BareWire.Abstractions.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BareWire.Saga;

/// <summary>
/// Dispatches raw inbound messages to a saga state machine by trying to deserialize
/// the body as each event type registered via <c>CorrelateBy&lt;T&gt;</c>.
/// Creates a DI scope per message to properly resolve scoped <see cref="ISagaRepository{TSaga}"/>.
/// </summary>
/// <typeparam name="TStateMachine">The state machine type (the class deriving from <see cref="BareWireStateMachine{TSaga}"/>).</typeparam>
/// <typeparam name="TSaga">The saga state type managed by the state machine.</typeparam>
internal sealed partial class SagaMessageDispatcher<TStateMachine, TSaga> : ISagaMessageDispatcher
    where TStateMachine : BareWireStateMachine<TSaga>
    where TSaga : class, ISagaState, new()
{
    // Type-erased delegate: (scopeFactory, definition, loggerFactory, body, headers, msgId, pub, send, deser, ct) -> Task<bool>
    private delegate Task<bool> TryDispatchEventDelegate(
        IServiceScopeFactory scopeFactory,
        StateMachineDefinition<TSaga> definition,
        ILoggerFactory loggerFactory,
        ReadOnlySequence<byte> body,
        IReadOnlyDictionary<string, string> headers,
        string messageId,
        IPublishEndpoint publishEndpoint,
        ISendEndpointProvider sendEndpointProvider,
        IMessageDeserializer deserializer,
        CancellationToken cancellationToken);

    private static readonly MethodInfo BuildEventDelegateMethod =
        typeof(SagaMessageDispatcher<TStateMachine, TSaga>)
            .GetMethod(nameof(BuildEventDelegate), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly TryDispatchEventDelegate[] _eventDispatchers;
    private readonly StateMachineDefinition<TSaga> _definition;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SagaMessageDispatcher<TStateMachine, TSaga>> _logger;

    /// <inheritdoc />
    public Type StateMachineType => typeof(TStateMachine);

    internal SagaMessageDispatcher(
        StateMachineDefinition<TSaga> definition,
        BareWireStateMachine<TSaga> stateMachine,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(stateMachine);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _definition = definition;
        _scopeFactory = scopeFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SagaMessageDispatcher<TStateMachine, TSaga>>();

        // Collect all event types the saga knows about.
        // The Correlations dictionary keyset is the authoritative source: only event types
        // for which CorrelateBy<T>() was called can be reliably correlated to a saga instance.
        IReadOnlyList<Type> eventTypes = [.. stateMachine.Correlations.Keys];

        // Build one type-erased dispatch delegate per event type at startup —
        // no reflection in the hot path.
        _eventDispatchers = eventTypes
            .Select(eventType => (TryDispatchEventDelegate)BuildEventDelegateMethod
                .MakeGenericMethod(eventType)
                .Invoke(null, null)!)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<bool> TryDispatchAsync(
        ReadOnlySequence<byte> body,
        IReadOnlyDictionary<string, string> headers,
        string messageId,
        IPublishEndpoint publishEndpoint,
        ISendEndpointProvider sendEndpointProvider,
        IMessageDeserializer deserializer,
        CancellationToken cancellationToken = default)
    {
        foreach (TryDispatchEventDelegate dispatch in _eventDispatchers)
        {
            bool handled = await dispatch(
                _scopeFactory, _definition, _loggerFactory,
                body, headers, messageId,
                publishEndpoint, sendEndpointProvider,
                deserializer, cancellationToken).ConfigureAwait(false);

            if (handled)
                return true;
        }

        LogNoEventMatched(messageId, typeof(TStateMachine).Name);
        return false;
    }

    private static TryDispatchEventDelegate BuildEventDelegate<TEvent>()
        where TEvent : class
    {
        return async (scopeFactory, definition, loggerFactory, body, headers, messageId, pub, send, deser, ct) =>
        {
            TEvent? evt = deser.Deserialize<TEvent>(body);
            if (evt is null)
                return false;

            Guid id = Guid.TryParse(messageId, out Guid parsed) ? parsed : Guid.NewGuid();
            Guid? correlationId = TryParseGuidHeader(headers, "correlation-id");
            Guid? conversationId = TryParseGuidHeader(headers, "conversation-id");
            headers.TryGetValue("content-type", out string? contentType);

            // Build a ConsumeContext to pass to the executor so sagas can publish/send side-effects.
            SagaDispatchConsumeContext context = new(
                id, correlationId, conversationId,
                null, null, null,
                headers, contentType, body,
                pub, send, ct);

            // Create a scope per message so ISagaRepository<TSaga> (scoped via EF Core DbContext)
            // gets a fresh instance and is properly disposed after processing.
            using IServiceScope scope = scopeFactory.CreateScope();
            ISagaRepository<TSaga> repository = scope.ServiceProvider.GetRequiredService<ISagaRepository<TSaga>>();
            var executor = new StateMachineExecutor<TSaga>(
                definition, repository, loggerFactory.CreateLogger<StateMachineExecutor<TSaga>>());

            await executor.ProcessEventAsync(evt, context, ct).ConfigureAwait(false);
            return true;
        };
    }

    private static Guid? TryParseGuidHeader(IReadOnlyDictionary<string, string> headers, string key)
    {
        if (headers.TryGetValue(key, out string? value) && Guid.TryParse(value, out Guid result))
            return result;
        return null;
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "No saga event delegate matched message {MessageId} for saga '{SagaType}'.")]
    private partial void LogNoEventMatched(string messageId, string sagaType);
}
