using BareWire.Abstractions.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BareWire.Bus;

/// <summary>
/// Resolves the single <see cref="IOutboundMessageInterceptor"/> slot used by the bus, warning when the
/// slot has been registered more than once.
/// </summary>
internal static partial class OutboundInterceptorResolver
{
    /// <summary>
    /// Returns the last registered interceptor, or <see langword="null"/> when none is registered.
    /// Evaluated once at bus construction — never per message.
    /// </summary>
    internal static IOutboundMessageInterceptor? Resolve(IServiceProvider provider, ILogger logger)
    {
        IOutboundMessageInterceptor[] all = [.. provider.GetServices<IOutboundMessageInterceptor>()];

        if (all.Length == 0)
            return null;

        IOutboundMessageInterceptor selected = all[^1];

        if (all.Length > 1)
        {
            LogMultipleInterceptors(
                logger,
                string.Join(", ", all.Select(static i => i.GetType().FullName)),
                selected.GetType().FullName ?? selected.GetType().Name);
        }

        return selected;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Multiple IOutboundMessageInterceptor implementations are registered ({Implementations}). " +
                  "The bus supports a single interceptor; the last registration ({Selected}) is used and the others are ignored.")]
    private static partial void LogMultipleInterceptors(ILogger logger, string implementations, string selected);
}
