using BareWire.Abstractions.Headers;

namespace BareWire.Transport.RabbitMQ;

/// <summary>
/// Accumulates header mapping configuration and exposes the result as immutable state.
/// Custom mappings override default AMQP ↔ BareWire mappings.
/// </summary>
internal sealed class RabbitMqHeaderMappingConfigurator : IHeaderMappingConfigurator
{
    private readonly Dictionary<string, string> _customMappings =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The transport header name that carries the correlation ID.
    /// <see langword="null"/> means the default (<c>IBasicProperties.CorrelationId</c>) is used.
    /// </summary>
    internal string? CorrelationIdMapping { get; private set; }

    /// <summary>
    /// The transport header name that carries the message type discriminator.
    /// <see langword="null"/> means the default (<c>IBasicProperties.Type</c>) is used.
    /// </summary>
    internal string? MessageTypeMapping { get; private set; }

    /// <summary>
    /// Custom header name mappings: BareWire canonical name → transport header name.
    /// </summary>
    internal IReadOnlyDictionary<string, string> CustomMappings => _customMappings;

    /// <summary>
    /// When <see langword="true"/>, only explicitly mapped headers pass through (whitelist mode).
    /// Default is <see langword="false"/> (passthrough all headers).
    /// </summary>
    internal bool ShouldIgnoreUnmapped { get; private set; }

    /// <inheritdoc />
    public void MapCorrelationId(string headerName)
    {
        ArgumentException.ThrowIfNullOrEmpty(headerName);
        CorrelationIdMapping = headerName;
    }

    /// <inheritdoc />
    public void MapMessageType(string headerName)
    {
        ArgumentException.ThrowIfNullOrEmpty(headerName);
        MessageTypeMapping = headerName;
    }

    /// <inheritdoc />
    public void MapHeader(string bareWireHeader, string transportHeader)
    {
        ArgumentException.ThrowIfNullOrEmpty(bareWireHeader);
        ArgumentException.ThrowIfNullOrEmpty(transportHeader);
        _customMappings[bareWireHeader] = transportHeader;
    }

    /// <inheritdoc />
    public void IgnoreUnmappedHeaders(bool ignore = true)
    {
        ShouldIgnoreUnmapped = ignore;
    }
}
