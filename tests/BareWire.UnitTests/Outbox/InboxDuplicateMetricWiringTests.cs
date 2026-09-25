// NSubstitute's Returns() for ValueTask-returning mocks triggers CA2012 as a known false positive.
// The ValueTask is consumed internally by NSubstitute and never double-consumed.
#pragma warning disable CA2012

using System.Buffers;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using BareWire.Abstractions.Pipeline;
using BareWire.Outbox;
using BareWire.Outbox.EntityFramework;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BareWire.UnitTests.Outbox;

// Production wiring of the barewire.inbox.duplicates counter: AddBareWireOutbox hands every scoped
// InboxFilter the same InboxDiagnostics, and the transactional middleware tags duplicates with a
// bounded endpoint-derived value — never with the producer-controlled message-type header.
public sealed class InboxDuplicateMetricWiringTests : IDisposable
{
    private readonly Meter _meter;
    private readonly IMeterFactory _meterFactory;
    private readonly MeterListener _listener;
    private readonly List<KeyValuePair<string, object?>[]> _measurementTags = [];

    public InboxDuplicateMetricWiringTests()
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
        _listener.SetMeasurementEventCallback<long>((_, _, tags, _) => _measurementTags.Add(tags.ToArray()));
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _meter.Dispose();
    }

    [Fact]
    public async Task AddBareWireOutbox_ResolvedInboxFilters_ShareOneInboxDiagnostics()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_meterFactory);
        services.AddBareWireOutbox(configureDbContext: o => o.UseSqlite("DataSource=:memory:"));
        await using ServiceProvider provider = services.BuildServiceProvider();

        await using AsyncServiceScope first = provider.CreateAsyncScope();
        await using AsyncServiceScope second = provider.CreateAsyncScope();
        InboxFilter firstFilter = first.ServiceProvider.GetRequiredService<InboxFilter>();
        InboxFilter secondFilter = second.ServiceProvider.GetRequiredService<InboxFilter>();

        firstFilter.Diagnostics.Should().NotBeNull();
        firstFilter.Diagnostics.Should().BeSameAs(secondFilter.Diagnostics);
    }

    [Fact]
    public async Task InvokeAsync_DuplicateWithEndpointName_TagsDuplicateWithEndpointName()
    {
        TransactionalOutboxMiddleware middleware = CreateDuplicateMiddleware();
        MessageContext context = CreateContext(endpointName: "orders-queue", messageTypeHeader: "Forged.Type");

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        _measurementTags.Should().ContainSingle();
        _measurementTags[0].Should().Contain(
            new KeyValuePair<string, object?>(InboxDiagnostics.ConsumerTypeTag, "orders-queue"));
    }

    [Fact]
    public async Task InvokeAsync_DuplicateWithoutEndpointName_TagsDuplicateAsUnknownNotHeaderValue()
    {
        TransactionalOutboxMiddleware middleware = CreateDuplicateMiddleware();
        MessageContext context = CreateContext(endpointName: string.Empty, messageTypeHeader: "Forged.Type");

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        _measurementTags.Should().ContainSingle();
        _measurementTags[0].Should().Contain(
            new KeyValuePair<string, object?>(InboxDiagnostics.ConsumerTypeTag, "unknown"));
        _measurementTags[0].Select(t => t.Value).Should().NotContain("Forged.Type");
    }

    private TransactionalOutboxMiddleware CreateDuplicateMiddleware()
    {
        IInboxStore inboxStore = Substitute.For<IInboxStore>();
        inboxStore
            .TryLockAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(false));

        var inboxFilter = new InboxFilter(
            inboxStore,
            OutboxOptions.Default,
            NullLogger<InboxFilter>.Instance,
            new InboxDiagnostics(_meterFactory));

        DbContextOptions<OutboxDbContext> dbOptions = new DbContextOptionsBuilder<OutboxDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;

        return new TransactionalOutboxMiddleware(
            new OutboxDbContext(dbOptions),
            Substitute.For<IOutboxStore>(),
            inboxFilter,
            NullLogger<TransactionalOutboxMiddleware>.Instance);
    }

    private static MessageContext CreateContext(string endpointName, string messageTypeHeader) =>
        new(
            messageId: Guid.NewGuid(),
            headers: new Dictionary<string, string> { ["BW-MessageType"] = messageTypeHeader },
            rawBody: ReadOnlySequence<byte>.Empty,
            serviceProvider: Substitute.For<IServiceProvider>(),
            endpointName: endpointName);
}
