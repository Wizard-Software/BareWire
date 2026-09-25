using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using BareWire.Abstractions.Configuration;
using BareWire.Abstractions.Outbox;
using BareWire.Abstractions.Serialization;
using BareWire.Abstractions.Transport;
using BareWire.IntegrationTests.Transport.InMemoryBus;
using BareWire.Outbox;
using BareWire.Outbox.EntityFramework;
using BareWire.Outbox.EntityFramework.Internal;
using BareWire.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace BareWire.IntegrationTests.Outbox.InMemoryTransport;

/// <summary>
/// Groups every Outbox+InMemoryTransport P0 test class into one xUnit collection with parallelization
/// disabled — every scenario measures dispatch-cycle timing, retry counters, or latch state on a shared
/// SQLite file, which would be skewed by another test in the same collection running concurrently.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OutboxInMemoryIsolation
{
    /// <summary>The collection name every scenario test class opts into via <c>[Collection(Name)]</c>.</summary>
    public const string Name = "OutboxInMemory";
}

/// <summary>Selects which <see cref="IOutboxStore"/> implementation an <see cref="OutboxInMemoryHost"/> drives.</summary>
internal enum OutboxStoreKind
{
    /// <summary>The production EF Core store against a temporary SQLite database file.</summary>
    EfCoreSqlite,

    /// <summary>
    /// The in-process <see cref="InMemoryOutboxStore"/>, wrapped in <see cref="HeadOfLineFreeOutboxStore"/>
    /// so the dispatcher's per-key ordering barrier can actually release a confirmed sibling — see that
    /// type's remarks.
    /// </summary>
    HeadOfLineFreeInMemory,
}

/// <summary>
/// Hosts a real <c>BareWireBus</c> on the in-memory transport (via <see cref="InMemoryBusHost"/>) together
/// with a real <c>OutboxDispatcher</c> and the production <c>TransactionalOutboxMiddleware</c> /
/// <c>InboxFilter</c> pipeline, for proving Outbox+Inbox liveness and deduplication properties against a
/// broker-free transport. Construct via <see cref="StartAsync"/> and dispose via <c>await using</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>SQLite file, not <c>:memory:</c>.</b> The dispatcher and the Inbox middleware (resolved per
/// consume scope by every receive endpoint — it is registered as a global <c>IMessageMiddleware</c>, not
/// per-endpoint) run concurrently against the SAME database. A single shared <c>SqliteConnection</c> is
/// not thread-safe across that concurrency, so each scoped <c>OutboxDbContext</c> opens its own
/// connection to a temporary file (<c>Pooling=False</c> so the file can be deleted on dispose), with
/// <c>journal_mode=WAL</c> and <c>synchronous=NORMAL</c> set once at startup and a busy timeout of 30
/// seconds (<c>Default Timeout</c> on the connection string — a Microsoft.Data.Sqlite retry window, not
/// the same thing as SQLite's own <c>busy_timeout</c> pragma).
/// </para>
/// <para>
/// <b>Ambient transaction warning.</b> <c>TransactionalOutboxMiddleware</c> opens a
/// <see cref="System.Transactions.TransactionScope"/> around every consume, which Microsoft.Data.Sqlite
/// cannot natively enlist in — EF Core surfaces this as <see cref="RelationalEventId.AmbientTransactionWarning"/>,
/// which by default escalates to an exception. The DbContext registered here suppresses it, exactly like
/// <c>TransactionalOutboxMiddlewareTests</c> in <c>OutboxIntegrationTests.cs</c> — this host still
/// exercises the middleware's real commit-order logic, just without SQLite's own rollback semantics.
/// </para>
/// <para>
/// <b>Why the gated consumer never blocks the database.</b> The middleware performs the Inbox
/// <c>TryLockAsync</c> check — and its own row commits immediately, outside the
/// <see cref="System.Transactions.TransactionScope"/> — BEFORE invoking the consumer, and opens the
/// <see cref="System.Transactions.TransactionScope"/> before the consumer runs too. Microsoft.Data.Sqlite
/// only begins an actual write transaction on the first data-modifying command issued after the scope
/// opens; a consumer that awaits a gate before touching any database issues no command at all while
/// blocked, so the open-but-idle connection under WAL never holds a write lock other connections would
/// wait on.
/// </para>
/// </remarks>
internal sealed class OutboxInMemoryHost : IAsyncDisposable
{
    private readonly OutboxStoreKind _storeKind;
    private readonly MeterListener _meterListener;
    private readonly ConcurrentBag<(string Instrument, long Value, KeyValuePair<string, object?>[] Tags)> _measurements;
    private readonly string _dbPath;
    private OutboxDispatcher? _dispatcher;
    private bool _disposed;

    private OutboxInMemoryHost(
        InMemoryBusHost bus,
        DispatchCycleRecorder cycles,
        OutboxOptions options,
        InboxDiagnostics inbox,
        OutboxStoreKind storeKind,
        MeterListener meterListener,
        ConcurrentBag<(string Instrument, long Value, KeyValuePair<string, object?>[] Tags)> measurements,
        string dbPath)
    {
        Bus = bus;
        Cycles = cycles;
        Options = options;
        Inbox = inbox;
        _storeKind = storeKind;
        _meterListener = meterListener;
        _measurements = measurements;
        _dbPath = dbPath;
    }

    /// <summary>Gets the real in-memory bus this host drives the dispatcher and consumers against.</summary>
    internal InMemoryBusHost Bus { get; }

    /// <summary>Gets the recorder of dispatch-cycle timing and per-row attempt/delivery/barrier history.</summary>
    internal DispatchCycleRecorder Cycles { get; }

    /// <summary>Gets the outbox options this host's dispatcher and store were configured with.</summary>
    internal OutboxOptions Options { get; }

    /// <summary>Gets the inbox-duplicate diagnostics instance wired into this host's <c>InboxFilter</c>.</summary>
    internal InboxDiagnostics Inbox { get; }

    /// <summary>
    /// Builds a private container, registers the in-memory transport, the real bus, and the production
    /// Outbox/Inbox pipeline (via the public <c>AddBareWireOutbox</c>, with the store and the diagnostics
    /// wiring overridden per <paramref name="storeKind"/>), starts the bus, and returns the running host.
    /// The dispatcher itself is NOT started — call <see cref="StartDispatcher"/> once seeding is done.
    /// </summary>
    /// <param name="transport">Configures the in-memory transport. Must not be <see langword="null"/>.</param>
    /// <param name="outbox">Configures the outbox (polling interval, batch size, ordering, …). Must not be <see langword="null"/>.</param>
    /// <param name="storeKind">Which <see cref="IOutboxStore"/> implementation the dispatcher claims rows from.</param>
    /// <param name="services">Optional callback to register additional per-test services (probes, consumers) before the container is built.</param>
    /// <param name="cancellationToken">A long-lived token passed to <c>IBusControl.StartAsync</c> — see <see cref="InMemoryBusHost.StartAsync"/>.</param>
    internal static async Task<OutboxInMemoryHost> StartAsync(
        Action<IInMemoryConfigurator> transport,
        Action<IOutboxConfigurator> outbox,
        OutboxStoreKind storeKind,
        Action<IServiceCollection>? services,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(outbox);

        string dbPath = Path.Combine(Path.GetTempPath(), $"barewire-outbox-{Guid.NewGuid():N}.db");
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            DefaultTimeout = 30,
            Pooling = false,
        }.ToString();

        var cycles = new DispatchCycleRecorder();

        InMemoryBusHost bus = await InMemoryBusHost.StartAsync(
            transport: transport,
            services: s =>
            {
                s.AddBareWireOutbox(
                    o => o.UseSqlite(connectionString)
                        .ConfigureWarnings(w => w.Ignore(RelationalEventId.AmbientTransactionWarning)),
                    c =>
                    {
                        outbox(c);
                        c.AllowNonAtomicProvider = true;
                    });

                s.AddSingleton(cycles);

                // Later registrations win at GetRequiredService — these two override AddBareWireOutbox's
                // own (diagnostics-less) InboxFilter registration so TransactionalOutboxMiddleware, which
                // resolves InboxFilter fresh per consume scope, observes the diagnostics-wired instance.
                s.AddSingleton(sp => new InboxDiagnostics(sp.GetRequiredService<IMeterFactory>()));
                s.AddScoped(sp => new InboxFilter(
                    sp.GetRequiredService<IInboxStore>(),
                    sp.GetRequiredService<OutboxOptions>(),
                    sp.GetRequiredService<ILogger<InboxFilter>>(),
                    sp.GetRequiredService<InboxDiagnostics>()));

                if (storeKind == OutboxStoreKind.EfCoreSqlite)
                {
                    s.AddScoped<IOutboxStore>(sp => cycles.Wrap(new EfCoreOutboxStore(
                        sp.GetRequiredService<OutboxDbContext>(),
                        sp.GetRequiredService<OutboxInstanceId>(),
                        sp.GetRequiredService<IOutboxSqlDialect>(),
                        sp.GetRequiredService<OutboxOptions>(),
                        sp.GetRequiredService<TimeProvider>(),
                        sp.GetRequiredService<IOutboxJitterSource>(),
                        sp.GetRequiredService<OutboxSingleSlotTurn>())));
                }
                else
                {
                    // The inner store itself runs OrderingMode.None (no head-of-line filtering of its
                    // own) — HeadOfLineFreeOutboxStore promotes the ordering key for the dispatcher's
                    // barrier while the store hands out every keyed row, not just the head.
                    s.AddSingleton(sp =>
                    {
                        OutboxOptions degraded = sp.GetRequiredService<OutboxOptions>() with
                        {
                            OrderingMode = OrderingMode.None,
                            OrderingKeyHeaderName = null,
                        };

                        return new InMemoryOutboxStore(
                            degraded,
                            timeProvider: sp.GetRequiredService<TimeProvider>(),
                            jitterSource: sp.GetRequiredService<IOutboxJitterSource>());
                    });
                    s.AddScoped<IOutboxStore>(sp => cycles.Wrap(new HeadOfLineFreeOutboxStore(
                        sp.GetRequiredService<InMemoryOutboxStore>(),
                        sp.GetRequiredService<OutboxOptions>().OrderingKeyHeaderName!)));
                }

                services?.Invoke(s);
            },
            cancellationToken: cancellationToken);

        try
        {
            var meterFactory = bus.Services.GetRequiredService<IMeterFactory>();
            var measurements = new ConcurrentBag<(string Instrument, long Value, KeyValuePair<string, object?>[] Tags)>();

            // Must start BEFORE the first resolution of InboxDiagnostics or OutboxDispatcher — both
            // create their Counter<long> instrument lazily, on first construction, and a MeterListener
            // started after Counter.Add() calls have already happened never sees earlier measurements.
            // Nothing above this point resolves either type (no hosted services run under InMemoryBusHost,
            // and no message has been published yet), so starting the listener here is still in time.
            var meterListener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == InboxDiagnostics.MeterName
                        && ReferenceEquals(instrument.Meter.Scope, meterFactory)
                        && (instrument.Name == OutboxRetryDiagnostics.RetriedRowsCounterName
                            || instrument.Name == InboxDiagnostics.DuplicatesCounterName))
                    {
                        l.EnableMeasurementEvents(instrument);
                    }
                },
            };
            meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
                measurements.Add((instrument.Name, measurement, tags.ToArray())));
            meterListener.Start();

            // PRAGMAs are connection-scoped in SQLite, but journal_mode=WAL is persisted in the database
            // file itself and applies to every later connection once set — so setting it here, before
            // EnsureCreatedAsync, covers every scoped OutboxDbContext connection this host ever opens.
            await using (var pragmaConnection = new SqliteConnection(connectionString))
            {
                await pragmaConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

                await using (SqliteCommand walCommand = pragmaConnection.CreateCommand())
                {
                    walCommand.CommandText = "PRAGMA journal_mode=WAL;";
                    await walCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (SqliteCommand syncCommand = pragmaConnection.CreateCommand())
                {
                    syncCommand.CommandText = "PRAGMA synchronous=NORMAL;";
                    await syncCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await using (AsyncServiceScope schemaScope = bus.Services.CreateAsyncScope())
            {
                await schemaScope.ServiceProvider.GetRequiredService<OutboxDbContext>()
                    .Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
            }

            OutboxOptions options = bus.Services.GetRequiredService<OutboxOptions>();
            InboxDiagnostics inbox = bus.Services.GetRequiredService<InboxDiagnostics>();

            return new OutboxInMemoryHost(bus, cycles, options, inbox, storeKind, meterListener, measurements, dbPath);
        }
        catch
        {
            // Nobody else owns the bus yet on a setup failure — clean it up ourselves.
            await bus.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Seeds a single outbox row directly through the store's own API (never through <c>ConsumeContext.PublishAsync</c>) and returns its message id.</summary>
    internal async Task<Guid> SaveRowAsync<T>(
        T message, string exchange, IReadOnlyDictionary<string, string>? extraHeaders = null)
        where T : class
    {
        IReadOnlyList<Guid> ids = await SaveRowsAsync([message], exchange, extraHeaders).ConfigureAwait(false);
        return ids[0];
    }

    /// <summary>
    /// Seeds every row in <paramref name="messages"/> in ONE store write (one <c>SaveMessagesAsync</c>
    /// call, committed in a single <c>SaveChangesAsync</c> for the EF store) so a dispatch cycle can never
    /// observe a partially-seeded batch, and returns each row's message id in the same order.
    /// </summary>
    internal async Task<IReadOnlyList<Guid>> SaveRowsAsync<T>(
        IReadOnlyList<T> messages, string exchange, IReadOnlyDictionary<string, string>? extraHeaders = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(exchange);

        if (messages.Count == 0)
        {
            return [];
        }

        IMessageSerializer serializer = Bus.Services.GetRequiredService<IMessageSerializer>();
        var ids = new List<Guid>(messages.Count);
        var outbound = new List<OutboundMessage>(messages.Count);

        foreach (T message in messages)
        {
            Guid messageId = Guid.NewGuid();
            ids.Add(messageId);

            Dictionary<string, string> headers = new()
            {
                ["BW-MessageType"] = typeof(T).Name,
                ["message-id"] = messageId.ToString(),
                ["BW-Exchange"] = exchange,
            };

            if (extraHeaders is not null)
            {
                foreach ((string key, string value) in extraHeaders)
                {
                    headers[key] = value;
                }
            }

            outbound.Add(MessagePipeline.ProcessOutboundAsync(
                message, serializer, routingKey: string.Empty, headers, CancellationToken.None));
        }

        // Written through the UNWRAPPED store (not DispatchCycleRecorder's decorator, and not through
        // ConsumeContext.PublishAsync, which bypasses the transactional outbox from inside a consumer —
        // a known library gap, out of scope here).
        await using AsyncServiceScope scope = Bus.Services.CreateAsyncScope();

        if (_storeKind == OutboxStoreKind.EfCoreSqlite)
        {
            var store = new EfCoreOutboxStore(
                scope.ServiceProvider.GetRequiredService<OutboxDbContext>(),
                scope.ServiceProvider.GetRequiredService<OutboxInstanceId>(),
                scope.ServiceProvider.GetRequiredService<IOutboxSqlDialect>(),
                scope.ServiceProvider.GetRequiredService<OutboxOptions>(),
                scope.ServiceProvider.GetRequiredService<TimeProvider>(),
                scope.ServiceProvider.GetRequiredService<IOutboxJitterSource>(),
                scope.ServiceProvider.GetRequiredService<OutboxSingleSlotTurn>());

            await store.SaveMessagesAsync(outbound).ConfigureAwait(false);
            await scope.ServiceProvider.GetRequiredService<OutboxDbContext>()
                .SaveChangesAsync().ConfigureAwait(false);
        }
        else
        {
            InMemoryOutboxStore store = scope.ServiceProvider.GetRequiredService<InMemoryOutboxStore>();
            await store.SaveMessagesAsync(outbound).ConfigureAwait(false);
        }

        return ids;
    }

    /// <summary>Constructs and starts a real <c>OutboxDispatcher</c> against this host's bus and store. May be called at most once per host.</summary>
    internal void StartDispatcher()
    {
        if (_dispatcher is not null)
        {
            throw new InvalidOperationException("StartDispatcher may only be called once per host.");
        }

        _dispatcher = new OutboxDispatcher(
            Bus.Services.GetRequiredService<IServiceScopeFactory>(),
            Bus.Adapter,
            Options,
            Bus.Services.GetRequiredService<ILogger<OutboxDispatcher>>(),
            StartedLifetime(),
            TimeProvider.System,
            Bus.Services.GetRequiredService<IMeterFactory>());

        // OutboxDispatcher.StartAsync only registers an ApplicationStarted callback and returns a
        // completed Task — StartedLifetime's token is already cancelled, so the callback (which merely
        // schedules the polling loop via Task.Run) runs synchronously during Register(). No blocking.
        _dispatcher.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Sums every <c>barewire.outbox.rows.retried</c> / <c>barewire.inbox.duplicates</c> measurement
    /// recorded for <paramref name="instrumentName"/> whose tags match all of <paramref name="tags"/> (a
    /// measurement may carry additional, unfiltered tags). Passing no <paramref name="tags"/> sums across
    /// every tag combination for that instrument.
    /// </summary>
    internal long CounterTotal(string instrumentName, params (string Key, string Value)[] tags)
    {
        ArgumentNullException.ThrowIfNull(instrumentName);

        long total = 0;
        foreach ((string instrument, long value, KeyValuePair<string, object?>[] measurementTags) in _measurements)
        {
            if (instrument == instrumentName && MatchesAllTags(measurementTags, tags))
            {
                total += value;
            }
        }

        return total;
    }

    /// <summary>Returns whether the underlying bus captured a log entry at or above <paramref name="minimumLevel"/> containing <paramref name="containsText"/>.</summary>
    internal bool HasLog(LogLevel minimumLevel, string containsText) => Bus.Telemetry.HasLog(minimumLevel, containsText);

    /// <summary>Polls <paramref name="condition"/> every 25 ms until it is true or <paramref name="timeout"/> elapses; returns whether it became true.</summary>
    internal static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(condition);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

        try
        {
            while (!condition())
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), linkedCts.Token).ConfigureAwait(false);
            }

            return true;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // Timed out — the caller's own assertion reports the unmet condition.
            return false;
        }
    }

    /// <summary>Stops the dispatcher (bounded to 10 seconds), stops and disposes the bus, and best-effort deletes the temporary SQLite file.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_dispatcher is not null)
        {
            try
            {
                // OutboxDispatcher.StopAsync does not itself observe the cancellation token it accepts —
                // it awaits its polling task to completion unconditionally — so the bound is applied here
                // via WaitAsync rather than by passing a cancelled/timed-out token into the call.
                await _dispatcher.StopAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Best-effort teardown bound for test teardown — a hung dispatcher stop must not hang the
                // whole test run.
            }

            await _dispatcher.DisposeAsync().ConfigureAwait(false);
        }

        await Bus.DisposeAsync().ConfigureAwait(false);
        _meterListener.Dispose();

        SqliteConnection.ClearAllPools();

        DeleteBestEffort(_dbPath);
        DeleteBestEffort(_dbPath + "-wal");
        DeleteBestEffort(_dbPath + "-shm");
    }

    private static void DeleteBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup only — a file the OS still has locked is not a test failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Same rationale as above (observed on Windows for a recently-closed SQLite file).
        }
    }

    private static bool MatchesAllTags(
        ReadOnlySpan<KeyValuePair<string, object?>> measurementTags, (string Key, string Value)[] filters)
    {
        foreach ((string key, string value) in filters)
        {
            bool found = false;
            foreach (KeyValuePair<string, object?> tag in measurementTags)
            {
                if (tag.Key == key && string.Equals(tag.Value?.ToString(), value, StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    // Copied from BareWire.UnitTests.Outbox.OutboxP0Harness.StartedLifetime() — that helper is private
    // to its test class, so this host needs its own copy. A lifetime whose ApplicationStarted has already
    // fired, so the dispatcher's loop starts as soon as StartDispatcher calls StartAsync.
    private static IHostApplicationLifetime StartedLifetime()
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        var started = new CancellationTokenSource();
        started.Cancel();
        lifetime.ApplicationStarted.Returns(started.Token);
        return lifetime;
    }
}
