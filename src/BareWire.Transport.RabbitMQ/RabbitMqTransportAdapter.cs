using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.RabbitMQ.Configuration;
using BareWire.Transport.RabbitMQ.Internal;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using BwExchangeType = BareWire.Abstractions.ExchangeType;

namespace BareWire.Transport.RabbitMQ;

internal sealed partial class RabbitMqTransportAdapter : ITransportAdapter, IConsumerChannelManager, IAsyncDisposable
{
    private readonly RabbitMqTransportOptions _options;
    private readonly ILogger<RabbitMqTransportAdapter> _logger;
    private readonly RabbitMqHeaderMapper _headerMapper;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();

    // Maps endpoint name → the IChannel used for that consumer, so SettleAsync can ACK/NACK
    // messages on the correct channel without threading channel references through InboundMessage.
    private readonly ConcurrentDictionary<string, IChannel> _activeConsumerChannels =
        new(StringComparer.Ordinal);

    private long _deliveryTagCounter;
    private IConnection? _connection;
    private bool _disposed;

    public RabbitMqTransportAdapter(
        RabbitMqTransportOptions options,
        ILogger<RabbitMqTransportAdapter> logger,
        RabbitMqHeaderMapper? headerMapper = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        options.Validate();

        _options = options;
        _logger = logger;
        _headerMapper = headerMapper ?? new RabbitMqHeaderMapper();
    }

    public string TransportName => "RabbitMQ";

    public TransportCapabilities Capabilities =>
        TransportCapabilities.PublisherConfirms |
        TransportCapabilities.DlqNative |
        TransportCapabilities.FlowControl;

    public async Task<IReadOnlyList<SendResult>> SendBatchAsync(
        IReadOnlyList<OutboundMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            throw new BareWireTransportException(
                message: "Failed to establish connection before sending batch.",
                transportName: TransportName,
                endpointAddress: null,
                innerException: ex);
        }

        var channelOptions = new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true);

        IChannel channel = await _connection!
            .CreateChannelAsync(channelOptions, cancellationToken)
            .ConfigureAwait(false);

        var results = new SendResult[messages.Count];

        try
        {
            for (int i = 0; i < messages.Count; i++)
            {
                OutboundMessage outbound = messages[i];

                bool hasExchangeHeader = outbound.Headers.TryGetValue("BW-Exchange", out string? headerExchange);
                string exchange = hasExchangeHeader
                    ? headerExchange!
                    : _options.DefaultExchange;

                // Fail-fast: throw only when the caller did NOT supply a BW-Exchange header AND no
                // DefaultExchange is configured. If the header is present (even as ""), honour it —
                // BareWireSendEndpoint uses BW-Exchange="" for the queue: scheme (AMQP default exchange).
                if (!hasExchangeHeader && string.IsNullOrEmpty(_options.DefaultExchange))
                {
                    throw new BareWireConfigurationException(
                        optionName: "Exchange",
                        optionValue: exchange,
                        expectedValue: "No exchange resolved for the outbound message. " +
                                       "Configure a DefaultExchange, call MapExchange<T>(), " +
                                       "or pass the BW-Exchange header on each publish call per ADR-002.");
                }

                ulong deliveryTag = (ulong)Interlocked.Increment(ref _deliveryTagCounter);

                (BasicProperties props, Dictionary<string, object?> amqpHeaders) =
                    _headerMapper.MapOutbound(outbound.Headers);

                // ContentType is always set from the OutboundMessage's dedicated field
                // (takes precedence over any "content-type" header in the dictionary).
                if (!string.IsNullOrEmpty(outbound.ContentType))
                {
                    props.ContentType = outbound.ContentType;
                }

                if (amqpHeaders.Count > 0)
                {
                    props.Headers = amqpHeaders;
                }

                try
                {
                    await channel.BasicPublishAsync<BasicProperties>(
                        exchange: exchange,
                        routingKey: outbound.RoutingKey,
                        mandatory: false,
                        basicProperties: props,
                        body: outbound.Body,
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    results[i] = new SendResult(IsConfirmed: true, DeliveryTag: deliveryTag);
                }
                catch (PublishException)
                {
                    // Broker sent a Nack — message was not durably stored. Continue the batch.
                    results[i] = new SendResult(IsConfirmed: false, DeliveryTag: deliveryTag);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BareWireTransportException(
                message: "Connection-level failure during batch send.",
                transportName: TransportName,
                endpointAddress: null,
                innerException: ex);
        }
        finally
        {
            try
            {
                await channel.CloseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogPublishChannelCloseError(ex);
            }

            await channel.DisposeAsync().ConfigureAwait(false);
        }

        return results;
    }

    public async IAsyncEnumerable<InboundMessage> ConsumeAsync(
        string endpointName,
        FlowControlOptions flowControl,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpointName);
        ArgumentNullException.ThrowIfNull(flowControl);
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            throw new BareWireTransportException(
                message: $"Failed to establish connection before consuming from '{endpointName}'.",
                transportName: TransportName,
                endpointAddress: null,
                innerException: ex);
        }

        IChannel consumerChannel = await _connection!
            .CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: false,
                    publisherConfirmationTrackingEnabled: false),
                cancellationToken)
            .ConfigureAwait(false);

        await consumerChannel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: (ushort)Math.Min(flowControl.MaxInFlightMessages, ushort.MaxValue),
            global: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        Channel<InboundMessage> inboundChannel = Channel.CreateBounded<InboundMessage>(
            new BoundedChannelOptions(flowControl.InternalQueueCapacity)
            {
                FullMode = flowControl.FullMode,
                SingleWriter = true,
                SingleReader = false,
            });

        // Each ConsumeAsync invocation gets a unique key so multiple consumers on the same
        // endpoint (round-robin) do not overwrite each other's channel in the dictionary.
        string consumerChannelId = Guid.NewGuid().ToString("N");
        var consumer = new RabbitMqConsumer(consumerChannel, inboundChannel, _headerMapper, consumerChannelId);

        // Register the channel so SettleAsync can resolve it by consumer channel ID.
        _activeConsumerChannels[consumerChannelId] = consumerChannel;

        string assignedTag = string.Empty;
        try
        {
            assignedTag = await consumerChannel.BasicConsumeAsync(
                queue: endpointName,
                autoAck: false,
                consumerTag: string.Empty,
                noLocal: false,
                exclusive: false,
                arguments: null,
                consumer: consumer,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await foreach (InboundMessage message in inboundChannel.Reader
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return message;
            }
        }
        finally
        {
            // Stop the broker from pushing new messages to this consumer, but keep the channel
            // open so the caller can still settle (ACK/NACK) in-flight messages via SettleAsync.
            // The channel lifecycle after this point:
            //   - Normal path: the consumer pipeline calls IConsumerChannelManager.ReleaseConsumerChannelAsync
            //     once all settlements are complete, which removes and closes the channel.
            //   - Fallback: DisposeAsync closes any channels that were not explicitly released
            //     (e.g. if the adapter is torn down before the pipeline calls Release).
            inboundChannel.Writer.TryComplete();

            try
            {
                if (consumerChannel.IsOpen)
                {
                    await consumerChannel.BasicCancelAsync(assignedTag, noWait: false, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                LogConsumeChannelCloseError(endpointName, ex);
            }

        }
    }

    public async Task SettleAsync(
        SettlementAction action,
        InboundMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(_disposed, this);

        IChannel? channel = ResolveChannelForMessage(message);

        if (channel is null)
        {
            throw new BareWireTransportException(
                message: $"No active consumer channel found for delivery tag {message.DeliveryTag}. " +
                         "The consumer may have been cancelled before settlement.",
                transportName: TransportName,
                endpointAddress: null);
        }

        switch (action)
        {
            case SettlementAction.Ack:
                await channel.BasicAckAsync(
                    deliveryTag: message.DeliveryTag,
                    multiple: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                break;

            case SettlementAction.Nack:
                await channel.BasicNackAsync(
                    deliveryTag: message.DeliveryTag,
                    multiple: false,
                    requeue: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                break;

            case SettlementAction.Reject:
                await channel.BasicRejectAsync(
                    deliveryTag: message.DeliveryTag,
                    requeue: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                break;

            case SettlementAction.Requeue:
                await channel.BasicNackAsync(
                    deliveryTag: message.DeliveryTag,
                    multiple: false,
                    requeue: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                break;

            case SettlementAction.Defer:
                if (!_options.DeferEnabled)
                {
                    throw new NotSupportedException(
                        "SettlementAction.Defer requires DeferEnabled = true in RabbitMqTransportOptions. " +
                        "Enable it and deploy the corresponding DLQ+TTL topology before using Defer.");
                }

                // Nack without requeue — relies on the DLQ+TTL topology for deferred re-delivery.
                await channel.BasicNackAsync(
                    deliveryTag: message.DeliveryTag,
                    multiple: false,
                    requeue: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(action), action, $"Unknown {nameof(SettlementAction)}: {action}.");
        }
    }

    public async Task DeployTopologyAsync(
        TopologyDeclaration topology,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topology);

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        IChannel channel = await _connection!.CreateChannelAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        try
        {
            foreach (ExchangeDeclaration exchange in topology.Exchanges)
            {
                try
                {
                    await channel.ExchangeDeclareAsync(
                        exchange: exchange.Name,
                        type: ToRabbitMqExchangeType(exchange.Type),
                        durable: exchange.Durable,
                        autoDelete: exchange.AutoDelete,
                        arguments: null,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (OperationInterruptedException ex)
                {
                    throw new TopologyDeploymentException(
                        topologyElement: exchange.Name,
                        transportName: TransportName,
                        brokerError: ex.ShutdownReason?.ReplyText,
                        endpointAddress: null,
                        innerException: ex);
                }
            }

            foreach (QueueDeclaration queue in topology.Queues)
            {
                try
                {
                    await channel.QueueDeclareAsync(
                        queue: queue.Name,
                        durable: queue.Durable,
                        exclusive: queue.Exclusive,
                        autoDelete: queue.AutoDelete,
                        arguments: queue.Arguments?.ToDictionary(
                            kvp => kvp.Key, kvp => (object?)kvp.Value),
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (OperationInterruptedException ex)
                {
                    throw new TopologyDeploymentException(
                        topologyElement: queue.Name,
                        transportName: TransportName,
                        brokerError: ex.ShutdownReason?.ReplyText,
                        endpointAddress: null,
                        innerException: ex);
                }
            }

            foreach (ExchangeQueueBinding binding in topology.ExchangeQueueBindings)
            {
                try
                {
                    await channel.QueueBindAsync(
                        queue: binding.QueueName,
                        exchange: binding.ExchangeName,
                        routingKey: binding.RoutingKey,
                        arguments: null,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (OperationInterruptedException ex)
                {
                    throw new TopologyDeploymentException(
                        topologyElement: $"{binding.ExchangeName} -> {binding.QueueName} [{binding.RoutingKey}]",
                        transportName: TransportName,
                        brokerError: ex.ShutdownReason?.ReplyText,
                        endpointAddress: null,
                        innerException: ex);
                }
            }

            foreach (ExchangeExchangeBinding binding in topology.ExchangeExchangeBindings)
            {
                try
                {
                    await channel.ExchangeBindAsync(
                        destination: binding.DestinationExchangeName,
                        source: binding.SourceExchangeName,
                        routingKey: binding.RoutingKey,
                        arguments: null,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (OperationInterruptedException ex)
                {
                    throw new TopologyDeploymentException(
                        topologyElement: $"{binding.SourceExchangeName} -> {binding.DestinationExchangeName} [{binding.RoutingKey}]",
                        transportName: TransportName,
                        brokerError: ex.ShutdownReason?.ReplyText,
                        endpointAddress: null,
                        innerException: ex);
                }
            }
        }
        finally
        {
            await channel.CloseAsync(cancellationToken).ConfigureAwait(false);
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private IChannel? ResolveChannelForMessage(InboundMessage message)
    {
        // Primary path: look up by the unique consumer channel ID stamped on each message.
        if (message.Headers.TryGetValue("BW-ConsumerChannelId", out string? channelId) &&
            !string.IsNullOrEmpty(channelId) &&
            _activeConsumerChannels.TryGetValue(channelId, out IChannel? channelById))
        {
            return channelById;
        }

        // Fast path: exactly one active consumer — use its channel directly.
        if (_activeConsumerChannels.Count == 1)
        {
            foreach (IChannel ch in _activeConsumerChannels.Values)
            {
                return ch;
            }
        }

        // Fallback: return any open channel.
        foreach (IChannel ch in _activeConsumerChannels.Values)
        {
            if (ch.IsOpen)
            {
                return ch;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task ReleaseConsumerChannelAsync(string channelId, CancellationToken cancellationToken = default)
    {
        if (!_activeConsumerChannels.TryRemove(channelId, out IChannel? channel))
        {
            // Already released or never registered — no-op (idempotent).
            return;
        }

        try
        {
            if (channel.IsOpen)
            {
                await channel.CloseAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            LogConsumeChannelCloseError(channelId, ex);
        }

        await channel.DisposeAsync().ConfigureAwait(false);
    }

    private static string ToRabbitMqExchangeType(BwExchangeType exchangeType) =>
        exchangeType switch
        {
            BwExchangeType.Direct  => "direct",
            BwExchangeType.Fanout  => "fanout",
            BwExchangeType.Topic   => "topic",
            BwExchangeType.Headers => "headers",
            _ => throw new ArgumentOutOfRangeException(nameof(exchangeType), exchangeType, "Unknown exchange type."),
        };

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _disposeCts.CancelAsync().ConfigureAwait(false);

        // Close consumer channels that were intentionally kept alive for post-iteration settlement.
        foreach (KeyValuePair<string, IChannel> kvp in _activeConsumerChannels)
        {
            try
            {
                await kvp.Value.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogConsumeChannelCloseError(kvp.Key, ex);
            }

            await kvp.Value.DisposeAsync().ConfigureAwait(false);
        }

        _activeConsumerChannels.Clear();

        if (_connection is not null)
        {
            try
            {
                await _connection.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogConnectionCloseError(ex);
            }

            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }

        _connectionLock.Dispose();
        _disposeCts.Dispose();
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null && _connection.IsOpen)
        {
            return;
        }

        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the lock — another caller may have connected.
            if (_connection is not null && _connection.IsOpen)
            {
                return;
            }

            var uri = new Uri(_options.ConnectionString);

            var factory = new ConnectionFactory
            {
                Uri = uri,
                AutomaticRecoveryEnabled = _options.AutomaticRecoveryEnabled,
                NetworkRecoveryInterval = _options.NetworkRecoveryInterval,
            };

            if (_options.ConfigureTls is not null)
            {
                string serverName = uri.Host;
                var tlsConfigurator = new RabbitMqTlsConfigurator();
                _options.ConfigureTls(tlsConfigurator);
                factory.Ssl = tlsConfigurator.Build(serverName);
            }
            else if (_options.SslOptions is not null)
            {
                factory.Ssl = _options.SslOptions;
            }
            else if (uri.Scheme.Equals("amqps", StringComparison.OrdinalIgnoreCase))
            {
                LogAmqpsWithoutTlsConfig();
            }

            _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);

            _connection.ConnectionShutdownAsync += OnConnectionShutdownAsync;
            _connection.RecoverySucceededAsync += OnRecoverySucceededAsync;

            string host = _connection.Endpoint.HostName;
            int port = _connection.Endpoint.Port;
            LogConnectionEstablished(host, port);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private Task OnConnectionShutdownAsync(object sender, ShutdownEventArgs args)
    {
        LogConnectionShutdown(args.Cause?.ToString() ?? "unknown", args.ReplyText ?? string.Empty);
        return Task.CompletedTask;
    }

    private Task OnRecoverySucceededAsync(object sender, AsyncEventArgs args)
    {
        LogRecoverySucceeded();
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "RabbitMQ connection established to {Host}:{Port}.")]
    private partial void LogConnectionEstablished(string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RabbitMQ connection shut down. Cause: {Cause}. ReplyText: {ReplyText}.")]
    private partial void LogConnectionShutdown(string cause, string replyText);

    [LoggerMessage(Level = LogLevel.Information, Message = "RabbitMQ connection recovery succeeded.")]
    private partial void LogRecoverySucceeded();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Exception while closing RabbitMQ connection during dispose.")]
    private partial void LogConnectionCloseError(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Exception while closing publish channel.")]
    private partial void LogPublishChannelCloseError(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Exception while closing consumer channel for endpoint '{EndpointName}'.")]
    private partial void LogConsumeChannelCloseError(string endpointName, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Connection URI uses 'amqps://' but no TLS configuration was provided via ConfigureTls or SslOptions. " +
                  "The connection may fail if the broker requires TLS. " +
                  "Call ConfigureTls on RabbitMqTransportOptions to configure TLS explicitly.")]
    private partial void LogAmqpsWithoutTlsConfig();
}
