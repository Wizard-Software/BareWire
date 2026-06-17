using BareWire.Abstractions;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using Microsoft.Extensions.Logging;

namespace BareWire.Saga.Scheduling;

internal static class ScheduleProviderFactory
{
    internal static IScheduleProvider Create(
        SchedulingStrategy strategy,
        ITransportAdapter transport,
        ILoggerFactory loggerFactory,
        IMessageSerializer serializer,
        int maxTokens = TransportNativeScheduleProvider.DefaultMaxTokens)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(serializer);

        return strategy switch
        {
            SchedulingStrategy.Auto => ResolveAuto(transport, serializer, loggerFactory, maxTokens),
            SchedulingStrategy.DelayRequeue => new DelayRequeueScheduleProvider(
                transport, serializer, loggerFactory.CreateLogger<DelayRequeueScheduleProvider>()),
            SchedulingStrategy.TransportNative => ResolveTransportNative(transport, serializer, loggerFactory, maxTokens),
            SchedulingStrategy.DelayTopic => throw new NotSupportedException(
                "Delay-topic scheduling is not yet implemented."),
            SchedulingStrategy.ExternalScheduler => throw new NotSupportedException(
                "External scheduler is not yet implemented."),
            _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy,
                $"Unknown scheduling strategy: {strategy}"),
        };
    }

    private static TransportNativeScheduleProvider CreateNativeProvider(
        INativeMessageScheduler nativeScheduler,
        IMessageSerializer serializer,
        ILoggerFactory loggerFactory,
        int maxTokens) =>
        new(nativeScheduler,
            serializer,
            loggerFactory.CreateLogger<TransportNativeScheduleProvider>(),
            TimeProvider.System,
            maxTokens);

    private static DelayRequeueScheduleProvider CreateDelayRequeueProvider(
        ITransportAdapter transport,
        IMessageSerializer serializer,
        ILoggerFactory loggerFactory) =>
        new(transport, serializer, loggerFactory.CreateLogger<DelayRequeueScheduleProvider>());

    private static TransportNativeScheduleProvider ResolveTransportNative(
        ITransportAdapter transport,
        IMessageSerializer serializer,
        ILoggerFactory loggerFactory,
        int maxTokens)
    {
        if (transport is INativeMessageScheduler nativeScheduler)
        {
            return CreateNativeProvider(nativeScheduler, serializer, loggerFactory, maxTokens);
        }

        throw new NotSupportedException(
            $"Transport '{transport.TransportName}' does not implement {nameof(INativeMessageScheduler)}. " +
            "Native scheduling requires a transport adapter that supports broker-level scheduled delivery " +
            "(e.g. Azure Service Bus). Use SchedulingStrategy.Auto or SchedulingStrategy.DelayRequeue instead.");
    }

    private static IScheduleProvider ResolveAuto(
        ITransportAdapter transport,
        IMessageSerializer serializer,
        ILoggerFactory loggerFactory,
        int maxTokens)
    {
        // Prefer native scheduling when the transport supports it (e.g. Azure Service Bus).
        // Fall back to DelayRequeue for transports that do not implement INativeMessageScheduler
        // (e.g. RabbitMQ), preserving existing behaviour.
        if (transport is INativeMessageScheduler nativeScheduler)
        {
            return CreateNativeProvider(nativeScheduler, serializer, loggerFactory, maxTokens);
        }

        return CreateDelayRequeueProvider(transport, serializer, loggerFactory);
    }
}
