using System.Net.Security;

namespace BareWire.Abstractions.Configuration;

/// <summary>
/// Provides a fluent API for configuring TLS and mutual TLS (mTLS) on a broker connection.
/// Obtain an instance via the transport-specific host configurator (e.g. <c>IHostConfigurator.UseTls</c>).
/// </summary>
public interface ITlsConfigurator
{
    /// <summary>
    /// Sets the path to the client certificate file (PFX or PEM) and an optional passphrase
    /// used to decrypt the private key.
    /// </summary>
    /// <param name="path">
    /// Absolute or relative file path to the certificate. Must not be <see langword="null"/> or empty.
    /// </param>
    /// <param name="password">
    /// Optional passphrase protecting the private key. Pass <see langword="null"/> for unprotected keys.
    /// The password is never logged or included in diagnostic output.
    /// </param>
    /// <returns>The current <see cref="ITlsConfigurator"/> for method chaining.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="path"/> is <see langword="null"/> or empty.
    /// </exception>
    ITlsConfigurator WithCertificate(string path, string? password = null);

    /// <summary>
    /// Enables mutual TLS authentication, requiring the client to present its certificate
    /// to the broker during the TLS handshake.
    /// </summary>
    /// <returns>The current <see cref="ITlsConfigurator"/> for method chaining.</returns>
    ITlsConfigurator WithMutualAuthentication();

    /// <summary>
    /// Sets the <see cref="SslPolicyErrors"/> values that are acceptable during server certificate
    /// validation. Use this to allow self-signed certificates in test environments.
    /// </summary>
    /// <param name="acceptablePolicyErrors">
    /// A bitwise combination of <see cref="SslPolicyErrors"/> that the client will tolerate.
    /// Defaults to <see cref="SslPolicyErrors.None"/> (strict validation).
    /// In production, always use <see cref="SslPolicyErrors.None"/>.
    /// </param>
    /// <returns>The current <see cref="ITlsConfigurator"/> for method chaining.</returns>
    ITlsConfigurator WithServerValidation(SslPolicyErrors acceptablePolicyErrors);
}
