using System.Buffers;
using BareWire.Abstractions.Transport;

namespace BareWire.Transport.InMemory.Internal;

/// <summary>
/// Implements the in-memory transport's send algorithm: each message of a batch is validated and routed
/// independently, one copy is reserved and committed per target queue (in a fixed, name-ordered
/// sequence), and the whole call waits at most once, for at most
/// <see cref="InMemoryTransportOptions.SendTimeout"/>, regardless of how many messages or target queues
/// are full when it runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>One wait per call.</b> Messages are processed in input order. As long as every target queue
/// reached so far either had room or was already latched, processing stays fully synchronous — the
/// public entry point never allocates a <see cref="Task"/> state machine on that path. The moment a
/// target queue is found full, with an active consumer, and this call has not waited yet, that queue
/// (and any other full queue of the SAME message reached afterwards) is queued as this call's one wait;
/// every full queue reached after that — for the rest of this message or any later one — is rejected
/// immediately, without waiting, because the call's single wait budget is already spent. A full queue is
/// never waited on while a reservation for another queue of the same message is being held: reservations
/// are committed to their queue's channel immediately, one queue at a time, before this call ever awaits.
/// </para>
/// <para>
/// <b>No <see cref="CancellationTokenSource"/>.</b> This type never creates one. The wait's deadline is a
/// plain parameter of <see cref="InMemoryQueue.WaitToReserveAsync"/>, backed by the queue's own
/// <see cref="TimeProvider"/>; cancellation is the caller's own token, forwarded unchanged. The timer,
/// promise, and FIFO waiter entry that one wait allocates (roughly 900-975 bytes) are therefore paid by
/// the queue, at most once per call, and only when a target queue is actually saturated — this is a real
/// cost, not a "zero-cost" wait, it is simply never paid by this sender directly.
/// </para>
/// <para>
/// <b>Cancellation.</b> When the call's one wait observes cancellation, this method never throws
/// <see cref="OperationCanceledException"/>: the message that was waiting, its still-pending target
/// queues, and every later message in the batch are all reported as not confirmed, with a throttled
/// diagnostic log, and the call returns normally. Copies already committed to a queue before the
/// cancellation was observed stay committed.
/// </para>
/// <para>
/// <b>Exception boundary.</b> An unexpected exception raised while processing one message — including
/// while that message is being waited on — is caught, mapped to
/// <see cref="SendRejectionReason.InternalError"/> for that message only, and processing of the rest of
/// the batch continues with the same "one wait per call" budget (spent or not) it had before the
/// exception. <see cref="OperationCanceledException"/> is never treated as an internal error — it is not
/// expected to leave <see cref="InMemoryQueue.WaitToReserveAsync"/>, which reports cancellation as a
/// result value instead of throwing, but this boundary does not swallow it if it ever does.
/// </para>
/// <para>
/// <b>Queue latching.</b> A target queue's "full, no active consumer" case latches itself, inside
/// <see cref="InMemoryQueue.TryReserve"/>, before this sender ever sees it — that latch is not
/// distinguishable from one already set by an earlier caller and is never logged per message. The one
/// latch this type sets itself — <see cref="InMemoryQueue.TryLatch"/>, after this call's one wait for
/// that queue times out — is the only latch event this type logs, once, throttled per queue.
/// </para>
/// </remarks>
internal sealed class InMemorySender
{
    private readonly InMemoryBroker _broker;
    private readonly InMemoryRouter _router;
    private readonly InMemoryTransportOptions _options;
    private readonly InMemorySendDiagnostics _diagnostics;
    private readonly Func<bool> _isClosed;
    private long _lastTag;

    /// <param name="broker">Resolves target queue instances by name. Must not be <see langword="null"/>.</param>
    /// <param name="router">Resolves target queue names for an (exchange, routing key) pair. Must not be <see langword="null"/>.</param>
    /// <param name="options">The transport options this send path validates against. Must not be <see langword="null"/>.</param>
    /// <param name="diagnostics">Records rejected copies/messages. Must not be <see langword="null"/>.</param>
    /// <param name="isClosed">
    /// Returns whether the owning adapter has been disposed. Called before every message and once more
    /// after this call's one wait, if it had one. Must not be <see langword="null"/>.
    /// </param>
    internal InMemorySender(
        InMemoryBroker broker,
        InMemoryRouter router,
        InMemoryTransportOptions options,
        InMemorySendDiagnostics diagnostics,
        Func<bool> isClosed)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(isClosed);

        _broker = broker;
        _router = router;
        _options = options;
        _diagnostics = diagnostics;
        _isClosed = isClosed;
    }

    /// <summary>
    /// Sends <paramref name="messages"/>. The caller (the adapter) has already validated that
    /// <paramref name="messages"/> and every element are non-null, that <c>messages.Count</c> is greater
    /// than zero, and that <paramref name="cancellationToken"/> was not already cancelled on entry — this
    /// method never throws.
    /// </summary>
    internal Task<IReadOnlyList<SendResult>> SendAsync(
        IReadOnlyList<OutboundMessage> messages, CancellationToken cancellationToken)
    {
        var results = new SendResult[messages.Count];
        long tagStart = Interlocked.Add(ref _lastTag, messages.Count) - messages.Count + 1;

        for (int i = 0; i < messages.Count; i++)
        {
            if (_isClosed())
            {
                MarkClosed(results, i, tagStart);
                return Task.FromResult<IReadOnlyList<SendResult>>(results);
            }

            ulong tag = (ulong)(tagStart + i);
            MessageOutcome outcome = ProcessMessage(messages[i], waitAvailable: true);
            if (outcome.Pending is null)
            {
                results[i] = new SendResult(outcome.Confirmed, tag);
                continue;
            }

            return ContinueAfterWaitAsync(messages, results, i, tag, outcome.Pending, tagStart, cancellationToken);
        }

        return Task.FromResult<IReadOnlyList<SendResult>>(results);
    }

    /// <summary>
    /// Reads the exchange header (<see cref="InMemoryHeaderNames.Exchange"/>) with an ordinal-key
    /// comparison, regardless of <paramref name="headers"/>' own equality comparer: a plain
    /// <see cref="Dictionary{TKey,TValue}"/> using the default or <see cref="StringComparer.Ordinal"/>
    /// comparer is read with a direct <c>TryGetValue</c>; any other dictionary (including one using
    /// <see cref="StringComparer.OrdinalIgnoreCase"/>) is scanned entry by entry with an ordinal key
    /// comparison, so a differently-cased variant such as <c>bw-exchange</c> is never honored — it would
    /// otherwise both drive routing and be silently stripped by <see cref="InMemoryHeaderSet.Stamp"/>,
    /// which strips every <c>BW-</c>-prefixed key case-insensitively.
    /// </summary>
    /// <param name="headers">The publisher-supplied headers. Must not be <see langword="null"/>.</param>
    /// <param name="exchange">The header's value, or <see langword="null"/> when the header is absent.</param>
    /// <returns><see langword="true"/> when the header was found.</returns>
    internal static bool TryGetExchangeHeader(IReadOnlyDictionary<string, string> headers, out string? exchange)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (headers is Dictionary<string, string> dictionary &&
            (ReferenceEquals(dictionary.Comparer, EqualityComparer<string>.Default) ||
             ReferenceEquals(dictionary.Comparer, StringComparer.Ordinal)))
        {
            return dictionary.TryGetValue(InMemoryHeaderNames.Exchange, out exchange);
        }

        foreach (KeyValuePair<string, string> entry in headers)
        {
            if (string.Equals(entry.Key, InMemoryHeaderNames.Exchange, StringComparison.Ordinal))
            {
                exchange = entry.Value;
                return true;
            }
        }

        exchange = null;
        return false;
    }

    /// <summary>
    /// Validates, routes, and reserves target-queue copies for one message, entirely synchronously.
    /// Every step is covered by one exception boundary mapping any unexpected failure to
    /// <see cref="SendRejectionReason.InternalError"/> for this message only.
    /// </summary>
    /// <param name="message">The message to process.</param>
    /// <param name="waitAvailable">
    /// Whether this call's single wait budget is still unspent. When <see langword="false"/>, a full
    /// target queue is rejected immediately and this method never returns a non-null
    /// <see cref="MessageOutcome.Pending"/>.
    /// </param>
    private MessageOutcome ProcessMessage(OutboundMessage message, bool waitAvailable)
    {
        string? exchange = null;
        try
        {
            TryGetExchangeHeader(message.Headers, out string? exchangeHeader);
            exchange = exchangeHeader ?? _options.DefaultExchange;
            if (exchange is null)
            {
                _diagnostics.MessageRejected(
                    SendRejectionReason.NoExchange, declaredExchange: null, exchangeHeader,
                    message.RoutingKey, message.Body.Length);
                return new MessageOutcome(false, null);
            }

            if (message.Body.Length > _options.MaxMessageSize)
            {
                string? declared = _router.Registry.ContainsExchange(exchange) ? exchange : null;
                _diagnostics.MessageRejected(
                    SendRejectionReason.Oversized, declared, exchange, message.RoutingKey, message.Body.Length);
                return new MessageOutcome(false, null);
            }

            InMemoryRouteResult route = _router.Route(exchange, message.RoutingKey);
            switch (route.Status)
            {
                case InMemoryRouteStatus.ExchangeNotFound:
                    _diagnostics.MessageRejected(
                        SendRejectionReason.UndeclaredExchange, declaredExchange: null, exchange,
                        message.RoutingKey, message.Body.Length);
                    return new MessageOutcome(false, null);

                case InMemoryRouteStatus.RoutingKeyTooLong:
                {
                    string? declared = _router.Registry.ContainsExchange(exchange) ? exchange : null;
                    _diagnostics.MessageRejected(
                        SendRejectionReason.RoutingKeyTooLong, declared, exchange, message.RoutingKey, message.Body.Length);
                    return new MessageOutcome(false, null);
                }

                case InMemoryRouteStatus.Unroutable:
                    return new MessageOutcome(_router.ReportUnroutable(exchange, message.RoutingKey), null);
            }

            InMemoryHeaderSet headers = InMemoryHeaderSet.Stamp(message.Headers, exchange, message.RoutingKey, message.ContentType);

            bool anyRejected = false;
            List<InMemoryQueue>? fullQueues = null;
            foreach (string queueName in route.Queues)
            {
                if (!_broker.TryGetQueue(queueName, out InMemoryQueue? queue))
                {
                    throw new InvalidOperationException(
                        $"In-memory queue '{queueName}' resolved by routing is not registered on the broker.");
                }

                switch (queue.TryReserve())
                {
                    case QueueReservationResult.Reserved:
                        Commit(queue, message.Body.Span, headers);
                        break;

                    case QueueReservationResult.Latched:
                        _diagnostics.CopyRejected(queue.Name, SendRejectionReason.QueueFull);
                        anyRejected = true;
                        break;

                    case QueueReservationResult.Full:
                        if (waitAvailable && _options.SendTimeout > TimeSpan.Zero)
                        {
                            (fullQueues ??= new List<InMemoryQueue>(capacity: 2)).Add(queue);
                        }
                        else
                        {
                            _diagnostics.CopyRejected(queue.Name, SendRejectionReason.QueueFull);
                            anyRejected = true;
                        }

                        break;
                }
            }

            if (fullQueues is null)
            {
                return new MessageOutcome(!anyRejected, null);
            }

            var pending = new PendingMessage { Headers = headers, Body = message.Body, AnyCopyRejected = anyRejected };
            pending.FullQueues.AddRange(fullQueues);
            return new MessageOutcome(false, pending);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string? declared = exchange is not null && _router.Registry.ContainsExchange(exchange) ? exchange : null;
            _diagnostics.MessageRejected(
                SendRejectionReason.InternalError, declared, exchange, message.RoutingKey, message.Body.Length, ex);
            return new MessageOutcome(false, null);
        }
    }

    /// <summary>
    /// Handles the call's one wait: sequentially waits, in a shared <see cref="InMemoryTransportOptions.SendTimeout"/>
    /// budget measured from entry into this method, on every queue <paramref name="pending"/> collected
    /// while it was processed synchronously, then finishes the rest of the batch with the wait budget spent.
    /// </summary>
    private async Task<IReadOnlyList<SendResult>> ContinueAfterWaitAsync(
        IReadOnlyList<OutboundMessage> messages, SendResult[] results, int index, ulong tag,
        PendingMessage pending, long tagStart, CancellationToken cancellationToken)
    {
        long start = TimeProvider.System.GetTimestamp();
        bool cancelled = false;

        for (int q = 0; q < pending.FullQueues.Count && !cancelled; q++)
        {
            InMemoryQueue queue = pending.FullQueues[q];
            TimeSpan remaining = RemainingBudget(start);

            QueueWaitResult waitResult;
            try
            {
                waitResult = await queue.WaitToReserveAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _diagnostics.MessageRejected(
                    SendRejectionReason.InternalError, declaredExchange: null, publisherExchange: null,
                    messages[index].RoutingKey, messages[index].Body.Length, ex);
                results[index] = new SendResult(false, tag);
                return await ProcessRemainingAsync(messages, results, index + 1, tagStart).ConfigureAwait(false);
            }

            switch (waitResult)
            {
                case QueueWaitResult.Reserved:
                    Commit(queue, pending.Body.Span, pending.Headers);
                    break;

                case QueueWaitResult.TimedOut:
                    if (queue.TryLatch())
                    {
                        _diagnostics.QueueLatchedAfterWait(queue);
                    }

                    _diagnostics.CopyRejected(queue.Name, SendRejectionReason.QueueFull);
                    pending.AnyCopyRejected = true;
                    break;

                case QueueWaitResult.Latched:
                    _diagnostics.CopyRejected(queue.Name, SendRejectionReason.QueueFull);
                    pending.AnyCopyRejected = true;
                    break;

                case QueueWaitResult.Cancelled:
                    cancelled = true;
                    break;
            }
        }

        if (cancelled)
        {
            int cancelledCount = messages.Count - index;
            for (int j = index; j < messages.Count; j++)
            {
                results[j] = new SendResult(false, (ulong)(tagStart + j));
            }

            _diagnostics.MessagesSkipped(SendRejectionReason.Cancelled, cancelledCount);
            return results;
        }

        if (_isClosed())
        {
            MarkClosed(results, index, tagStart);
            return results;
        }

        results[index] = new SendResult(!pending.AnyCopyRejected, tag);
        return await ProcessRemainingAsync(messages, results, index + 1, tagStart).ConfigureAwait(false);
    }

    /// <summary>
    /// Finishes messages <paramref name="startIndex"/>.. after this call's one wait has already run (or
    /// was never needed) — entirely synchronous, since <c>waitAvailable: false</c> guarantees
    /// <see cref="ProcessMessage"/> never returns a pending wait from here on.
    /// </summary>
    private Task<IReadOnlyList<SendResult>> ProcessRemainingAsync(
        IReadOnlyList<OutboundMessage> messages, SendResult[] results, int startIndex, long tagStart)
    {
        for (int i = startIndex; i < messages.Count; i++)
        {
            if (_isClosed())
            {
                MarkClosed(results, i, tagStart);
                return Task.FromResult<IReadOnlyList<SendResult>>(results);
            }

            MessageOutcome outcome = ProcessMessage(messages[i], waitAvailable: false);
            results[i] = new SendResult(outcome.Confirmed, (ulong)(tagStart + i));
        }

        return Task.FromResult<IReadOnlyList<SendResult>>(results);
    }

    private void MarkClosed(SendResult[] results, int startIndex, long tagStart)
    {
        int count = results.Length - startIndex;
        for (int i = startIndex; i < results.Length; i++)
        {
            results[i] = new SendResult(false, (ulong)(tagStart + i));
        }

        _diagnostics.MessagesSkipped(SendRejectionReason.Closed, count);
    }

    private TimeSpan RemainingBudget(long start)
    {
        TimeSpan elapsed = TimeProvider.System.GetElapsedTime(start);
        TimeSpan remaining = _options.SendTimeout - elapsed;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        return remaining > InMemoryQueue.MaxSupportedWaitTimeout ? InMemoryQueue.MaxSupportedWaitTimeout : remaining;
    }

    /// <summary>
    /// Rents a buffer sized to <paramref name="body"/> (at least one byte, so a zero-length body still
    /// gets a private buffer rather than the shared empty array — see <see cref="ArrayPool{T}.Rent"/>),
    /// copies the body into it, and writes it to <paramref name="queue"/>'s already-reserved slot. On any
    /// failure before the write succeeds, the rented buffer is returned to the pool and the reservation is
    /// released before the exception propagates — the caller (<see cref="ProcessMessage"/> or
    /// <see cref="ContinueAfterWaitAsync"/>) maps it to <see cref="SendRejectionReason.InternalError"/>.
    /// </summary>
    private static void Commit(InMemoryQueue queue, ReadOnlySpan<byte> body, InMemoryHeaderSet headers)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(body.Length, 1));
        bool written = false;
        try
        {
            body.CopyTo(buffer);
            queue.WriteReserved(new InMemoryDelivery(buffer, body.Length, headers));
            written = true;
        }
        finally
        {
            if (!written)
            {
                ArrayPool<byte>.Shared.Return(buffer);
                queue.ReleaseSlot();
            }
        }
    }

    private readonly record struct MessageOutcome(bool Confirmed, PendingMessage? Pending);

    /// <summary>The one message of a call that reached its wait — allocated on the slow path only.</summary>
    private sealed class PendingMessage
    {
        internal required InMemoryHeaderSet Headers { get; init; }

        internal required ReadOnlyMemory<byte> Body { get; init; }

        internal List<InMemoryQueue> FullQueues { get; } = new(capacity: 2);

        internal bool AnyCopyRejected { get; set; }
    }
}
