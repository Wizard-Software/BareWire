using AwesomeAssertions;
using BareWire.Abstractions.Configuration;
using BareWire.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BareWire.UnitTests.BusConfiguratorExtensions;

// AddPartitionerMiddleware is [Obsolete] (R8.14 — superseded by per-endpoint OrderedByHeader/OrderedBy,
// retained for one coexistence release). These tests still cover the deprecated method during the
// coexistence period, so CS0618 is suppressed for the whole class.
#pragma warning disable CS0618 // Type or member is obsolete
public sealed class BusConfiguratorExtensionsTests
{
    [Fact]
    public void AddPartitionerMiddleware_NullServices_ThrowsArgumentNullException()
    {
        IServiceCollection services = null!;
        var busConfigurator = Substitute.For<IBusConfigurator>();

        var act = () => services.AddPartitionerMiddleware(busConfigurator, partitionCount: 4);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("services");
    }

    [Fact]
    public void AddPartitionerMiddleware_NullBusConfigurator_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection();
        IBusConfigurator busConfigurator = null!;

        var act = () => services.AddPartitionerMiddleware(busConfigurator, partitionCount: 4);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("busConfigurator");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void AddPartitionerMiddleware_PartitionCountLessThanOne_ThrowsArgumentOutOfRangeException(int partitionCount)
    {
        var services = new ServiceCollection();
        var busConfigurator = Substitute.For<IBusConfigurator>();

        var act = () => services.AddPartitionerMiddleware(busConfigurator, partitionCount);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName(nameof(partitionCount));
    }

    [Fact]
    public void AddPartitionerMiddleware_ValidArguments_RegistersPartitionerMiddlewareInContainer()
    {
        var services = new ServiceCollection();
        var busConfigurator = Substitute.For<IBusConfigurator>();

        services.AddPartitionerMiddleware(busConfigurator, partitionCount: 8);

        var provider = services.BuildServiceProvider();
        var middleware = provider.GetService<PartitionerMiddleware>();
        middleware.Should().NotBeNull();
    }

    [Fact]
    public void AddPartitionerMiddleware_ValidArguments_CallsAddMiddlewareOnConfigurator()
    {
        var services = new ServiceCollection();
        var busConfigurator = Substitute.For<IBusConfigurator>();

        services.AddPartitionerMiddleware(busConfigurator, partitionCount: 8);

        busConfigurator.Received(1).AddMiddleware<PartitionerMiddleware>();
    }

    [Fact]
    public void AddPartitionerMiddleware_ValidArguments_ReturnsServiceCollection()
    {
        var services = new ServiceCollection();
        var busConfigurator = Substitute.For<IBusConfigurator>();

        var result = services.AddPartitionerMiddleware(busConfigurator, partitionCount: 8);

        result.Should().BeSameAs(services);
    }
}
#pragma warning restore CS0618 // Type or member is obsolete
