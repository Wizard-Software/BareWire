using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using BareWire.Abstractions.Configuration;
using RabbitMQ.Client;

namespace BareWire.Transport.RabbitMQ.Configuration;

internal sealed class RabbitMqTlsConfigurator : ITlsConfigurator
{
    private string? _certificatePath;
    private string? _certificatePassword;
    private bool _mutualAuthEnabled;
    private SslPolicyErrors _acceptablePolicyErrors = SslPolicyErrors.None;

    public ITlsConfigurator WithCertificate(string path, string? password = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        _certificatePath = path;
        _certificatePassword = password;

        return this;
    }

    public ITlsConfigurator WithMutualAuthentication()
    {
        _mutualAuthEnabled = true;
        return this;
    }

    public ITlsConfigurator WithServerValidation(SslPolicyErrors acceptablePolicyErrors)
    {
        _acceptablePolicyErrors = acceptablePolicyErrors;
        return this;
    }

    internal SslOption Build(string serverName)
    {
        ArgumentException.ThrowIfNullOrEmpty(serverName);

        var sslOption = new SslOption
        {
            Enabled = true,
            ServerName = serverName,
            CertPath = _certificatePath ?? string.Empty,
            CertPassphrase = _certificatePassword ?? string.Empty,
            AcceptablePolicyErrors = _acceptablePolicyErrors,
        };

        if (_mutualAuthEnabled && !string.IsNullOrEmpty(_certificatePath))
        {
            X509Certificate2 cert = string.IsNullOrEmpty(_certificatePassword)
                ? X509CertificateLoader.LoadPkcs12FromFile(_certificatePath, password: null)
                : X509CertificateLoader.LoadPkcs12FromFile(_certificatePath, _certificatePassword);

            sslOption.Certs = [cert];
        }

        return sslOption;
    }
}
