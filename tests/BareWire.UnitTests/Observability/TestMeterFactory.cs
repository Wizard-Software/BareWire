using System.Diagnostics.Metrics;

namespace BareWire.UnitTests.Observability;

/// <summary>
/// A real <see cref="IMeterFactory"/> for tests that need <c>AddBareWireObservability()</c> to resolve
/// without referencing the full metrics hosting stack. Owns and disposes every meter it creates.
/// </summary>
internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _created = [];

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options);
        lock (_created)
        {
            _created.Add(meter);
        }

        return meter;
    }

    public void Dispose()
    {
        lock (_created)
        {
            foreach (Meter meter in _created)
            {
                meter.Dispose();
            }
        }
    }
}
