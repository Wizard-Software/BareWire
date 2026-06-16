namespace BareWire.CloudEvents;

/// <summary>
/// An immutable, publicly constructable representation of CloudEvents 1.0 context attributes.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CloudEventContext"/> is the concrete contract type used by publishers to supply
/// CloudEvent attributes when sending a message, and by consumers as the return value of
/// <c>CloudEventContextExtensions.GetCloudEvent</c>. It implements <see cref="ICloudEventAttributes"/>
/// so that 13.5 can introduce allocation-optimised implementations without any API surface change.
/// </para>
/// <para>
/// This class performs null-guards on the four mandatory attributes but does NOT enforce
/// CloudEvents 1.0 domain rules (e.g. non-empty <c>Id</c>, valid <c>SpecVersion</c> value).
/// Domain validation is the responsibility of the fail-fast validator introduced in task 13.3.
/// </para>
/// <para>
/// All properties are <see langword="init"/>-only, ensuring instances are immutable after construction.
/// </para>
/// </remarks>
public sealed class CloudEventContext : ICloudEventAttributes
{
    private static readonly IReadOnlyDictionary<string, string> _emptyExtensions =
        new Dictionary<string, string>(0);

    /// <inheritdoc/>
    public string Id { get; init; }

    /// <inheritdoc/>
    public Uri Source { get; init; }

    /// <inheritdoc/>
    public string SpecVersion { get; init; }

    /// <inheritdoc/>
    public string Type { get; init; }

    /// <inheritdoc/>
    public string? Subject { get; init; }

    /// <inheritdoc/>
    public DateTimeOffset? Time { get; init; }

    /// <inheritdoc/>
    public string? DataContentType { get; init; }

    /// <inheritdoc/>
    public Uri? DataSchema { get; init; }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> Extensions { get; init; }

    /// <summary>
    /// Initializes a new instance of <see cref="CloudEventContext"/> with the four mandatory
    /// CloudEvents 1.0 attributes and any optional attributes supplied by the caller.
    /// </summary>
    /// <param name="id">
    /// The unique event identifier. Must not be <see langword="null"/> or whitespace.
    /// </param>
    /// <param name="source">
    /// The URI identifying the context in which the event occurred. Must not be <see langword="null"/>.
    /// </param>
    /// <param name="type">
    /// The type descriptor of the event. Must not be <see langword="null"/> or whitespace.
    /// </param>
    /// <param name="specVersion">
    /// The CloudEvents specification version. Defaults to <c>"1.0"</c>.
    /// Must not be <see langword="null"/> or whitespace.
    /// </param>
    /// <param name="subject">
    /// Optional additional qualifier for the event subject. <see langword="null"/> when not required.
    /// </param>
    /// <param name="time">
    /// Optional timestamp (RFC 3339) of the occurrence. <see langword="null"/> when not required.
    /// </param>
    /// <param name="dataContentType">
    /// Optional MIME content type of the event data. <see langword="null"/> when not required.
    /// </param>
    /// <param name="dataSchema">
    /// Optional URI identifying the data schema. <see langword="null"/> when not required.
    /// </param>
    /// <param name="extensions">
    /// Optional extension attributes. When <see langword="null"/>, an empty dictionary is used.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="id"/>, <paramref name="source"/>, <paramref name="type"/>,
    /// or <paramref name="specVersion"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="id"/>, <paramref name="type"/>, or <paramref name="specVersion"/>
    /// is empty or consists only of whitespace.
    /// </exception>
    public CloudEventContext(
        string id,
        Uri source,
        string type,
        string specVersion = "1.0",
        string? subject = null,
        DateTimeOffset? time = null,
        string? dataContentType = null,
        Uri? dataSchema = null,
        IReadOnlyDictionary<string, string>? extensions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(specVersion);

        Id = id;
        Source = source;
        Type = type;
        SpecVersion = specVersion;
        Subject = subject;
        Time = time;
        DataContentType = dataContentType;
        DataSchema = dataSchema;
        Extensions = extensions ?? _emptyExtensions;
    }
}
