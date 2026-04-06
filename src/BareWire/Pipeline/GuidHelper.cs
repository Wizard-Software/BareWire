using System.Security.Cryptography;
using System.Text;

namespace BareWire.Pipeline;

/// <summary>
/// Converts a string identifier to a <see cref="Guid"/>. If the string is a valid Guid format
/// it is parsed directly; otherwise a deterministic Guid is derived from the first 16 bytes of
/// the SHA-256 hash, ensuring a stable, collision-resistant identifier.
/// </summary>
internal static class GuidHelper
{
    internal static Guid ParseOrHash(string value)
    {
        if (Guid.TryParse(value, out Guid parsed))
            return parsed;

        byte[] idBytes = Encoding.UTF8.GetBytes(value);
        byte[] hash = SHA256.HashData(idBytes);

        // Use the first 16 bytes of the hash as the Guid bytes.
        return new Guid(hash.AsSpan(0, 16));
    }
}
