namespace BareWire.IntegrationTests.Transport;

/// <summary>
/// Integration tests for TLS and mTLS connections to RabbitMQ.
/// </summary>
/// <remarks>
/// These tests require a TLS-enabled RabbitMQ container configured with:
/// <list type="bullet">
///   <item>A valid server certificate (self-signed is sufficient for tests).</item>
///   <item>Optional: mutual TLS (mTLS) with client certificate verification.</item>
/// </list>
/// The Aspire AppHost must be extended with a custom RabbitMQ image or configuration
/// that enables TLS (e.g. via <c>rabbitmq.conf</c> and mounted certificate files).
///
/// All tests in this class are tagged with <c>Category=TLS</c> so they can be
/// selectively skipped in CI environments that do not provide a TLS-enabled broker:
/// <code>
/// dotnet test --filter "Category!=TLS"
/// </code>
/// </remarks>
[Trait("Category", "TLS")]
public sealed class RabbitMqTlsTests
{
    // TODO (Task 3.7 integration): Implement the following test scenarios once a
    // TLS-enabled RabbitMQ container is available in the Aspire AppHost:
    //
    // ConnectWithTls_SelfSignedCert_Succeeds
    //   → Configure ITlsConfigurator with self-signed server cert + AcceptablePolicyErrors.RemoteCertificateChainErrors
    //   → Verify connection to amqps://... succeeds and adapter.TransportName == "RabbitMQ"
    //
    // ConnectWithTls_InvalidCert_ThrowsBareWireTransportException
    //   → Configure ITlsConfigurator with wrong CA / expired cert
    //   → Verify BareWireTransportException is thrown on first send
    //
    // ConnectWithMutualTls_ValidClientCert_Succeeds
    //   → Configure ITlsConfigurator with WithCertificate + WithMutualAuthentication
    //   → Verify mTLS handshake completes and messages can be published/consumed
}
