using Azure.Messaging.ServiceBus;
using BareWire.Transport.AzureServiceBus;
using BareWire.Transport.AzureServiceBus.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace BareWire.UnitTests.Transport.AzureServiceBus;

/// <summary>Shared doubles and helpers for the Azure Service Bus consumer shutdown tests.</summary>
internal static class AzureServiceBusShutdownTestSupport
{
    internal const string QueueName = "shutdown-queue";
    internal const string SecretLockText = "SECRET-LOCK-TOKEN-TEXT";

    internal static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    internal static AzureServiceBusTransportOptions Options(bool sessions = false)
    {
        var configurator = new AzureServiceBusConfigurator();
        configurator.ConnectionString(
            "Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=dGVzdA==");
        if (sessions)
        {
            configurator.UseSessions(maxConcurrentSessions: 1);
            configurator.MaxAutoLockRenewDuration(TimeSpan.Zero);
        }

        return configurator.Build();
    }

    internal static ServiceBusReceivedMessage Message(string id, string? sessionId = null) =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString("{}"),
            messageId: id,
            sessionId: sessionId,
            lockTokenGuid: Guid.NewGuid());

    internal static IReadOnlyList<ServiceBusReceivedMessage> Batch(string prefix, int count, string? sessionId = null) =>
        [.. Enumerable.Range(1, count).Select(i => Message($"{prefix}{i}", sessionId))];

    /// <summary>
    /// Programs <c>ReceiveMessagesAsync</c>: returns the given batches one per call, then calls
    /// <paramref name="idle"/> (default: block until the token is cancelled).
    /// </summary>
    internal static void ProgramReceive(
        ServiceBusReceiver receiver,
        IEnumerable<IReadOnlyList<ServiceBusReceivedMessage>> batches,
        Func<CancellationToken, Task>? idle = null)
    {
        var queue = new Queue<IReadOnlyList<ServiceBusReceivedMessage>>(batches);
        idle ??= ct => Task.Delay(Timeout.Infinite, ct);

        receiver
            .ReceiveMessagesAsync(Arg.Any<int>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => NextAsync(call.ArgAt<CancellationToken>(2)));

        async Task<IReadOnlyList<ServiceBusReceivedMessage>> NextAsync(CancellationToken ct)
        {
            IReadOnlyList<ServiceBusReceivedMessage>? next;
            lock (queue)
            {
                queue.TryDequeue(out next);
            }

            if (next is not null)
            {
                return next;
            }

            await idle(ct);
            return [];
        }
    }

    internal static int ReceiveCalls(ServiceBusReceiver receiver) =>
        receiver.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ServiceBusReceiver.ReceiveMessagesAsync));

    internal static IReadOnlyList<string> AbandonedIds(ServiceBusReceiver receiver) =>
        [.. receiver.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ServiceBusReceiver.AbandonMessageAsync))
            .Select(c => ((ServiceBusReceivedMessage)c.GetArguments()[0]!).MessageId)];

    internal static async Task<bool> PollAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10, CancellationToken.None);
        }

        return condition();
    }

    internal sealed class CapturingLogger : ILogger<AzureServiceBusTransportAdapter>
    {
        private readonly List<(LogLevel Level, string Text)> _entries = [];

        public List<string> Warnings
        {
            get
            {
                lock (_entries)
                {
                    return _entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Text).ToList();
                }
            }
        }

        public string AllText
        {
            get
            {
                lock (_entries)
                {
                    return string.Join('\n', _entries.Select(e => e.Text));
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception) + (exception?.ToString() ?? string.Empty)));
            }
        }
    }
}
