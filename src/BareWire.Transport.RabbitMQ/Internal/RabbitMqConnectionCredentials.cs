using RabbitMQ.Client;

namespace BareWire.Transport.RabbitMQ.Internal;

/// <summary>
/// Applies the credential overrides of <see cref="RabbitMqTransportOptions"/> to a <see cref="ConnectionFactory"/>.
/// </summary>
internal static class RabbitMqConnectionCredentials
{
    /// <summary>
    /// Replaces the factory's user name and password with the configured overrides, when set.
    /// Call this after <see cref="ConnectionFactory.Uri"/> is set, because that setter copies the URI user info
    /// into <see cref="ConnectionFactory.UserName"/> and <see cref="ConnectionFactory.Password"/>.
    /// Never logs or throws with a credential value.
    /// </summary>
    internal static void ApplyOverrides(ConnectionFactory factory, RabbitMqTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);

        // "is not null" matches the documented "when set" contract. The configurator rejects empty values, so an
        // empty string can only come from code that sets the options directly; it is applied as given and
        // authentication then fails, which is the safe outcome.
        if (options.UsernameOverride is not null)
        {
            factory.UserName = options.UsernameOverride;
        }

        if (options.PasswordOverride is not null)
        {
            factory.Password = options.PasswordOverride;
        }
    }
}
