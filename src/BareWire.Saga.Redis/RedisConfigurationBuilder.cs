using BareWire.Abstractions.Exceptions;
using StackExchange.Redis;

namespace BareWire.Saga.Redis;

internal static class RedisConfigurationBuilder
{
    internal static ConfigurationOptions Build(RedisConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // 1. Endpoints — required, at least one
        if (options.Endpoints is null || options.Endpoints.Count == 0)
        {
            throw new BareWireConfigurationException(
                nameof(options.Endpoints),
                optionValue: null,
                expectedValue: "at least one Redis endpoint (host[:port])");
        }

        // 2. SEC-01: TLS flag check — builder is a pure function, no env-var reads
        if (options.RequireTlsInProduction && !options.Ssl)
        {
            throw new BareWireConfigurationException(
                nameof(options.Ssl),
                optionValue: "false",
                expectedValue: "TLS required (RequireTlsInProduction=true)");
        }

        // 3. Build base ConfigurationOptions
        var config = new ConfigurationOptions
        {
            AbortOnConnectFail = options.AbortOnConnectFail,
            ConnectRetry = options.ConnectRetry,
            ClientName = options.ClientName,
        };

        if (options.ConnectTimeout.HasValue)
        {
            config.ConnectTimeout = options.ConnectTimeout.Value;
        }

        foreach (var ep in options.Endpoints)
        {
            config.EndPoints.Add(ep);
        }

        // 4. Auth
        if (options.Password is not null)
        {
            config.Password = options.Password;
        }

        if (options.User is not null)
        {
            config.User = options.User;
        }

        // 5. TLS
        config.Ssl = options.Ssl;

        if (!string.IsNullOrEmpty(options.SslHost))
        {
            config.SslHost = options.SslHost;
        }

        // 6. mTLS — PFX-only (§0 dec. 1); no PEM branch
        if (!string.IsNullOrEmpty(options.ClientCertificatePfxPath))
        {
            if (!File.Exists(options.ClientCertificatePfxPath))
            {
                throw new BareWireConfigurationException(
                    nameof(options.ClientCertificatePfxPath),
                    optionValue: null,
                    expectedValue: "an existing PFX file");
            }

            config.SetUserPfxCertificate(options.ClientCertificatePfxPath, options.ClientCertificatePfxPassword);
        }

        // 7. Sentinel
        if (!string.IsNullOrEmpty(options.ServiceName))
        {
            config.ServiceName = options.ServiceName;
        }

        return config;
    }
}
