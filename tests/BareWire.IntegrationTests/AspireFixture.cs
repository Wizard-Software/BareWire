using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BareWire.IntegrationTests;

public sealed class AspireFixture : IAsyncLifetime
{
    // Kafka (KRaft-mode container) starts noticeably slower than RabbitMQ, so the shared
    // startup budget is generous enough to cover the slowest resource in the AppHost.
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);
    private DistributedApplication? _app;
    private string? _rabbitMqConnectionString;
    private string? _kafkaBootstrapServers;
    private string? _redisConnectionString;

    public async ValueTask InitializeAsync()
    {
        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.BareWire_AppHost>();
        _app = await builder.BuildAsync();
        var notifier = _app.Services.GetRequiredService<ResourceNotificationService>();
        await _app.StartAsync();

        using var cts = new CancellationTokenSource(StartupTimeout);
        await notifier.WaitForResourceHealthyAsync("rmq", cts.Token);
        await notifier.WaitForResourceHealthyAsync("kafka", cts.Token);
        await notifier.WaitForResourceHealthyAsync("redis", cts.Token);

        _rabbitMqConnectionString = await _app.GetConnectionStringAsync("rmq");
        _kafkaBootstrapServers = await _app.GetConnectionStringAsync("kafka");
        _redisConnectionString = await _app.GetConnectionStringAsync("redis");
    }

    public string GetRabbitMqConnectionString()
    {
        return _rabbitMqConnectionString
            ?? throw new InvalidOperationException("RabbitMQ connection string not available");
    }

    /// <summary>
    /// Returns the Kafka bootstrap-server list (e.g. <c>localhost:9092</c>) for the broker
    /// provisioned by the AppHost. The Aspire connection string for a Kafka resource is the
    /// bootstrap-server list expected by <c>KafkaTransportOptions.BootstrapServers</c>.
    /// </summary>
    public string GetKafkaBootstrapServers()
    {
        return _kafkaBootstrapServers
            ?? throw new InvalidOperationException("Kafka bootstrap servers not available");
    }

    /// <summary>
    /// Returns the Redis connection string for the container provisioned by the AppHost.
    /// The Aspire connection string for a Redis resource is a full StackExchange.Redis
    /// configuration string (e.g. <c>host:port,password=…,ssl=true</c>) and must be parsed
    /// via <c>StackExchange.Redis.ConfigurationOptions.Parse(...)</c> before use — it is NOT
    /// a bare endpoint suitable for <c>EndPointCollection.Add(...)</c>/<c>RedisConnectionOptions.Endpoints.Add(...)</c>,
    /// which accept only <c>host</c> or <c>host:port</c>.
    /// </summary>
    public string GetRedisConnectionString()
    {
        return _redisConnectionString
            ?? throw new InvalidOperationException("Redis connection string not available");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
