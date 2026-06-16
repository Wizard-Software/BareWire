using System.Text.Json;
using System.Text.Json.Serialization;

namespace BareWire.CloudEvents;

/// <summary>
/// Provides the shared <see cref="JsonSerializerOptions"/> instance used for serializing
/// the <c>data</c> payload in a CloudEvents structured-mode envelope.
/// </summary>
/// <remarks>
/// <para>
/// This type exists because <c>BareWire.CloudEvents</c> depends solely on
/// <c>BareWire.Abstractions</c> (ADR-007 / task 13.14 NetArchTest enforcement) and therefore
/// cannot reference <c>BareWireJsonSerializerOptions</c> from <c>BareWire.Serialization.Json</c>.
/// The singleton here carries identical semantics: camelCase naming (implied by
/// <see cref="JsonSerializerDefaults.Web"/>), null values omitted, no indentation.
/// </para>
/// <para>
/// PERF: The singleton is constructed once at class initialization. Never create a
/// <see cref="JsonSerializerOptions"/> instance inside <c>Serialize&lt;T&gt;</c> — doing so
/// bypasses the reflection-metadata cache and causes a critical per-call allocation regression.
/// </para>
/// </remarks>
internal static class CloudEventsJsonSerializerOptions
{
    // PERF-1: constructed once as a static readonly singleton.
    // JsonSerializerDefaults.Web implies camelCase — do NOT also set PropertyNamingPolicy.
    internal static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    // SEC-1: a second singleton with MaxDepth = 32 used exclusively for DESERIALIZING untrusted
    // envelope bytes and the inner data payload. Without MaxDepth, a deeply-nested 'data' value
    // smaller than MaxEnvelopeSizeBytes can still cause CPU/allocation blow-up in the STJ
    // reflection path. Default STJ MaxDepth = 64; 32 is a safe bounded cap for CE payloads.
    // Do NOT mutate Default above — it is shared with CloudEventsEnvelopeSerializer (13.8)
    // which produces trusted output and must not be constrained by the consumer-side depth limit
    // (feedback_serializer_no_global_replace rule).
    internal static readonly JsonSerializerOptions Bounded = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        MaxDepth = CloudEventsEnvelopeLimits.Default.MaxDataDepth,
    };
}
