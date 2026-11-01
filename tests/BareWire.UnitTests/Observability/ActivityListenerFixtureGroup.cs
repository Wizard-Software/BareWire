namespace BareWire.UnitTests.Observability;

/// <summary>
/// Serializes every test class that registers an <see cref="System.Diagnostics.ActivityListener"/>.
/// Listeners are process-global, so concurrently running classes would otherwise observe each other's
/// activities (and break "no listener" assertions).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ActivityListenerFixtureGroup
{
    public const string Name = "ActivityListener";
}
