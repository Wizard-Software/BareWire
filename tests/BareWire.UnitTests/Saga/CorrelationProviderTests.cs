using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Saga;

namespace BareWire.UnitTests.Saga;

// ── Event types for CorrelationProvider tests ─────────────────────────────────

public sealed record InvoiceCreated(Guid InvoiceId, decimal Amount);
public sealed record InvoicePaid(Guid InvoiceId);
public sealed record UnmappedEvent(Guid SomeId);

// ── Tests ─────────────────────────────────────────────────────────────────────

public sealed class CorrelationProviderTests
{
    private static CorrelationProvider<CorrelationSagaState> CreateProvider()
    {
        var correlations = new Dictionary<Type, Func<object, Guid>>
        {
            [typeof(InvoiceCreated)] = obj => ((InvoiceCreated)obj).InvoiceId,
            [typeof(InvoicePaid)] = obj => ((InvoicePaid)obj).InvoiceId
        };
        return new CorrelationProvider<CorrelationSagaState>(correlations);
    }

    [Fact]
    public void GetCorrelationId_ValidMapping_ReturnsGuid()
    {
        var provider = CreateProvider();
        var invoiceId = Guid.NewGuid();

        var result = provider.GetCorrelationId(new InvoiceCreated(invoiceId, 99.99m));

        result.Should().Be(invoiceId);
    }

    [Fact]
    public void GetCorrelationId_DifferentRegisteredType_ReturnsCorrectGuid()
    {
        var provider = CreateProvider();
        var invoiceId = Guid.NewGuid();

        var result = provider.GetCorrelationId(new InvoicePaid(invoiceId));

        result.Should().Be(invoiceId);
    }

    [Fact]
    public void GetCorrelationId_MissingMapping_ThrowsSagaException()
    {
        var provider = CreateProvider();

        Action act = () => provider.GetCorrelationId(new UnmappedEvent(Guid.NewGuid()));

        act.Should().Throw<BareWireSagaException>()
            .WithMessage("*UnmappedEvent*");
    }

    [Fact]
    public void GetCorrelationId_NullEvent_ThrowsArgumentNullException()
    {
        var provider = CreateProvider();

        Action act = () => provider.GetCorrelationId<InvoiceCreated>(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
