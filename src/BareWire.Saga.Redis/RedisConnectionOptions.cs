namespace BareWire.Saga.Redis;

/// <summary>
/// Configuration options for establishing a connection to Redis via StackExchange.Redis.
/// Supports single-node, Sentinel, and Cluster topologies with optional TLS and mTLS (PFX-only).
/// </summary>
/// <remarks>
/// Pass an instance of this class to
/// <see cref="ServiceCollectionExtensions.AddBareWireRedisConnection"/> to configure and register
/// <c>IConnectionMultiplexer</c> as a singleton in the DI container.
/// </remarks>
public sealed class RedisConnectionOptions
{
    /// <summary>
    /// Gets the list of Redis endpoints in <c>host</c> or <c>host:port</c> format.
    /// At least one endpoint is required. Multiple endpoints activate Cluster or Sentinel mode.
    /// </summary>
    public IList<string> Endpoints { get; } = new List<string>();

    /// <summary>
    /// Gets or sets the password used for Redis authentication.
    /// When <see langword="null"/> or empty, no password is sent.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Gets or sets the ACL username used for Redis 6+ authentication.
    /// When <see langword="null"/> or empty, the default user is assumed.
    /// </summary>
    public string? User { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether TLS (SSL) is enabled for the connection.
    /// </summary>
    /// <value><see langword="false"/> by default.</value>
    public bool Ssl { get; set; }

    /// <summary>
    /// Gets or sets the expected server name for TLS certificate validation.
    /// When set, StackExchange.Redis enforces that the server's certificate matches this host name.
    /// </summary>
    public string? SslHost { get; set; }

    /// <summary>
    /// Gets or sets the file system path to the PFX certificate used for mutual TLS (mTLS).
    /// When set, the file must exist, and <see cref="ServiceCollectionExtensions.AddBareWireRedisConnection"/>
    /// calls <c>ConfigurationOptions.SetUserPfxCertificate</c> which also implicitly enables TLS.
    /// </summary>
    public string? ClientCertificatePfxPath { get; set; }

    /// <summary>
    /// Gets or sets the password for the PFX certificate at <see cref="ClientCertificatePfxPath"/>.
    /// When <see langword="null"/>, the PFX is assumed to have no password.
    /// </summary>
    public string? ClientCertificatePfxPassword { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether TLS is required regardless of other settings.
    /// When <see langword="true"/> (the default) and <see cref="Ssl"/> is <see langword="false"/>,
    /// <see cref="RedisConfigurationBuilder.Build"/> throws a <see cref="BareWire.Abstractions.Exceptions.BareWireConfigurationException"/>.
    /// Set to <see langword="false"/> only for development/test environments.
    /// </summary>
    /// <value><see langword="true"/> by default.</value>
    public bool RequireTlsInProduction { get; set; } = true;

    /// <summary>
    /// Gets or sets the Sentinel service name. When non-empty, StackExchange.Redis operates in
    /// Sentinel mode and treats <see cref="Endpoints"/> as Sentinel node addresses.
    /// </summary>
    public string? ServiceName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to abort immediately when the initial connection
    /// attempt fails. The recommended value for library use is <see langword="false"/>, which
    /// allows StackExchange.Redis to reconnect in the background.
    /// </summary>
    /// <value><see langword="false"/> by default.</value>
    public bool AbortOnConnectFail { get; set; }

    /// <summary>
    /// Gets or sets the number of connection retry attempts before giving up.
    /// </summary>
    /// <value><c>3</c> by default.</value>
    public int ConnectRetry { get; set; } = 3;

    /// <summary>
    /// Gets or sets the connection timeout in milliseconds.
    /// When <see langword="null"/>, the StackExchange.Redis default timeout is used.
    /// </summary>
    public int? ConnectTimeout { get; set; }

    /// <summary>
    /// Gets or sets a label for this connection, visible through the Redis <c>CLIENT LIST</c> command.
    /// </summary>
    public string? ClientName { get; set; }

    /// <summary>
    /// Returns a diagnostic string representation of this configuration.
    /// Secrets (<see cref="Password"/> and <see cref="ClientCertificatePfxPassword"/>) are masked.
    /// </summary>
    public override string ToString()
    {
        var password = string.IsNullOrEmpty(Password) ? "(none)" : "***";
        var pfxPassword = string.IsNullOrEmpty(ClientCertificatePfxPassword) ? "(none)" : "***";

        return $"RedisConnectionOptions {{ Endpoints = [{string.Join(", ", Endpoints)}], "
             + $"User = {User ?? "(none)"}, Password = {password}, "
             + $"Ssl = {Ssl}, SslHost = {SslHost ?? "(none)"}, "
             + $"ClientCertificatePfxPath = {ClientCertificatePfxPath ?? "(none)"}, ClientCertificatePfxPassword = {pfxPassword}, "
             + $"RequireTlsInProduction = {RequireTlsInProduction}, ServiceName = {ServiceName ?? "(none)"}, "
             + $"AbortOnConnectFail = {AbortOnConnectFail}, ConnectRetry = {ConnectRetry}, "
             + $"ConnectTimeout = {ConnectTimeout?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(default)"}, ClientName = {ClientName ?? "(none)"} }}";
    }
}
