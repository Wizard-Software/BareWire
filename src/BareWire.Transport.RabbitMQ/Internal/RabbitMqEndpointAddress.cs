using System.Text;

namespace BareWire.Transport.RabbitMQ.Internal;

/// <summary>
/// Builds MassTransit-compatible <c>rabbitmq://</c> endpoint address URIs from RabbitMQ
/// connection data, following the native RabbitMQ URI scheme that MassTransit adopts.
/// </summary>
/// <remarks>
/// <para>
/// The produced addresses are embedded into message envelopes as <c>responseAddress</c>,
/// <c>destinationAddress</c>, and <c>faultAddress</c> fields. Because these addresses travel
/// on the wire and may be logged by remote systems, credentials are never included in the output.
/// </para>
/// <para>
/// <b>SEC-1 (D1) — Credential stripping (mandatory):</b> This builder constructs the URI
/// exclusively from <c>uri.Host</c>, <c>uri.Port</c>, and the vhost argument.
/// The <c>uri.UserInfo</c> and <c>uri.Authority</c> components (which carry
/// <c>user:password@host</c> when the AMQP connection string includes credentials) are
/// deliberately ignored. A unit test (<c>Build_WhenConnectionStringHasCredentials_OmitsUserInfo</c>)
/// enforces this invariant.
/// </para>
/// <para>
/// <b>Address format:</b>
/// <list type="bullet">
///   <item>Default vhost <c>/</c>: <c>rabbitmq://host/name</c> (no vhost segment)</item>
///   <item>Named vhost: <c>rabbitmq://host/vhost/name</c></item>
///   <item>Non-default port: <c>rabbitmq://host:port/[vhost/]name</c></item>
///   <item>Temporary (server-named) queue: <c>rabbitmq://host/[vhost/]name?temporary=true</c></item>
/// </list>
/// Both the vhost and the queue name are percent-encoded to handle special characters safely
/// (server-named queues use characters such as <c>+</c>, <c>=</c>, and <c>/</c>).
/// </para>
/// </remarks>
internal static class RabbitMqEndpointAddress
{
    private const string Scheme = "rabbitmq";
    private const int DefaultAmqpPort = 5672;
    private const string TemporaryQueryParam = "?temporary=true";
    private const string DefaultVhost = "/";

    /// <summary>
    /// Builds the MassTransit-compatible <c>amq.rabbitmq.reply-to</c> response address that
    /// must be embedded in the <c>responseAddress</c> envelope field when targeting a MassTransit
    /// responder via the RabbitMQ Direct Reply-To mechanism.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>MT interop — why this address form is required:</b>
    /// When MassTransit's <c>ConsumeContext.RespondAsync()</c> is called, MT resolves the send
    /// endpoint from the envelope's <c>responseAddress</c> field.  If that address contains a
    /// server-named queue (e.g. <c>rabbitmq://host/amq.gen-xxx?temporary=true</c>), MT's
    /// <c>RabbitMqSendSettings.GetBrokerTopology()</c> declares a fanout exchange with that name
    /// and publishes to it — but the exclusive reply queue has no binding to that exchange, so
    /// the response is silently dropped.
    /// </para>
    /// <para>
    /// The fix: embed <c>rabbitmq://host[:port]/[vhost/]amq.rabbitmq.reply-to</c> as the
    /// <c>responseAddress</c>.  MT's <c>IsReplyToAddress()</c> detects the
    /// <c>amq.rabbitmq.reply-to</c> suffix and wraps the endpoint with
    /// <c>ReplyToSendEndpoint</c>, which publishes to the default AMQP exchange
    /// (<c>""</c>) with <c>routingKey</c> taken from the incoming AMQP <c>ReplyTo</c>
    /// property — which BareWire already sets to the actual exclusive reply-queue name in
    /// <see cref="RabbitMqRequestClient{TRequest}.SerializeAndPublishAsync"/>.  The response
    /// therefore arrives at the correct exclusive queue.
    /// </para>
    /// <para>
    /// <b>SEC-1 (D1):</b> Same credential-stripping rules as <see cref="Build"/> apply —
    /// only <c>Host</c>, <c>Port</c>, and vhost are used.
    /// </para>
    /// </remarks>
    /// <param name="connectionUri">
    /// The AMQP connection URI. Only <c>Host</c> and <c>Port</c> are used; credentials are stripped.
    /// </param>
    /// <param name="vhost">
    /// The RabbitMQ virtual host. Pass <c>/</c> or <see langword="null"/> for the default vhost.
    /// Named vhosts are percent-encoded and included as the first path segment.
    /// </param>
    /// <returns>A <c>rabbitmq://host[:port]/[vhost/]amq.rabbitmq.reply-to</c> address string.</returns>
    internal static string BuildReplyToAddress(Uri connectionUri, string? vhost)
    {
        ArgumentNullException.ThrowIfNull(connectionUri);

        // SEC-1 / D1: use ONLY Host and Port — never UserInfo or Authority.
        string host = connectionUri.Host;
        int port = connectionUri.Port;

        var sb = new StringBuilder(capacity: 96);
        sb.Append(Scheme);
        sb.Append("://");
        sb.Append(host);

        if (port > 0 && port != DefaultAmqpPort)
        {
            sb.Append(':');
            sb.Append(port);
        }

        sb.Append('/');

        bool isDefaultVhost = string.IsNullOrEmpty(vhost) || vhost == DefaultVhost;
        if (!isDefaultVhost)
        {
            sb.Append(Uri.EscapeDataString(vhost!));
            sb.Append('/');
        }

        // MT's magic constant: ends with this value → IsReplyToAddress() returns true →
        // MT routes via default exchange using the AMQP ReplyTo header as routing key.
        sb.Append("amq.rabbitmq.reply-to");

        return sb.ToString();
    }

    /// <summary>
    /// Builds a <c>rabbitmq://</c> endpoint address URI.
    /// </summary>
    /// <param name="connectionUri">
    /// The AMQP connection URI (e.g. <c>amqp://user:pass@host:5672/vhost</c>). Only the
    /// <c>Host</c> and <c>Port</c> components are used; credentials are stripped.
    /// </param>
    /// <param name="vhost">
    /// The RabbitMQ virtual host. Pass <c>/</c> (or <see langword="null"/>) for the default
    /// vhost — it will be omitted from the output URI. Named vhosts are percent-encoded and
    /// included as the first path segment.
    /// </param>
    /// <param name="name">
    /// The queue or exchange name to address. Will be percent-encoded in the output.
    /// </param>
    /// <param name="temporary">
    /// When <see langword="true"/>, appends <c>?temporary=true</c> to the URI. Use this for
    /// server-named exclusive auto-delete reply queues.
    /// </param>
    /// <returns>A fully-formed <c>rabbitmq://</c> address string.</returns>
    internal static string Build(Uri connectionUri, string? vhost, string name, bool temporary = false)
    {
        ArgumentNullException.ThrowIfNull(connectionUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // SEC-1 / D1: use ONLY Host and Port — never UserInfo or Authority.
        // connectionUri.Host strips credentials; connectionUri.Authority includes "user:pass@host"
        // when the input URI carries credentials, so we must NOT use Authority here.
        string host = connectionUri.Host;
        int port = connectionUri.Port;

        var sb = new StringBuilder(capacity: 128);
        sb.Append(Scheme);
        sb.Append("://");
        sb.Append(host);

        // Include port only when it differs from the standard AMQP port.
        if (port > 0 && port != DefaultAmqpPort)
        {
            sb.Append(':');
            sb.Append(port);
        }

        sb.Append('/');

        // Include named vhost as the first path segment; omit the default '/'.
        bool isDefaultVhost = string.IsNullOrEmpty(vhost) || vhost == DefaultVhost;
        if (!isDefaultVhost)
        {
            sb.Append(Uri.EscapeDataString(vhost!));
            sb.Append('/');
        }

        sb.Append(Uri.EscapeDataString(name));

        if (temporary)
        {
            sb.Append(TemporaryQueryParam);
        }

        return sb.ToString();
    }
}
