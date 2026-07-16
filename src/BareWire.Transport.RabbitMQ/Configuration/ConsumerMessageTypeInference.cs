using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;

namespace BareWire.Transport.RabbitMQ.Configuration;

/// <summary>
/// Resolves the single message type <c>TMessage</c> for a consumer registered through the
/// sugar overload <c>Consumer&lt;TConsumer&gt;()</c> on the RabbitMQ endpoint configurator, by
/// inspecting the <see cref="IConsumer{T}"/> interfaces the consumer implements. Runs
/// <strong>once at startup</strong> (configuration time), never per message. Duplicated per project
/// (Core/Transport internals are not shared — package dependency rule: Transport.RabbitMQ depends on
/// Abstractions only) with semantics identical to the core inference helper.
/// </summary>
internal static class ConsumerMessageTypeInference
{
    /// <summary>
    /// Returns the single message type handled by <paramref name="consumerType"/> when it implements
    /// exactly one <see cref="IConsumer{T}"/>. Fails fast with an actionable
    /// <see cref="BareWireConfigurationException"/> when the consumer implements none, or more than one
    /// (multi-consumer ambiguity — the caller must use the explicit
    /// <c>Consumer&lt;TConsumer, TMessage&gt;()</c> overload).
    /// </summary>
    /// <param name="consumerType">The consumer implementation type to inspect.</param>
    /// <returns>The single inferred message type.</returns>
    /// <exception cref="BareWireConfigurationException">
    /// Thrown when <paramref name="consumerType"/> implements zero or multiple
    /// <see cref="IConsumer{T}"/> interfaces.
    /// </exception>
    internal static Type ResolveSingleMessageType(Type consumerType)
    {
        Type[] messageTypes = consumerType.GetInterfaces()
            .Where(static i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>))
            .Select(static i => i.GetGenericArguments()[0])
            .Distinct()
            .ToArray();

        if (messageTypes.Length == 1)
        {
            return messageTypes[0];
        }

        if (messageTypes.Length == 0)
        {
            throw new BareWireConfigurationException(
                optionName: $"Consumer<{consumerType.Name}>()",
                optionValue: consumerType.FullName,
                expectedValue:
                    $"'{consumerType.Name}' to implement exactly one IConsumer<T>; it implements none. " +
                    "Implement IConsumer<TMessage>, or register an untyped consumer with RawConsumer<T>()");
        }

        string implemented = string.Join(", ", messageTypes.Select(static t => $"IConsumer<{t.Name}>"));
        throw new BareWireConfigurationException(
            optionName: $"Consumer<{consumerType.Name}>()",
            optionValue: implemented,
            expectedValue:
                $"exactly one IConsumer<T>; '{consumerType.Name}' implements {messageTypes.Length} " +
                $"({implemented}). Use the explicit Consumer<{consumerType.Name}, TMessage>() overload to disambiguate");
    }
}
