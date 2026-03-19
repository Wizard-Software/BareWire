namespace BareWire.Abstractions.Configuration;

/// <summary>
/// Provides a fluent API for configuring RabbitMQ broker host credentials and transport security.
/// Obtained via the optional <c>configure</c> parameter of <see cref="IRabbitMqConfigurator.Host"/>.
/// </summary>
public interface IHostConfigurator
{
    /// <summary>
    /// Sets the username used to authenticate with the RabbitMQ broker.
    /// Overrides any username embedded in the connection URI.
    /// </summary>
    /// <param name="username">
    /// The broker username. Must not be <see langword="null"/> or empty.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="username"/> is <see langword="null"/> or empty.
    /// </exception>
    void Username(string username);

    /// <summary>
    /// Sets the password used to authenticate with the RabbitMQ broker.
    /// Overrides any password embedded in the connection URI.
    /// The password is never logged or included in diagnostic output.
    /// </summary>
    /// <param name="password">
    /// The broker password. Must not be <see langword="null"/> or empty.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="password"/> is <see langword="null"/> or empty.
    /// </exception>
    void Password(string password);

    /// <summary>
    /// Configures TLS or mutual TLS (mTLS) for the broker connection.
    /// When the connection URI uses the <c>amqps://</c> scheme, calling this method
    /// provides additional certificate and policy settings.
    /// </summary>
    /// <param name="configure">
    /// A delegate that receives an <see cref="ITlsConfigurator"/> and applies certificate
    /// path, passphrase, and server validation policy settings.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    void UseTls(Action<ITlsConfigurator> configure);
}
