using System.Diagnostics.Metrics;
using AwesomeAssertions;
using BareWire.Outbox;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Outbox;

// InboxDiagnostics + the optional diagnostics parameter on InboxFilter's constructor: the
// barewire.inbox.duplicates counter, its consumer_type tag, and the counter-independent
// DuplicateCount used when no IMeterFactory is supplied.
public sealed class InboxDuplicateMetricTests : IDisposable
{
    private readonly InMemoryInboxStore _store = new();
    private readonly Meter _meter;
    private readonly IMeterFactory _meterFactory;
    private readonly MeterListener _listener;
    private readonly List<(long Value, KeyValuePair<string, object?>[] Tags)> _measurements = [];

    public InboxDuplicateMetricTests()
    {
        _meter = new Meter("BareWire.Test." + Guid.NewGuid());
        _meterFactory = Substitute.For<IMeterFactory>();
        _meterFactory.Create(Arg.Any<MeterOptions>()).Returns(_meter);

        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, _meter) && instrument.Name == InboxDiagnostics.DuplicatesCounterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
            _measurements.Add((measurement, tags.ToArray())));
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _meter.Dispose();
    }

    [Fact]
    public async Task TryLockAsync_WhenLockAlreadyHeld_IncrementsDuplicatesCounterWithConsumerTag()
    {
        var diagnostics = new InboxDiagnostics(_meterFactory);
        var filter = new InboxFilter(_store, OutboxOptions.Default, NullLogger<InboxFilter>.Instance, diagnostics);
        var messageId = Guid.NewGuid();

        (await filter.TryLockAsync(messageId, "orders-queue", TestContext.Current.CancellationToken)).Should().BeTrue();
        (await filter.TryLockAsync(messageId, "orders-queue", TestContext.Current.CancellationToken)).Should().BeFalse();

        diagnostics.DuplicateCount.Should().Be(1);
        _measurements.Should().ContainSingle();
        _measurements[0].Value.Should().Be(1);
        _measurements[0].Tags.Should().Contain(new KeyValuePair<string, object?>(InboxDiagnostics.ConsumerTypeTag, "orders-queue"));
    }

    [Fact]
    public async Task TryLockAsync_WhenLockAcquired_DoesNotIncrementDuplicatesCounter()
    {
        var diagnostics = new InboxDiagnostics(_meterFactory);
        var filter = new InboxFilter(_store, OutboxOptions.Default, NullLogger<InboxFilter>.Instance, diagnostics);
        Guid firstMessageId = Guid.NewGuid();
        Guid secondMessageId = Guid.NewGuid();

        (await filter.TryLockAsync(firstMessageId, "orders-queue", TestContext.Current.CancellationToken)).Should().BeTrue();
        (await filter.TryLockAsync(secondMessageId, "orders-queue", TestContext.Current.CancellationToken)).Should().BeTrue();
        (await filter.TryLockAsync(firstMessageId, "payments-queue", TestContext.Current.CancellationToken)).Should().BeTrue();

        diagnostics.DuplicateCount.Should().Be(0);
        _measurements.Should().BeEmpty();
    }

    [Fact]
    public async Task TryLockAsync_WithoutDiagnostics_ReturnsFalseForDuplicateWithoutThrowing()
    {
        var filter = new InboxFilter(_store, OutboxOptions.Default, NullLogger<InboxFilter>.Instance);
        var messageId = Guid.NewGuid();

        (await filter.TryLockAsync(messageId, "orders-queue", TestContext.Current.CancellationToken)).Should().BeTrue();
        (await filter.TryLockAsync(messageId, "orders-queue", TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public void DuplicateDetected_WithoutMeterFactory_CountsWithoutInstrument()
    {
        var diagnostics = new InboxDiagnostics();

        diagnostics.DuplicateDetected("q");
        diagnostics.DuplicateDetected("q");

        diagnostics.DuplicateCount.Should().Be(2);
        _measurements.Should().BeEmpty();
    }

    [Fact]
    public async Task TryLockAsync_AfterMarkProcessed_CountsRedeliveryAsDuplicate()
    {
        var diagnostics = new InboxDiagnostics(_meterFactory);
        var filter = new InboxFilter(_store, OutboxOptions.Default, NullLogger<InboxFilter>.Instance, diagnostics);
        var messageId = Guid.NewGuid();

        (await filter.TryLockAsync(messageId, "orders-queue", TestContext.Current.CancellationToken)).Should().BeTrue();
        await filter.MarkProcessedAsync(messageId, "orders-queue", TestContext.Current.CancellationToken);
        (await filter.TryLockAsync(messageId, "orders-queue", TestContext.Current.CancellationToken)).Should().BeFalse();

        diagnostics.DuplicateCount.Should().Be(1);
    }
}
