using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using BareWire.Abstractions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Topology;
using BareWire.Abstractions.Transport;
using BareWire.Transport.AzureServiceBus.Internal;
using BareWire.Transport.AzureServiceBus.Topology;
using Microsoft.Extensions.Logging;

namespace BareWire.Transport.AzureServiceBus;

/// <summary>
/// Azure Service Bus transport adapter. Implements producer (<see cref="SendBatchAsync"/>),
/// topology deployment (<see cref="DeployTopologyAsync"/>), and lifecycle management.
/// Consumer side (<see cref="ConsumeAsync"/>, <see cref="SettleAsync"/>) is implemented in
/// <c>AzureServiceBusTransportAdapter.Consumer.cs</c>.
/// </summary>
/// <remarks>
/// <para>
/// Uses a single, long-lived, thread-safe <see cref="ServiceBusClient"/> constructed lazily on
/// first use under a <see cref="SemaphoreSlim"/> double-check lock (D-7).
/// </para>
/// <para>
/// <b>Producer body (D-5):</b> <c>OutboundMessage.Body</c> is <see cref="ReadOnlyMemory{T}"/>.
/// <c>BinaryData.FromBytes(ReadOnlyMemory&lt;byte&gt;)</c> wraps the memory without copying —
/// no ADR-003 deviation (unlike Kafka, which must call <c>.ToArray()</c>).
/// </para>
/// <para>
/// <b>Capabilities note (R-1):</b> <see cref="TransportCapabilities.Sessions"/> and
/// <see cref="TransportCapabilities.NativeScheduling"/> are declared in <see cref="Capabilities"/>
/// because the ASB broker natively supports these features. Full BareWire-level support (session
/// receivers, session-id mapping) is implemented in R2.2. Native scheduling
/// (<c>ScheduleMessageAsync</c>) is implemented in R2.3.
/// </para>
/// <para>
/// <b>Authentication (R2.4):</b> Two auth modes are supported, selected via
/// <c>IAzureServiceBusConfigurator</c>:
/// <list type="bullet">
/// <item>
/// <term>SAS (default)</term>
/// <description>
/// Connection-string SAS — <c>UseSasAuth(connectionString)</c>. The connection string is passed
/// directly to the SDK and never logged (SEC-02/SEC-03).
/// </description>
/// </item>
/// <item>
/// <term>Entra ID</term>
/// <description>
/// Azure RBAC / Managed Identity — <c>UseEntraIdAuth(fullyQualifiedNamespace, credential)</c>.
/// A <see cref="Azure.Core.TokenCredential"/> is passed to the SDK constructor; token refresh
/// is handled automatically by the Azure SDK — BareWire does not implement its own refresh loop.
/// The credential object is never logged or serialised (SEC-02/SEC-06); only the namespace host
/// (a non-secret identifier) appears in diagnostic output.
/// </description>
/// </item>
/// </list>
/// Azure Service Bus uses AMQP-over-TLS (port 5671) by default — no credential is transmitted
/// in plaintext.
/// </para>
/// </remarks>
internal sealed partial class AzureServiceBusTransportAdapter : ITransportAdapter, INativeMessageScheduler, IAsyncDisposable
{
    private readonly AzureServiceBusTransportOptions _options;
    private readonly ILogger<AzureServiceBusTransportAdapter> _logger;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    private ServiceBusClient? _client;
    private ServiceBusAdministrationClient? _adminClient;
    private bool _disposed;

    // Sender cache: one sender per routing key (queue/topic name). Thread-safe.
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders =
        new(StringComparer.Ordinal);

    public AzureServiceBusTransportAdapter(
        AzureServiceBusTransportOptions options,
        ILogger<AzureServiceBusTransportAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        options.Validate();

        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string TransportName => "AzureServiceBus";

    /// <inheritdoc />
    /// <remarks>
    /// <b>Capabilities note (R-1):</b>
    /// <list type="bullet">
    /// <item><term><see cref="TransportCapabilities.Sessions"/></term><description>Full session-receiver support arrives in R2.2.</description></item>
    /// <item><term><see cref="TransportCapabilities.NativeScheduling"/></term><description>Full scheduled-message support (<c>ScheduleMessageAsync</c>) is implemented in R2.3.</description></item>
    /// </list>
    /// </remarks>
    public TransportCapabilities Capabilities =>
        TransportCapabilities.NativeDeduplication |
        TransportCapabilities.Sessions |
        TransportCapabilities.NativeScheduling |
        TransportCapabilities.DlqNative;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SendResult>> SendBatchAsync(
        IReadOnlyList<OutboundMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            throw new BareWireTransportException(
                message: "Failed to establish Azure Service Bus client connection before sending batch.",
                transportName: TransportName,
                endpointAddress: null,
                innerException: ex);
        }

        var results = new SendResult[messages.Count];

        // Group messages by routing key so we can send per-queue batches.
        // For simplicity in R2.1, send each message individually via the cached sender.
        // Batching via ServiceBusMessageBatch is deferred to a perf optimisation task.
        for (int i = 0; i < messages.Count; i++)
        {
            OutboundMessage outbound = messages[i];

            ServiceBusSender sender = GetOrCreateSender(outbound.RoutingKey);

            // D-5: BinaryData.FromBytes(ReadOnlyMemory<byte>) wraps without copying — no ADR-003 deviation.
            var sbMessage = new ServiceBusMessage(BinaryData.FromBytes(outbound.Body));

            // Apply BareWire headers to ApplicationProperties.
            AzureServiceBusHeaderMapper.MapOutbound(outbound.Headers, sbMessage);

            // R2.2: resolve SessionId from BW-SessionId header (priority) or correlation-id
            // fallback (D-1/D-13). When neither is present, leave SessionId unset (R2.1 behaviour).
            string? resolvedSessionId = AzureServiceBusSessionMapper.Resolve(outbound.Headers);
            if (!string.IsNullOrEmpty(resolvedSessionId))
            {
                sbMessage.SessionId = resolvedSessionId;
                LogSessionIdResolved(resolvedSessionId, outbound.RoutingKey);
            }

            // Propagate content-type if available.
            if (!string.IsNullOrEmpty(outbound.ContentType))
            {
                sbMessage.ContentType = outbound.ContentType;
            }

            try
            {
                await sender.SendMessageAsync(sbMessage, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new BareWireTransportException(
                    message: $"Failed to send message to Azure Service Bus queue/topic '{outbound.RoutingKey}'.",
                    transportName: TransportName,
                    endpointAddress: null,
                    innerException: ex);
            }

            // ASB send is fire-and-forget confirm (publisher confirms not applicable to SB in R2.1).
            // Use a monotonic counter as the delivery tag (mirrors RabbitMQ adapter pattern for
            // transports without per-message broker delivery tags on the send path).
            results[i] = new SendResult(IsConfirmed: true, DeliveryTag: (ulong)(i + 1));
        }

        return results;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Creates ASB queues from <see cref="TopologyDeclaration.Queues"/> using the
    /// <c>ServiceBusAdministrationClient</c>. Exchanges and bindings are
    /// accepted (shared contract) but produce no admin operations — ASB has no exchange or binding
    /// concept (D-6). Queue-already-exists errors are swallowed (idempotent declaration).
    /// </remarks>
    public async Task DeployTopologyAsync(
        TopologyDeclaration topology,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EnsureAdminClientAsync(cancellationToken).ConfigureAwait(false);

        foreach (QueueDeclaration queue in topology.Queues)
        {
            AzureServiceBusQueueSpec spec = AzureServiceBusTopologyArguments.Parse(queue);

            var queueOptions = new CreateQueueOptions(queue.Name)
            {
                MaxDeliveryCount = spec.MaxDeliveryCount,
                LockDuration = spec.LockDuration,
                RequiresDuplicateDetection = spec.RequiresDuplicateDetection,
                RequiresSession = spec.RequiresSession,
            };

            try
            {
                await _adminClient!.CreateQueueAsync(queueOptions, cancellationToken).ConfigureAwait(false);
                LogQueueCreated(queue.Name);
            }
            catch (ServiceBusException ex)
                when (ex.Reason == ServiceBusFailureReason.MessagingEntityAlreadyExists)
            {
                // Idempotent — queue already exists; swallow and log.
                LogQueueAlreadyExists(queue.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new TopologyDeploymentException(
                    topologyElement: queue.Name,
                    transportName: TransportName,
                    brokerError: ex.Message,
                    endpointAddress: null,
                    innerException: ex);
            }
        }

        foreach (ExchangeDeclaration exchange in topology.Exchanges)
        {
            LogExchangeSkipped(exchange.Name);
        }

        foreach (ExchangeQueueBinding binding in topology.ExchangeQueueBindings)
        {
            LogBindingSkipped(binding.ExchangeName, binding.QueueName);
        }

        foreach (ExchangeExchangeBinding binding in topology.ExchangeExchangeBindings)
        {
            LogBindingSkipped(binding.SourceExchangeName, binding.DestinationExchangeName);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Stop all active non-session consumers BEFORE disposing the client.
        foreach (AzureServiceBusConsumer consumer in _consumerRegistry.AllConsumers())
        {
            await consumer.StopAsync().ConfigureAwait(false);
            await consumer.DisposeAsync().ConfigureAwait(false);
        }

        // Stop all active session consumers (R2.2).
        AzureServiceBusSessionConsumer[] sessionConsumersSnapshot;
        lock (_sessionConsumersLock)
        {
            sessionConsumersSnapshot = [.. _sessionConsumers];
        }

        foreach (AzureServiceBusSessionConsumer sessionConsumer in sessionConsumersSnapshot)
        {
            await sessionConsumer.StopAsync().ConfigureAwait(false);
            await sessionConsumer.DisposeAsync().ConfigureAwait(false);
        }

        // Dispose all cached senders.
        foreach (ServiceBusSender sender in _senders.Values)
        {
            await sender.DisposeAsync().ConfigureAwait(false);
        }

        _senders.Clear();

        // ServiceBusAdministrationClient does not implement IDisposable or IAsyncDisposable;
        // simply clear the reference (the underlying HttpClient is managed by the SDK).
        _adminClient = null;

        // Dispose the main client last (after all senders/receivers are done).
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }

        _connectionLock.Dispose();
    }

    // ── Lazy connection helpers ───────────────────────────────────────────────

    private async Task EnsureClientAsync(CancellationToken cancellationToken)
    {
        // Fast path: client already built.
        if (_client is not null)
        {
            return;
        }

        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check after acquiring the lock.
            if (_client is not null)
            {
                return;
            }

            // SEC-02/SEC-03: secrets are never logged.
            // Branch on auth mode — both SDK constructors accept the same ServiceBusClientOptions.
            _client = _options.AuthMode switch
            {
                AzureServiceBusAuthMode.EntraId =>
                    // Entra ID: namespace host + TokenCredential (automatic token refresh by SDK).
                    new ServiceBusClient(_options.FullyQualifiedNamespace, _options.Credential!),
                _ =>
                    // SAS: connection string passed directly to the SDK, never logged.
                    // The SDK exposes FullyQualifiedNamespace (host without the key) after construction.
                    new ServiceBusClient(_options.ConnectionString),
            };

            // Log only the non-secret namespace host (FullyQualifiedNamespace, which the SDK
            // exposes separately from any key/token — safe to include in diagnostics).
            LogClientCreated(_client.FullyQualifiedNamespace);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task EnsureAdminClientAsync(CancellationToken cancellationToken)
    {
        // Fast path: admin client already built.
        if (_adminClient is not null)
        {
            return;
        }

        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_adminClient is not null)
            {
                return;
            }

            // SEC-02: secrets are never logged.
            // Branch on auth mode — both SDK constructors accept the same ServiceBusAdministrationClientOptions.
            _adminClient = _options.AuthMode switch
            {
                AzureServiceBusAuthMode.EntraId =>
                    // Entra ID: namespace host + TokenCredential (automatic token refresh by SDK).
                    new ServiceBusAdministrationClient(_options.FullyQualifiedNamespace, _options.Credential!),
                _ =>
                    // SAS: connection string not logged — only the derived namespace host is safe.
                    new ServiceBusAdministrationClient(_options.ConnectionString),
            };

            LogAdminClientCreated();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private ServiceBusSender GetOrCreateSender(string queueOrTopicName) =>
        _senders.GetOrAdd(queueOrTopicName, name => _client!.CreateSender(name));

    // ── Logging (source-gen partial methods) ──────────────────────────────────
    // SEC-02: NEVER bind the connection string or any value derived from it (e.g. the
    // raw connection string, SAS key fragments) to a log parameter. Log only non-secret
    // identifiers: queue/entity names and the FullyQualifiedNamespace host (which the SDK
    // exposes without the SAS key).

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Azure Service Bus client created. Namespace: {Namespace}.")]
    private partial void LogClientCreated(string @namespace);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Azure Service Bus administration client created.")]
    private partial void LogAdminClientCreated();

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Azure Service Bus queue '{QueueName}' created successfully.")]
    private partial void LogQueueCreated(string queueName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Azure Service Bus queue '{QueueName}' already exists — skipping (idempotent declaration).")]
    private partial void LogQueueAlreadyExists(string queueName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Azure Service Bus topology deploy: exchange '{ExchangeName}' skipped — ASB has no exchange concept.")]
    private partial void LogExchangeSkipped(string exchangeName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Azure Service Bus topology deploy: binding '{SourceName}' -> '{DestinationName}' skipped — ASB has no binding concept.")]
    private partial void LogBindingSkipped(string sourceName, string destinationName);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Azure Service Bus produce: resolved SessionId='{SessionId}' for queue/topic '{RoutingKey}'.")]
    private partial void LogSessionIdResolved(string sessionId, string routingKey);
}
