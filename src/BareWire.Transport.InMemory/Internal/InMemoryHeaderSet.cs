using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// An immutable set of headers stamped once per publish: every publisher-supplied header whose key
/// starts with <see cref="InMemoryHeaderNames.ReservedPrefix"/> is stripped (case-insensitively),
/// except the exact key <see cref="InMemoryHeaderNames.MessageType"/>, and
/// <see cref="InMemoryHeaderNames.Exchange"/> / <see cref="InMemoryHeaderNames.RoutingKey"/> are
/// stamped with the values actually used for routing. Entries are held in a single array, never
/// modified after construction, and looked up with a linear <see cref="StringComparison.Ordinal"/>
/// scan — tuned for the small header sets (typically fewer than sixteen entries) a single message
/// carries, where a linear scan over a short array is faster than hashing and needs no bucket array;
/// the MVP places no upper bound on header count, the same trade-off the RabbitMQ adapter makes. The
/// same instance is shared, by reference, by every fan-out copy of one publish, so the header cost per
/// copy is zero.
/// </summary>
internal sealed class InMemoryHeaderSet : IReadOnlyDictionary<string, string>
{
    private readonly KeyValuePair<string, string>[] _entries;

    private InMemoryHeaderSet(KeyValuePair<string, string>[] entries, string messageId)
    {
        _entries = entries;
        MessageId = messageId;
    }

    /// <summary>Gets the empty header set: no entries, <see cref="MessageId"/> is <see cref="string.Empty"/>.</summary>
    internal static InMemoryHeaderSet Empty { get; } = new([], string.Empty);

    /// <summary>
    /// Gets the message identifier resolved for this set: the publisher's non-empty
    /// <see cref="InMemoryHeaderNames.MessageId"/> header (the same string instance), or a generated
    /// identifier when the publisher supplied none.
    /// </summary>
    internal string MessageId { get; }

    /// <summary>
    /// Builds the header set for one publish. Strips every publisher header whose key starts with
    /// <see cref="InMemoryHeaderNames.ReservedPrefix"/> (<see cref="StringComparison.OrdinalIgnoreCase"/>),
    /// except the exact key <see cref="InMemoryHeaderNames.MessageType"/>, which is carried through
    /// unchanged as a message property. Stamps <see cref="InMemoryHeaderNames.Exchange"/> and
    /// <see cref="InMemoryHeaderNames.RoutingKey"/> with the values actually used for routing.
    /// Applies <paramref name="contentType"/> when non-empty (it then wins over the publisher's
    /// <see cref="InMemoryHeaderNames.ContentType"/> header); when empty, the publisher's header, if
    /// any, is left unchanged. Resolves <see cref="MessageId"/> to the publisher's non-empty
    /// <see cref="InMemoryHeaderNames.MessageId"/> header (the same string instance, never copied) or,
    /// when missing or empty, generates one <see cref="Guid"/> per call. A typed publish supplies a
    /// message identifier, so this fallback is exercised only by the send and raw-publish paths — and
    /// even then it allocates exactly one <see cref="Guid"/> string per publish, never once per
    /// fan-out copy.
    /// </summary>
    /// <param name="publisherHeaders">The publisher-supplied headers. Must not be <see langword="null"/>.</param>
    /// <param name="exchange">The exchange actually used for routing. May be empty (the default exchange).</param>
    /// <param name="routingKey">The routing key actually used for routing.</param>
    /// <param name="contentType">The outbound message's content type. May be empty.</param>
    internal static InMemoryHeaderSet Stamp(
        IReadOnlyDictionary<string, string> publisherHeaders, string exchange, string routingKey, string contentType)
    {
        ArgumentNullException.ThrowIfNull(publisherHeaders);
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(routingKey);
        ArgumentNullException.ThrowIfNull(contentType);

        bool replaceContentType = contentType.Length > 0;
        bool hasPublisherMessageId =
            publisherHeaders.TryGetValue(InMemoryHeaderNames.MessageId, out string? publisherMessageId)
            && !string.IsNullOrEmpty(publisherMessageId);
        bool generateMessageId = !hasPublisherMessageId;
        string messageId = hasPublisherMessageId ? publisherMessageId! : Guid.NewGuid().ToString();

        // Both passes over a Dictionary<string, string> use its structural (non-boxed) enumerator;
        // any other IReadOnlyDictionary<string, string> implementation goes through the interface.
        int keptCount = publisherHeaders is Dictionary<string, string> countSource
            ? CountKept(countSource, replaceContentType)
            : CountKept(publisherHeaders, replaceContentType);

        var entries = new KeyValuePair<string, string>[
            keptCount + (generateMessageId ? 1 : 0) + (replaceContentType ? 1 : 0) + 2];

        int index = publisherHeaders is Dictionary<string, string> fillSource
            ? FillKept(fillSource, replaceContentType, entries)
            : FillKept(publisherHeaders, replaceContentType, entries);

        if (generateMessageId)
        {
            entries[index++] = new KeyValuePair<string, string>(InMemoryHeaderNames.MessageId, messageId);
        }

        if (replaceContentType)
        {
            entries[index++] = new KeyValuePair<string, string>(InMemoryHeaderNames.ContentType, contentType);
        }

        entries[index++] = new KeyValuePair<string, string>(InMemoryHeaderNames.Exchange, exchange);
        entries[index] = new KeyValuePair<string, string>(InMemoryHeaderNames.RoutingKey, routingKey);

        return new InMemoryHeaderSet(entries, messageId);
    }

    /// <inheritdoc/>
    public int Count => _entries.Length;

    /// <inheritdoc/>
    public string this[string key] =>
        TryGetValue(key, out string? value) ? value : throw new KeyNotFoundException($"The header key '{key}' was not found.");

    /// <inheritdoc/>
    public IEnumerable<string> Keys
    {
        get
        {
            foreach (KeyValuePair<string, string> entry in _entries)
            {
                yield return entry.Key;
            }
        }
    }

    /// <inheritdoc/>
    public IEnumerable<string> Values
    {
        get
        {
            foreach (KeyValuePair<string, string> entry in _entries)
            {
                yield return entry.Value;
            }
        }
    }

    /// <inheritdoc/>
    public bool ContainsKey(string key) => TryGetValue(key, out _);

    /// <inheritdoc/>
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value)
    {
        foreach (KeyValuePair<string, string> entry in _entries)
        {
            if (string.Equals(entry.Key, key, StringComparison.Ordinal))
            {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
    {
        foreach (KeyValuePair<string, string> entry in _entries)
        {
            yield return entry;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Returns whether a publisher-supplied header survives stripping: every <c>BW-</c>-prefixed key
    /// is dropped except the exact key <see cref="InMemoryHeaderNames.MessageType"/>; an empty
    /// <see cref="InMemoryHeaderNames.MessageId"/> is dropped (replaced by a generated one); a
    /// publisher <see cref="InMemoryHeaderNames.ContentType"/> is dropped when
    /// <paramref name="replaceContentType"/> is <see langword="true"/>.
    /// </summary>
    private static bool IsKept(string key, string value, bool replaceContentType)
    {
        if (key.StartsWith(InMemoryHeaderNames.ReservedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(key, InMemoryHeaderNames.MessageType, StringComparison.Ordinal);
        }

        if (string.Equals(key, InMemoryHeaderNames.MessageId, StringComparison.Ordinal))
        {
            return !string.IsNullOrEmpty(value);
        }

        if (replaceContentType && string.Equals(key, InMemoryHeaderNames.ContentType, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static int CountKept(Dictionary<string, string> headers, bool replaceContentType)
    {
        int count = 0;
        foreach (KeyValuePair<string, string> entry in headers)
        {
            if (IsKept(entry.Key, entry.Value, replaceContentType))
            {
                count++;
            }
        }

        return count;
    }

    private static int CountKept(IReadOnlyDictionary<string, string> headers, bool replaceContentType)
    {
        int count = 0;
        foreach (KeyValuePair<string, string> entry in headers)
        {
            if (IsKept(entry.Key, entry.Value, replaceContentType))
            {
                count++;
            }
        }

        return count;
    }

    private static int FillKept(Dictionary<string, string> headers, bool replaceContentType, KeyValuePair<string, string>[] entries)
    {
        int index = 0;
        foreach (KeyValuePair<string, string> entry in headers)
        {
            if (IsKept(entry.Key, entry.Value, replaceContentType))
            {
                entries[index++] = entry;
            }
        }

        return index;
    }

    private static int FillKept(
        IReadOnlyDictionary<string, string> headers, bool replaceContentType, KeyValuePair<string, string>[] entries)
    {
        int index = 0;
        foreach (KeyValuePair<string, string> entry in headers)
        {
            if (IsKept(entry.Key, entry.Value, replaceContentType))
            {
                entries[index++] = entry;
            }
        }

        return index;
    }
}
