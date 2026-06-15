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
}
