using System.Buffers;
using System.Reflection;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace BareWire.Core.Bus;

/// <summary>
/// Creates type-erased consumer invoker delegates at startup time using reflection.
/// The returned delegates are fully generic at runtime — no reflection in the hot path.
/// </summary>
internal static class ConsumerInvokerFactory
{
    /// <summary>
    /// Delegate for typed consumer invokers. Throws <see cref="UnknownPayloadException"/>
    /// when the message body cannot be deserialized to the expected message type.
    /// </summary>
    internal delegate Task InvokerDelegate(
        IServiceScopeFactory scopeFactory,
        ReadOnlySequence<byte> body,
        IReadOnlyDictionary<string, string> headers,
        string messageId,
        IPublishEndpoint publishEndpoint,
        ISendEndpointProvider sendEndpointProvider,
        IMessageDeserializer deserializer,
        string endpointName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Delegate for raw consumer invokers. Never throws <see cref="UnknownPayloadException"/> —
    /// raw consumers accept any message payload.
    /// </summary>
    internal delegate Task RawInvokerDelegate(
        IServiceScopeFactory scopeFactory,
        ReadOnlySequence<byte> body,
        IReadOnlyDictionary<string, string> headers,
        string messageId,
        IPublishEndpoint publishEndpoint,
        ISendEndpointProvider sendEndpointProvider,
        IMessageDeserializer deserializer,
        CancellationToken cancellationToken);

    private static readonly MethodInfo CreateTypedMethod =
        typeof(ConsumerInvokerFactory).GetMethod(nameof(CreateTyped), BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            $"Method '{nameof(CreateTyped)}' not found on {nameof(ConsumerInvokerFactory)}.");

    private static readonly MethodInfo CreateRawTypedMethod =
        typeof(ConsumerInvokerFactory).GetMethod(nameof(CreateRawTyped), BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            $"Method '{nameof(CreateRawTyped)}' not found on {nameof(ConsumerInvokerFactory)}.");

    internal static InvokerDelegate Create(Type consumerType, Type messageType)
    {
        return (InvokerDelegate)CreateTypedMethod
            .MakeGenericMethod(consumerType, messageType)
            .Invoke(null, null)!;
    }

    internal static RawInvokerDelegate CreateRaw(Type rawConsumerType)
    {
        return (RawInvokerDelegate)CreateRawTypedMethod
            .MakeGenericMethod(rawConsumerType)
            .Invoke(null, null)!;
    }

    private static InvokerDelegate CreateTyped<TConsumer, TMessage>()
        where TConsumer : class, IConsumer<TMessage>
        where TMessage : class
    {
        return async (scopeFactory, body, headers, messageId, pub, send, deser, endpointName, ct) =>
        {
            TMessage? msg = deser.Deserialize<TMessage>(body);
            if (msg is null)
                throw new UnknownPayloadException(endpointName, deser.ContentType);

            Guid id = Guid.TryParse(messageId, out Guid parsed) ? parsed : Guid.NewGuid();

            ConsumeContext<TMessage> context = new(
                msg, id,
                TryParseGuidHeader(headers, "correlation-id"),
                TryParseGuidHeader(headers, "conversation-id"),
                null, null, null,
                headers,
                deser.ContentType,
                body,
                pub, send, ct);

            using IServiceScope scope = scopeFactory.CreateScope();
            TConsumer consumer = scope.ServiceProvider.GetRequiredService<TConsumer>();
            await ((IConsumer<TMessage>)consumer).ConsumeAsync(context).ConfigureAwait(false);
        };
    }

    private static RawInvokerDelegate CreateRawTyped<TRawConsumer>()
        where TRawConsumer : class, IRawConsumer
    {
        return async (scopeFactory, body, headers, messageId, pub, send, deser, ct) =>
        {
            Guid id = Guid.TryParse(messageId, out Guid parsed) ? parsed : Guid.NewGuid();

            headers.TryGetValue("content-type", out string? contentType);

            RawConsumeContext context = new(
                id,
                TryParseGuidHeader(headers, "correlation-id"),
                TryParseGuidHeader(headers, "conversation-id"),
                null, null, null,
                headers,
                contentType,
                body,
                pub, send, deser, ct);

            using IServiceScope scope = scopeFactory.CreateScope();
            TRawConsumer consumer = scope.ServiceProvider.GetRequiredService<TRawConsumer>();
            await ((IRawConsumer)consumer).ConsumeAsync(context).ConfigureAwait(false);
        };
    }

    private static Guid? TryParseGuidHeader(IReadOnlyDictionary<string, string> headers, string key)
    {
        if (headers.TryGetValue(key, out string? value) && Guid.TryParse(value, out Guid result))
            return result;
        return null;
    }
}
