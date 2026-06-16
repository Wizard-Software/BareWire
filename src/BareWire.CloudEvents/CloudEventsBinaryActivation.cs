namespace BareWire.CloudEvents;

/// <summary>
/// A dependency-injection marker that signals CloudEvents binary-mode activation.
/// Registered as a singleton by <see cref="ServiceCollectionExtensions.AddCloudEvents"/>.
/// </summary>
/// <remarks>
/// <para>
/// The CloudEvents binary-mode building blocks — <c>CloudEventBinaryHeaderMapper</c> (13.4/13.5)
/// and <c>CloudEventAttributeValidator</c> (13.3) — are <see langword="internal static"/> types
/// and therefore do not require DI registration themselves. This marker class instead serves as
/// an explicit, resolvable signal that binary-mode was activated via <c>AddCloudEvents()</c>.
/// </para>
/// <para>
/// Future consumers (e.g. the structured-mode router introduced in 13.8/13.11) can depend on this
/// type to detect whether binary activation has occurred and skip duplicate setup.
/// </para>
/// </remarks>
internal sealed class CloudEventsBinaryActivation
{
}
