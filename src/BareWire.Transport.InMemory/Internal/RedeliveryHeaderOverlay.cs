using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// A read-only view over a shared <see cref="InMemoryHeaderSet"/> that adds exactly one entry —
/// <see cref="InMemoryHeaderNames.RedeliveryCount"/> — formatted once at construction. The inner set
/// is never copied. Built exactly once, by <see cref="InMemoryDelivery"/>'s constructor, for a
/// delivery whose redelivery count is greater than zero — the redelivery path, off the hot path a
/// first delivery takes.
/// </summary>
internal sealed class RedeliveryHeaderOverlay : IReadOnlyDictionary<string, string>
{
    private readonly InMemoryHeaderSet _inner;
    private readonly string _redeliveryCount;

    /// <param name="inner">The shared header set this overlay adds the redelivery counter to. Must not be <see langword="null"/>.</param>
    /// <param name="redeliveryCount">The redelivery counter to materialize. Must be greater than zero.</param>
    internal RedeliveryHeaderOverlay(InMemoryHeaderSet inner, int redeliveryCount)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(redeliveryCount);

        _inner = inner;
        _redeliveryCount = redeliveryCount.ToString(CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    public int Count => _inner.Count + 1;

    /// <inheritdoc/>
    public string this[string key] =>
        string.Equals(key, InMemoryHeaderNames.RedeliveryCount, StringComparison.Ordinal) ? _redeliveryCount : _inner[key];

    /// <inheritdoc/>
    public IEnumerable<string> Keys
    {
        get
        {
            foreach (KeyValuePair<string, string> entry in _inner)
            {
                yield return entry.Key;
            }

            yield return InMemoryHeaderNames.RedeliveryCount;
        }
    }

    /// <inheritdoc/>
    public IEnumerable<string> Values
    {
        get
        {
            foreach (KeyValuePair<string, string> entry in _inner)
            {
                yield return entry.Value;
            }

            yield return _redeliveryCount;
        }
    }

    /// <inheritdoc/>
    public bool ContainsKey(string key) =>
        string.Equals(key, InMemoryHeaderNames.RedeliveryCount, StringComparison.Ordinal) || _inner.ContainsKey(key);

    /// <inheritdoc/>
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value)
    {
        if (string.Equals(key, InMemoryHeaderNames.RedeliveryCount, StringComparison.Ordinal))
        {
            value = _redeliveryCount;
            return true;
        }

        return _inner.TryGetValue(key, out value);
    }

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
    {
        foreach (KeyValuePair<string, string> entry in _inner)
        {
            yield return entry;
        }

        yield return new KeyValuePair<string, string>(InMemoryHeaderNames.RedeliveryCount, _redeliveryCount);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
