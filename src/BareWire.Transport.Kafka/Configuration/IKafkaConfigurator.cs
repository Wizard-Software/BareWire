namespace BareWire.Transport.Kafka.Configuration;

/// <summary>
/// Provides a fluent API for configuring the Kafka transport adapter.
/// Obtained via <see cref="ServiceCollectionExtensions.AddBareWireKafka"/>.
/// </summary>
/// <remarks>
/// This is the minimal surface for R1.1 (producer side).
/// Consumer endpoint registration and topology configuration will be added in R1.2 / R1.4.
/// </remarks>
public interface IKafkaConfigurator
{
    /// <summary>
    /// Configures the Kafka bootstrap server(s) to connect to.
    /// Must be called before the bus is started.
    /// </summary>
    /// <param name="bootstrapServers">
    /// A comma-separated list of host:port pairs (e.g. <c>localhost:9092</c>).
    /// Must not be <see langword="null"/> or empty.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="bootstrapServers"/> is <see langword="null"/> or empty.
    /// </exception>
    void BootstrapServers(string bootstrapServers);
}
