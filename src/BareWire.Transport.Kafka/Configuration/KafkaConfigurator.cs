namespace BareWire.Transport.Kafka.Configuration;

internal sealed class KafkaConfigurator : IKafkaConfigurator
{
    private string? _bootstrapServers;

    public void BootstrapServers(string bootstrapServers)
    {
        ArgumentException.ThrowIfNullOrEmpty(bootstrapServers);
        _bootstrapServers = bootstrapServers;
    }

    internal KafkaTransportOptions Build()
    {
        var options = new KafkaTransportOptions();

        if (_bootstrapServers is not null)
        {
            options.BootstrapServers = _bootstrapServers;
        }

        options.Validate();

        return options;
    }
}
