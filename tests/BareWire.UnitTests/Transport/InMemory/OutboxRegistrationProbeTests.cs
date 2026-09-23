using AwesomeAssertions;
using BareWire.Outbox.EntityFramework;
using BareWire.Transport.InMemory.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BareWire.UnitTests.Transport.InMemory;

public sealed class OutboxRegistrationProbeTests
{
    [Fact]
    public void ServiceTypeNames_MatchOutboxPackageTypes()
    {
        OutboxRegistrationProbe.OutboxStoreServiceTypeName.Should().Be(typeof(BareWire.Outbox.IOutboxStore).FullName);
        OutboxRegistrationProbe.InboxStoreServiceTypeName.Should().Be(typeof(BareWire.Outbox.IInboxStore).FullName);
    }

    [Fact]
    public void Inspect_EfOutboxRegistered_ReportsOutboxAndInbox()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBareWireOutbox(o => o.UseSqlite("DataSource=:memory:"));

        OutboxRegistrationProbe.Inspect(services).Should().Be(new OutboxRegistrationState(true, true));
    }

    [Fact]
    public void Inspect_EmptyCollection_ReportsNeither() =>
        OutboxRegistrationProbe.Inspect(new ServiceCollection()).Should().Be(new OutboxRegistrationState(false, false));

    [Fact]
    public void Inspect_OutboxStoreWithoutInboxStore_ReportsOutboxOnly()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(BareWire.Outbox.IOutboxStore), _ => Substitute.For<BareWire.Outbox.IOutboxStore>());

        OutboxRegistrationProbe.Inspect(services).Should().Be(new OutboxRegistrationState(true, false));
    }
}
