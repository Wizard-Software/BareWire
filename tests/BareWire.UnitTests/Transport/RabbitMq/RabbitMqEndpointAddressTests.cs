using AwesomeAssertions;
using BareWire.Transport.RabbitMQ.Internal;

namespace BareWire.UnitTests.Transport.RabbitMq;

/// <summary>
/// Unit tests for <see cref="RabbitMqEndpointAddress"/> URI builder.
/// Verifies correct <c>rabbitmq://</c> URI construction and the SEC-1 requirement
/// that credentials are never included in produced addresses.
/// </summary>
public sealed class RabbitMqEndpointAddressTests
{
    // ── Default vhost ─────────────────────────────────────────────────────────

    [Fact]
    public void Build_WithDefaultVhost_OmitsVhostSegment()
    {
        // Arrange
        var connectionUri = new Uri("amqp://localhost");

        // Act
        string address = RabbitMqEndpointAddress.Build(connectionUri, vhost: "/", name: "my-queue");

        // Assert — default vhost '/' must NOT produce an extra path segment
        address.Should().Be("rabbitmq://localhost/my-queue");
    }

    [Fact]
    public void Build_WithDefaultVhostPort5672_OmitsDefaultPort()
    {
        // Arrange
        var connectionUri = new Uri("amqp://localhost:5672");

        // Act
        string address = RabbitMqEndpointAddress.Build(connectionUri, vhost: "/", name: "orders");

        // Assert — standard AMQP port 5672 is the default; omit it for clean URIs
        address.Should().Be("rabbitmq://localhost/orders");
    }

    // ── Named vhost ───────────────────────────────────────────────────────────

    [Fact]
    public void Build_WithNamedVhost_IncludesVhostSegment()
    {
        // Arrange
        var connectionUri = new Uri("amqp://localhost");

        // Act
        string address = RabbitMqEndpointAddress.Build(connectionUri, vhost: "staging", name: "orders");

        // Assert — named vhost appears as the first path segment
        address.Should().Be("rabbitmq://localhost/staging/orders");
    }

    [Fact]
    public void Build_WithNamedVhostAndNonDefaultPort_IncludesPort()
    {
        // Arrange
        var connectionUri = new Uri("amqp://rabbit-host:5673");

        // Act
        string address = RabbitMqEndpointAddress.Build(connectionUri, vhost: "prod", name: "payments");

        // Assert — non-standard port must be included in the authority
        address.Should().Be("rabbitmq://rabbit-host:5673/prod/payments");
    }

    // ── Temporary (server-named response) queues ──────────────────────────────

    [Fact]
    public void Build_WithTemporaryTrue_AppendsTemporaryQueryParam()
    {
        // Arrange
        var connectionUri = new Uri("amqp://localhost");

        // Act
        string address = RabbitMqEndpointAddress.Build(connectionUri, vhost: "/", name: "amq.gen-AbCdEfGh", temporary: true);

        // Assert — response queue addresses carry ?temporary=true
        address.Should().Be("rabbitmq://localhost/amq.gen-AbCdEfGh?temporary=true");
    }

    [Fact]
    public void Build_WithTemporaryFalse_OmitsQueryParam()
    {
        // Arrange
        var connectionUri = new Uri("amqp://localhost");

        // Act
        string address = RabbitMqEndpointAddress.Build(connectionUri, vhost: "/", name: "my-queue", temporary: false);

        // Assert — destination addresses must not carry the temporary flag
        address.Should().NotContain("temporary");
    }

    // ── URL-encoding ──────────────────────────────────────────────────────────

    [Fact]
    public void Build_WithServerNamedQueueSpecialChars_UrlEncodesName()
    {
        // Arrange — server-named queues use chars like '+', '=', '/'
        var connectionUri = new Uri("amqp://localhost");

        // Act
        string address = RabbitMqEndpointAddress.Build(connectionUri, vhost: "/", name: "amq.gen-Abc+XYZ=foo/bar", temporary: true);

        // Assert — special chars in the queue name must be percent-encoded.
        // '+' → '%2B', '=' → '%3D', '/' → '%2F'. The '=' in '?temporary=true' is expected.
        address.Should().Contain("%2B");   // encoded '+'
        address.Should().Contain("%3D");   // encoded '='
        address.Should().Contain("%2F");   // encoded '/'
        address.Should().NotContain("+");  // raw '+' must not appear
    }

    [Fact]
    public void Build_WithVhostContainingSpecialChars_UrlEncodesVhost()
    {
        // Arrange — vhost names may contain slashes and other special characters
        var connectionUri = new Uri("amqp://localhost");

        // Act
        string address = RabbitMqEndpointAddress.Build(connectionUri, vhost: "my/vhost", name: "orders");

        // Assert — the vhost segment must be percent-encoded
        address.Should().NotContain("my/vhost"); // raw slash in vhost would be ambiguous
        address.Should().Contain("my%2Fvhost");
    }

    // ── SEC-1: credentials must never leak into the envelope address ──────────

    [Fact]
    public void Build_WhenConnectionStringHasCredentials_OmitsUserInfo()
    {
        // Arrange — connection strings typically include user:pass
        var connectionUri = new Uri("amqp://admin:s3cr3t@rabbit-host:5672/prod");

        // Act — the builder must strip credentials before building the address
        string address = RabbitMqEndpointAddress.Build(connectionUri, vhost: "prod", name: "orders");

        // Assert — credentials must NOT appear in the envelope address (D1 / SEC-1)
        address.Should().NotContain("admin");
        address.Should().NotContain("s3cr3t");
        address.Should().NotContain("admin:s3cr3t");
        address.Should().StartWith("rabbitmq://rabbit-host");
    }

    [Fact]
    public void Build_WhenConnectionStringHasCredentials_ProducesCorrectUri()
    {
        // Arrange
        var connectionUri = new Uri("amqp://user:pass@myhost/myvhost");

        // Act
        string address = RabbitMqEndpointAddress.Build(connectionUri, vhost: "myvhost", name: "queue-name");

        // Assert — only host and vhost, no user info
        address.Should().Be("rabbitmq://myhost/myvhost/queue-name");
    }

    // ── Scheme ────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_Always_ProducesRabbitmqScheme()
    {
        // Arrange
        var connectionUri = new Uri("amqp://localhost");

        // Act
        string address = RabbitMqEndpointAddress.Build(connectionUri, vhost: "/", name: "test");

        // Assert — output scheme must always be 'rabbitmq', never 'amqp'
        address.Should().StartWith("rabbitmq://");
    }

    // ── BuildReplyToAddress — MT direct reply-to (GH #19) ────────────────────

    /// <summary>
    /// The produced address must end with the MT magic constant <c>amq.rabbitmq.reply-to</c>.
    /// MT's <c>IsReplyToAddress()</c> checks for this suffix to activate <c>ReplyToSendEndpoint</c>
    /// routing (default exchange + AMQP ReplyTo header as routing key).
    /// </summary>
    [Fact]
    public void BuildReplyToAddress_WithDefaultVhost_EndsWithMtReplyToExchangeName()
    {
        // Arrange
        var connectionUri = new Uri("amqp://localhost");

        // Act
        string address = RabbitMqEndpointAddress.BuildReplyToAddress(connectionUri, vhost: "/");

        // Assert
        address.Should().Be("rabbitmq://localhost/amq.rabbitmq.reply-to");
    }

    [Fact]
    public void BuildReplyToAddress_WithNamedVhost_IncludesVhostSegment()
    {
        // Arrange
        var connectionUri = new Uri("amqp://localhost");

        // Act
        string address = RabbitMqEndpointAddress.BuildReplyToAddress(connectionUri, vhost: "staging");

        // Assert — vhost segment before the reply-to name
        address.Should().Be("rabbitmq://localhost/staging/amq.rabbitmq.reply-to");
    }

    [Fact]
    public void BuildReplyToAddress_WithNonDefaultPort_IncludesPort()
    {
        // Arrange
        var connectionUri = new Uri("amqp://rabbit-host:5673");

        // Act
        string address = RabbitMqEndpointAddress.BuildReplyToAddress(connectionUri, vhost: "/");

        // Assert — non-standard port must be included
        address.Should().Be("rabbitmq://rabbit-host:5673/amq.rabbitmq.reply-to");
    }

    [Fact]
    public void BuildReplyToAddress_WithDefaultPort5672_OmitsPort()
    {
        // Arrange
        var connectionUri = new Uri("amqp://localhost:5672");

        // Act
        string address = RabbitMqEndpointAddress.BuildReplyToAddress(connectionUri, vhost: "/");

        // Assert — standard AMQP port must be omitted
        address.Should().Be("rabbitmq://localhost/amq.rabbitmq.reply-to");
    }

    /// <summary>
    /// The reply-to address must NOT contain <c>?temporary=true</c> — that query param
    /// would cause MT to declare a temporary fanout exchange rather than recognising the
    /// <c>amq.rabbitmq.reply-to</c> suffix for direct reply-to routing.
    /// </summary>
    [Fact]
    public void BuildReplyToAddress_NeverContainsTemporaryQueryParam()
    {
        // Arrange
        var connectionUri = new Uri("amqp://localhost:5673");

        // Act
        string address = RabbitMqEndpointAddress.BuildReplyToAddress(connectionUri, vhost: "prod");

        // Assert
        address.Should().NotContain("temporary",
            because: "?temporary=true on the reply-to address breaks MT's IsReplyToAddress() detection");
    }

    /// <summary>
    /// SEC-1: credentials from the connection string must never appear in the reply-to address.
    /// </summary>
    [Fact]
    public void BuildReplyToAddress_WhenConnectionStringHasCredentials_OmitsCredentials()
    {
        // Arrange
        var connectionUri = new Uri("amqp://admin:s3cr3t@rabbit-host:5672/prod");

        // Act
        string address = RabbitMqEndpointAddress.BuildReplyToAddress(connectionUri, vhost: "prod");

        // Assert — SEC-1: no credentials in the envelope address
        address.Should().NotContain("admin");
        address.Should().NotContain("s3cr3t");
        address.Should().StartWith("rabbitmq://rabbit-host");
        address.Should().EndWith("amq.rabbitmq.reply-to");
    }

    [Fact]
    public void BuildReplyToAddress_WithNullVhost_TreatsAsDefaultVhost()
    {
        // Arrange
        var connectionUri = new Uri("amqp://localhost");

        // Act
        string address = RabbitMqEndpointAddress.BuildReplyToAddress(connectionUri, vhost: null);

        // Assert — null vhost behaves the same as default '/'
        address.Should().Be("rabbitmq://localhost/amq.rabbitmq.reply-to");
    }
}
