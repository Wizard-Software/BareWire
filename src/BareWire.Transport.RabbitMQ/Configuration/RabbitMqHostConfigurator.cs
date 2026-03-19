using BareWire.Abstractions.Configuration;

namespace BareWire.Transport.RabbitMQ.Configuration;

internal sealed class RabbitMqHostConfigurator : IHostConfigurator
{
    private string? _username;
    private string? _password;
    private Action<ITlsConfigurator>? _tlsConfigure;

    internal string? UsernameValue => _username;
    internal string? PasswordValue => _password;
    internal Action<ITlsConfigurator>? TlsConfigure => _tlsConfigure;

    public void Username(string username)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        _username = username;
    }

    public void Password(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        _password = password;
    }

    public void UseTls(Action<ITlsConfigurator> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _tlsConfigure = configure;
    }
}
