using Amazon;
using AwesomeAssertions;
using BareWire.Transport.AWS.SQS;
using BareWire.Transport.AWS.SQS.Internal;
using Xunit;

namespace BareWire.UnitTests.Transport.Sqs;

public sealed class SqsClientConfigFactoryTests
{
    [Fact]
    public void Create_WithServiceUrlAndRegion_SignsWithAuthenticationRegionOnly()
    {
        var options = new SqsTransportOptions { ServiceUrl = "http://localhost:4566", RegionEndpoint = "us-east-1" };

        var config = SqsClientConfigFactory.Create(options);

        config.ServiceURL.Should().StartWith("http://localhost:4566");
        config.AuthenticationRegion.Should().Be("us-east-1");
        config.RegionEndpoint.Should().BeNull(
            "combining RegionEndpoint with a custom ServiceURL makes every SDK call spend ~1.5 s before the request is sent");
    }

    [Fact]
    public void Create_WithRegionAndNoServiceUrl_UsesRegionEndpoint()
    {
        var options = new SqsTransportOptions { RegionEndpoint = "eu-central-1" };

        var config = SqsClientConfigFactory.Create(options);

        config.RegionEndpoint.Should().Be(RegionEndpoint.EUCentral1);
        config.ServiceURL.Should().BeNull();
    }
}
