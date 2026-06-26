namespace BareWire.Transport.RabbitMQ.Configuration;

// Resolved per-type publish-style request registration produced by RabbitMqConfigurator.PublishRequest<T>
// and passed to RabbitMqTransportOptions. The exchange name is already resolved
// (either from the explicit override or from RequestExchangeNameFormatter.Format<T>()).
internal readonly record struct PublishRequestRegistration(
    string ExchangeName,
    bool Strict,
    bool AutoDeclare);
