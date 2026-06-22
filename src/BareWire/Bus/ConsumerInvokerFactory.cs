using System.Buffers;
using System.Reflection;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace BareWire.Bus;

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
        IDeserializerResolver deserializerResolver,
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
        IDeserializerResolver deserializerResolver,
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
        return InvokeTypedConsumerAsync<TConsumer, TMessage>;
    }

    private static RawInvokerDelegate CreateRawTyped<TRawConsumer>()
        where TRawConsumer : class, IRawConsumer
    {
        return InvokeRawConsumerAsync<TRawConsumer>;
    }

    private static async Task InvokeTypedConsumerAsync<TConsumer, TMessage>(
        IServiceScopeFactory scopeFactory,
        ReadOnlySequence<byte> body,
        IReadOnlyDictionary<string, string> headers,
        string messageId,
        IPublishEndpoint publishEndpoint,
        ISendEndpointProvider sendEndpointProvider,
        IDeserializerResolver deserializerResolver,
        string endpointName,
        CancellationToken cancellationToken)
        where TConsumer : class, IConsumer<TMessage>
        where TMessage : class
    {
        headers.TryGetValue("content-type", out string? contentType);
        IMessageDeserializer deserializer = deserializerResolver.Resolve(contentType);

        TMessage? msg = deserializer.Deserialize<TMessage>(body);
        if (msg is null)
            throw new UnknownPayloadException(endpointName, deserializer.ContentType);

        Guid id = Guid.TryParse(messageId, out Guid parsed) ? parsed : Guid.NewGuid();

        ConsumeContext<TMessage> context = new(
            msg, id,
            TryParseGuidHeader(headers, "correlation-id"),
            TryParseGuidHeader(headers, "conversation-id"),
            null, null, null,
            headers,
            deserializer.ContentType,
            body,
            publishEndpoint, sendEndpointProvider, cancellationToken);

        // When the deserializer can read inbound MT request routing metadata, extract it once
        // and thread it through the context so RespondAsync can route the reply correctly
        // (echo requestId, send to responseAddress). Gate on content-type to avoid unnecessary
        // work on raw-first messages (PERF-2 / ADR-003).
        if (deserializer is IRequestEnvelopeRouteReader routeReader &&
            string.Equals(contentType, "application/vnd.masstransit+json", StringComparison.OrdinalIgnoreCase))
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            IResponseEnvelopeWriter? responseWriter = scope.ServiceProvider
                .GetService<IResponseEnvelopeWriter>();

            if (responseWriter is not null &&
                routeReader.TryReadRequestEnvelope(body, out RequestEnvelopeContext routing))
            {
                context.InboundRequestContext = routing;
                context.ResponseEnvelopeWriter = responseWriter;
            }

            TConsumer consumer = scope.ServiceProvider.GetRequiredService<TConsumer>();
            await ((IConsumer<TMessage>)consumer).ConsumeAsync(context).ConfigureAwait(false);
        }
        else
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            TConsumer consumer = scope.ServiceProvider.GetRequiredService<TConsumer>();
            await ((IConsumer<TMessage>)consumer).ConsumeAsync(context).ConfigureAwait(false);
        }
    }

    private static async Task InvokeRawConsumerAsync<TRawConsumer>(
        IServiceScopeFactory scopeFactory,
        ReadOnlySequence<byte> body,
        IReadOnlyDictionary<string, string> headers,
        string messageId,
        IPublishEndpoint publishEndpoint,
        ISendEndpointProvider sendEndpointProvider,
        IDeserializerResolver deserializerResolver,
        CancellationToken cancellationToken)
        where TRawConsumer : class, IRawConsumer
    {
        Guid id = Guid.TryParse(messageId, out Guid parsed) ? parsed : Guid.NewGuid();

        headers.TryGetValue("content-type", out string? contentType);
        IMessageDeserializer deserializer = deserializerResolver.Resolve(contentType);

        RawConsumeContext context = new(
            id,
            TryParseGuidHeader(headers, "correlation-id"),
            TryParseGuidHeader(headers, "conversation-id"),
            null, null, null,
            headers,
            contentType,
            body,
            publishEndpoint, sendEndpointProvider, deserializer, cancellationToken);

        // Apply the same MT routing wiring as the typed path: gate on content-type to keep the
        // raw-first path free from overhead (PERF-2 / ADR-003).
        if (deserializer is IRequestEnvelopeRouteReader routeReader &&
            string.Equals(contentType, "application/vnd.masstransit+json", StringComparison.OrdinalIgnoreCase))
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            IResponseEnvelopeWriter? responseWriter = scope.ServiceProvider
                .GetService<IResponseEnvelopeWriter>();

            if (responseWriter is not null &&
                routeReader.TryReadRequestEnvelope(body, out RequestEnvelopeContext routing))
            {
                context.InboundRequestContext = routing;
                context.ResponseEnvelopeWriter = responseWriter;
            }

            TRawConsumer consumer = scope.ServiceProvider.GetRequiredService<TRawConsumer>();
            await ((IRawConsumer)consumer).ConsumeAsync(context).ConfigureAwait(false);
        }
        else
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            TRawConsumer consumer = scope.ServiceProvider.GetRequiredService<TRawConsumer>();
            await ((IRawConsumer)consumer).ConsumeAsync(context).ConfigureAwait(false);
        }
    }

    private static Guid? TryParseGuidHeader(IReadOnlyDictionary<string, string> headers, string key)
    {
        if (headers.TryGetValue(key, out string? value) && Guid.TryParse(value, out Guid result))
            return result;
        return null;
    }
}
