using MessagePack;
using MessagePack.Resolvers;

namespace BareWire.Serialization.MsgPack;

/// <summary>
/// Shared <see cref="MessagePackSerializerOptions"/> used by all BareWire MessagePack
/// serializer and deserializer instances.
/// </summary>
internal static class BareWireMessagePackSerializerOptions
{
    /// <summary>
    /// Default options: <see cref="ContractlessStandardResolver"/> (supports plain <c>record</c> types
    /// without <c>[MessagePackObject]</c> attributes — aligned with ADR-001 raw-first and ADR-005 plain records)
    /// combined with <see cref="MessagePackSecurity.UntrustedData"/> (defends against DoS via
    /// SipHash collision-resistance and a recursion-depth limit on the consume path).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>SEC-1 — LZ4 compression MUST NOT be enabled on the untrusted consume path.</b>
    /// Enabling <c>MessagePackCompression.Lz4Block</c> or <c>Lz4BlockArray</c> on data received
    /// from the network would re-open GHSA-hv8m-jj95-wg3x (LZ4 out-of-bounds read).
    /// These options intentionally keep <see cref="MessagePackCompression.None"/> (the default for
    /// <c>MessagePackSerializerOptions.Standard</c>).  If compression is ever required on a
    /// trusted, internal publish path, create a separate options instance scoped to that path
    /// and never share it with the consume path.
    /// </para>
    /// <para>
    /// <b>SEC-2 — Never use a Typeless resolver or polymorphic <c>object</c> fields on records
    /// deserialized from the network.</b>  <c>TypelessContractlessStandardResolver</c> allows
    /// attacker-controlled type instantiation (gadget-chain RCE).  Always use concrete
    /// <c>Deserialize&lt;T&gt;</c> with a known, closed CLR type.
    /// </para>
    /// </remarks>
    internal static readonly MessagePackSerializerOptions Default =
        MessagePackSerializerOptions.Standard
            .WithResolver(ContractlessStandardResolver.Instance)
            .WithSecurity(MessagePackSecurity.UntrustedData);
}
