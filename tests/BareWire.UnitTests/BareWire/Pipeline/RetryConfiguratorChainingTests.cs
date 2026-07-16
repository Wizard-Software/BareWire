using AwesomeAssertions;
using BareWire.Abstractions.Configuration;
using BareWire.Pipeline.Retry;

namespace BareWire.UnitTests.Core.Pipeline;

public sealed class RetryConfiguratorChainingTests
{
    // 19.2 (I-3 ACCEPT-chain): every configurator method returns the same IRetryConfigurator
    // instance, so calls can be chained fluently.
    [Fact]
    public void Interval_ThenHandle_ThenIgnore_ReturnsChainableConfigurator()
    {
        var sut = new RetryConfigurator();

        IRetryConfigurator chained = sut
            .Interval(3, TimeSpan.FromSeconds(1))
            .Handle<InvalidOperationException>()
            .Ignore<ArgumentException>();

        chained.Should().BeSameAs(sut);
    }

    // 19.2: the public contract lives in the zero-dep Abstractions layer and is public.
    [Fact]
    public void PublicContract_LivesInAbstractions_AndIsPublic()
    {
        typeof(IRetryConfigurator).Namespace
            .Should().Be("BareWire.Abstractions.Configuration");
        typeof(IRetryConfigurator).IsPublic
            .Should().BeTrue();
    }
}
