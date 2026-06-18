using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Saga;
using BareWire.Saga.Redis;
using StackExchange.Redis;

namespace BareWire.IntegrationTests.Saga;

// ── Lokalny typ stanu SAGA używany wyłącznie przez testy integracyjne Redis ───

/// <summary>
/// Test-only SAGA state type used by <see cref="RedisSagaRepositoryIntegrationTests"/>.
/// Contains only synthetic, non-PII fields (ADR-019 SEC-1).
/// </summary>
internal sealed class RedisOrderSagaState : ISagaState
{
    /// <inheritdoc />
    public Guid CorrelationId { get; set; }

    /// <inheritdoc />
    public string CurrentState { get; set; } = "Initial";

    /// <inheritdoc />
    public int Version { get; set; }

    /// <summary>Gets or sets the synthetic order number for this test saga instance.</summary>
    public string? OrderNumber { get; set; }

    /// <summary>Gets or sets the synthetic monetary amount for this test saga instance.</summary>
    public decimal Amount { get; set; }
}

// ── Klasa testowa ─────────────────────────────────────────────────────────────

/// <summary>
/// Integration tests for <c>RedisSagaRepository&lt;TSaga&gt;</c> (R6.1) running against a real
/// Redis container orchestrated by .NET Aspire (<see cref="AspireFixture"/>).
///
/// <para>
/// Covers: SAGA CRUD, optimistic concurrency via Lua scripts, TTL expiry, and multiplexer
/// durability (reconnect). Each test uses a unique <c>KeyPrefix</c> (Guid) and random
/// <c>CorrelationId</c> to guarantee isolation without teardown.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class RedisSagaRepositoryIntegrationTests(AspireFixture fixture)
    : IClassFixture<AspireFixture>
{
    // ── Helper: buduje repozytorium ───────────────────────────────────────────

    /// <summary>
    /// Tworzy <see cref="ISagaRepository{TSaga}"/> przez bezpośrednią konstrukcję
    /// wewnętrznych typów <c>BareWire.Saga.Redis</c> (dostęp przez
    /// <c>InternalsVisibleTo</c> w projekcie <c>BareWire.Saga.Redis.csproj</c>).
    ///
    /// <para>
    /// UWAGA ARCHITEKTONICZNA: Plan (D2) zakładał ścieżkę DI
    /// (<c>AddBareWireRedisConnection</c> + <c>AddBareWireSagaRedis</c> →
    /// <c>GetRequiredService</c>), jednak runtime DI nie widzi konstruktora
    /// <c>internal</c> — <c>RedisSagaRepository</c> jest <c>internal sealed</c>,
    /// a <c>InternalsVisibleTo</c> działa tylko w czasie kompilacji, nie refleksji.
    /// Bezpośrednia konstrukcja przez <c>InternalsVisibleTo</c> jest wzorcem
    /// stosowanym przez istniejące testy jednostkowe (<c>RedisSagaRepositoryTests</c>).
    /// </para>
    /// <para>
    /// Connection string Aspire Redis ma format pełnego StackExchange.Redis:
    /// <c>host:port,password=…,ssl=true|false</c>. Parsujemy go przez
    /// <c>ConfigurationOptions.Parse</c>, zamiast próby przekazania całości
    /// do <c>EndPointCollection.Add</c> (które akceptuje wyłącznie <c>host[:port]</c>).
    /// </para>
    /// </summary>
    /// <param name="keyPrefix">
    /// Unikalny prefiks klucza Redis dla izolacji testu (D3). Gdy <see langword="null"/>,
    /// generowany jest nowy <see cref="Guid"/>.
    /// </param>
    /// <param name="ttl">Opcjonalne TTL stanu SAGA.</param>
    private RedisSagaRepository<RedisOrderSagaState> CreateRepository(
        string? keyPrefix = null,
        TimeSpan? ttl = null)
    {
        var prefix = keyPrefix ?? $"itest-{Guid.NewGuid():N}";

        // Parsuj pełny connection string Aspire (format: host:port,password=...,ssl=true).
        var config = ConfigurationOptions.Parse(fixture.GetRedisConnectionString());
        var multiplexer = ConnectionMultiplexer.Connect(config);

        var options = new RedisSagaRepositoryOptions
        {
            KeyPrefix = prefix,
            StateTtl = ttl
        };

        var serializer = new SagaStateSerializer<RedisOrderSagaState>();
        return new RedisSagaRepository<RedisOrderSagaState>(multiplexer, options, serializer);
    }

    // ── CRUD ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_NewSaga_PersistsAndFindReturnsState()
    {
        var repo = CreateRepository();

        var correlationId = Guid.NewGuid();
        var saga = new RedisOrderSagaState
        {
            CorrelationId = correlationId,
            CurrentState = "Pending",
            Version = 0,
            OrderNumber = "ORD-001",
            Amount = 99.99m
        };

        await repo.SaveAsync(saga);

        var found = await repo.FindAsync(correlationId);

        found.Should().NotBeNull();
        found!.CorrelationId.Should().Be(correlationId);
        found.CurrentState.Should().Be("Pending");
        found.Version.Should().Be(0);
        found.OrderNumber.Should().Be("ORD-001");
        found.Amount.Should().Be(99.99m);
    }

    [Fact]
    public async Task FindAsync_NonExistentKey_ReturnsNull()
    {
        var repo = CreateRepository();

        var found = await repo.FindAsync(Guid.NewGuid());

        found.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ExistingSaga_RemovesState()
    {
        var repo = CreateRepository();

        var correlationId = Guid.NewGuid();
        var saga = new RedisOrderSagaState
        {
            CorrelationId = correlationId,
            CurrentState = "Active",
            Version = 0
        };

        await repo.SaveAsync(saga);

        // Upewnij się, że stan istnieje przed usunięciem.
        var beforeDelete = await repo.FindAsync(correlationId);
        beforeDelete.Should().NotBeNull();

        await repo.DeleteAsync(correlationId);

        var afterDelete = await repo.FindAsync(correlationId);
        afterDelete.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_NonExistentKey_IsIdempotent()
    {
        var repo = CreateRepository();

        var nonExistentId = Guid.NewGuid();

        // Upewnij się, że niezwiązana SAGA nie jest naruszona.
        var unrelatedId = Guid.NewGuid();
        var unrelated = new RedisOrderSagaState
        {
            CorrelationId = unrelatedId,
            CurrentState = "Stable",
            Version = 0,
            OrderNumber = "ORD-STABLE"
        };
        await repo.SaveAsync(unrelated);

        // Usunięcie nieistniejącego klucza nie powinno rzucić wyjątku.
        var act = async () => await repo.DeleteAsync(nonExistentId);
        await act.Should().NotThrowAsync();

        // Niezwiązana SAGA musi pozostać nieruszona.
        var unrelatedAfter = await repo.FindAsync(unrelatedId);
        unrelatedAfter.Should().NotBeNull();
        unrelatedAfter!.OrderNumber.Should().Be("ORD-STABLE");
    }

    // ── Save duplicate ────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_DuplicateCorrelationId_ThrowsInvalidOperationException()
    {
        var repo = CreateRepository();

        var correlationId = Guid.NewGuid();
        var saga = new RedisOrderSagaState
        {
            CorrelationId = correlationId,
            CurrentState = "Initial",
            Version = 0
        };

        await repo.SaveAsync(saga);

        // Drugi SaveAsync na ten sam CorrelationId — Lua SET NX zwraca 0.
        var duplicate = new RedisOrderSagaState
        {
            CorrelationId = correlationId,
            CurrentState = "Duplicate",
            Version = 0
        };

        var act = async () => await repo.SaveAsync(duplicate);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Concurrency via Lua ───────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_MatchingVersion_IncrementsVersion()
    {
        var repo = CreateRepository();

        var correlationId = Guid.NewGuid();
        var saga = new RedisOrderSagaState
        {
            CorrelationId = correlationId,
            CurrentState = "Initial",
            Version = 0
        };
        await repo.SaveAsync(saga);

        // Pobierz stan i zaktualizuj.
        var loaded = await repo.FindAsync(correlationId);
        loaded!.CurrentState = "Processing";

        await repo.UpdateAsync(loaded);

        var updated = await repo.FindAsync(correlationId);
        updated.Should().NotBeNull();
        updated!.Version.Should().Be(1);
        updated.CurrentState.Should().Be("Processing");
    }

    [Fact]
    public async Task UpdateAsync_StaleVersion_ThrowsConcurrencyException()
    {
        // Aranżacja: dwa „wątki" pracujące na tym samym kluczu Redis.
        // Oba używają tego samego KeyPrefix, aby kolizja wersji była realna.
        var sharedPrefix = $"itest-{Guid.NewGuid():N}";

        var repo1 = CreateRepository(keyPrefix: sharedPrefix);
        var repo2 = CreateRepository(keyPrefix: sharedPrefix);

        var correlationId = Guid.NewGuid();
        var saga = new RedisOrderSagaState
        {
            CorrelationId = correlationId,
            CurrentState = "Initial",
            Version = 0
        };

        // Zapis początkowy (Version=0 w Redis).
        await repo1.SaveAsync(saga);

        // Ścieżka A: wczytuje Version=0.
        var sagaA = await repo1.FindAsync(correlationId);
        sagaA!.CurrentState = "FromPathA";

        // Ścieżka B: również wczytuje Version=0, aktualizuje jako pierwsza → Redis ma teraz Version=1.
        var sagaB = await repo2.FindAsync(correlationId);
        sagaB!.CurrentState = "FromPathB";
        await repo2.UpdateAsync(sagaB);

        // Ścieżka A: próbuje zaktualizować ze starą wersją (Version=0) → ConcurrencyException.
        var act = async () => await repo1.UpdateAsync(sagaA);
        await act.Should().ThrowAsync<ConcurrencyException>();
    }

    [Fact]
    public async Task UpdateAsync_MissingState_ThrowsConcurrencyException()
    {
        var repo = CreateRepository();

        // UpdateAsync na CorrelationId, który nigdy nie był zapisany → gałąź Lua "missing".
        var neverSaved = new RedisOrderSagaState
        {
            CorrelationId = Guid.NewGuid(),
            CurrentState = "Ghost",
            Version = 0
        };

        var act = async () => await repo.UpdateAsync(neverSaved);
        await act.Should().ThrowAsync<ConcurrencyException>();
    }

    // ── TTL expiry ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_WithShortTtl_StateExpires()
    {
        // Decyzja R2 (binding): TTL=2s, polling z capem ~6s co 250ms — unikamy stałego Task.Delay.
        var repo = CreateRepository(ttl: TimeSpan.FromSeconds(2));

        var correlationId = Guid.NewGuid();
        var saga = new RedisOrderSagaState
        {
            CorrelationId = correlationId,
            CurrentState = "WillExpire",
            Version = 0
        };
        await repo.SaveAsync(saga);

        // Stan powinien być widoczny bezpośrednio po zapisie.
        var immediate = await repo.FindAsync(correlationId);
        immediate.Should().NotBeNull("stan powinien istnieć zaraz po zapisie");

        // Czekaj na wygaśnięcie przez polling z hojnym capem (~6s).
        RedisOrderSagaState? polled = immediate;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(6);

        while (polled is not null && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            polled = await repo.FindAsync(correlationId);
        }

        polled.Should().BeNull("klucz Redis powinien wygasnąć po upływie TTL");
    }

    [Fact]
    public async Task SaveAsync_WithoutTtl_StatePersists()
    {
        // Wariant kontrolny: bez TTL stan musi przeżyć analogiczny czas co test wygaśnięcia.
        // Dowodzi, że null w teście powyżej wynika z TTL, a nie z błędu zapisu.
        var repo = CreateRepository(ttl: null);

        var correlationId = Guid.NewGuid();
        var saga = new RedisOrderSagaState
        {
            CorrelationId = correlationId,
            CurrentState = "WillPersist",
            Version = 0
        };
        await repo.SaveAsync(saga);

        // Odczekaj czas porównywalny z TTL testu wygaśnięcia.
        await Task.Delay(TimeSpan.FromSeconds(3));

        var found = await repo.FindAsync(correlationId);
        found.Should().NotBeNull("stan bez TTL powinien nadal istnieć po 3 sekundach");
        found!.CurrentState.Should().Be("WillPersist");
    }

    // ── Multiplexer durability ────────────────────────────────────────────────

    /// <summary>
    /// Dowodzi, że ten sam <c>IConnectionMultiplexer</c> (z <c>AbortOnConnectFail=false</c>)
    /// pozostaje sprawny przez wiele operacji rozdzielonych krótką pauzą.
    ///
    /// <para>
    /// UWAGA: Test NIE wymusza zerwania połączenia TCP (brak <c>AllowAdmin</c>/CLIENT KILL —
    /// decyzja OQ-1). Zamiast tego dowodzi trwałości multiplexera jako long-lived singleton:
    /// ten sam <see cref="IConnectionMultiplexer"/> obsługuje kolejne operacje bez ponownej
    /// konfiguracji. Nazywamy test zgodnie z tym, co faktycznie jest weryfikowane
    /// (reguła anti-tautology §4 planu).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Repository_ReusedAcrossOperations_RemainsOperational()
    {
        var repo = CreateRepository();

        // Pierwsza operacja SAGA.
        var correlationId1 = Guid.NewGuid();
        var saga1 = new RedisOrderSagaState
        {
            CorrelationId = correlationId1,
            CurrentState = "FirstOp",
            Version = 0,
            OrderNumber = "ORD-DUR-1"
        };
        await repo.SaveAsync(saga1);

        var found1 = await repo.FindAsync(correlationId1);
        found1.Should().NotBeNull();
        found1!.OrderNumber.Should().Be("ORD-DUR-1");

        // Krótka pauza — multiplexer może wykonać background maintenance (heartbeat, itp.).
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        // Druga operacja SAGA na tym samym repozytorium (tym samym multiplexerze).
        var correlationId2 = Guid.NewGuid();
        var saga2 = new RedisOrderSagaState
        {
            CorrelationId = correlationId2,
            CurrentState = "SecondOp",
            Version = 0,
            OrderNumber = "ORD-DUR-2"
        };
        await repo.SaveAsync(saga2);

        var found2 = await repo.FindAsync(correlationId2);
        found2.Should().NotBeNull();
        found2!.OrderNumber.Should().Be("ORD-DUR-2");
    }
}
