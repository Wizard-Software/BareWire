namespace BareWire.Transport.RabbitMQ.Configuration;

// The per-type publish-routing dimension that diverged across two registration paths.
internal enum PublishRoutingDimension
{
    Exchange,
    RoutingKey,
}

// A config-time diagnostic record describing a divergent overwrite of a per-type publish-routing
// mapping: the same message type received a DIFFERENT value for the same dimension from two
// registration paths (e.g. DeclareExchange<T>("a") then Publish<T>(p => p.Exchange("b"))).
// Last-call-wins still applies — <see cref="NewValue"/> is the value used at runtime. This record
// exists solely to surface the divergence as a DEFAULT-ON warning at bus startup; it never affects
// runtime resolution.
internal readonly record struct PublishRoutingDivergence(
    PublishRoutingDimension Dimension,
    Type MessageType,
    string PreviousValue,
    string NewValue);
