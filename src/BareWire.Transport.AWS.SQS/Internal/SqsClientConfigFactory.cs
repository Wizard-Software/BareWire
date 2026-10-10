using Amazon;
using Amazon.SQS;

namespace BareWire.Transport.AWS.SQS.Internal;

/// <summary>Builds the <see cref="AmazonSQSConfig"/> for the adapter's SQS client.</summary>
internal static class SqsClientConfigFactory
{
    internal static AmazonSQSConfig Create(SqsTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var config = new AmazonSQSConfig();
        bool hasServiceUrl = !string.IsNullOrEmpty(options.ServiceUrl);

        if (hasServiceUrl)
        {
            config.ServiceURL = options.ServiceUrl;
        }

        if (!string.IsNullOrEmpty(options.RegionEndpoint))
        {
            if (hasServiceUrl)
            {
                // A custom service URL already fixes the endpoint, so the region is only needed for request
                // signing. Setting RegionEndpoint as well makes the SDK spend roughly 0.75 s each in its
                // endpoint-resolution and signing handlers on every call (measured against LocalStack), which
                // under load exhausts the shutdown release budget before the request is even sent.
                config.AuthenticationRegion = options.RegionEndpoint;
            }
            else
            {
                config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.RegionEndpoint);
            }
        }

        return config;
    }
}
