using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Saga;
using BareWire.Saga.Redis;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BareWire.UnitTests.Saga.Redis;

// ── Test saga state ───────────────────────────────────────────────────────────

/// <summary>A SAGA state dedicated to RedisSagaRepository tests.</summary>
public sealed class RedisSagaState : ISagaState
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = "Initial";
    public int Version { get; set; }
    public string? Payload { get; set; }
}

/// <summary>A second SAGA state type to verify prefix isolation between saga types.</summary>
public sealed class OtherRedisSagaState : ISagaState
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = "Initial";
    public int Version { get; set; }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

public sealed class RedisSagaRepositoryTests
{
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly IDatabase _database;
    private readonly RedisSagaRepositoryOptions _options;
    private readonly SagaStateSerializer<RedisSagaState> _serializer;

    public RedisSagaRepositoryTests()
    {
        _multiplexer = Substitute.For<IConnectionMultiplexer>();
        _database = Substitute.For<IDatabase>();
        _multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(_database);
        _options = new RedisSagaRepositoryOptions { KeyPrefix = "RedisSagaState" };
        _serializer = new SagaStateSerializer<RedisSagaState>();
    }

    private RedisSagaRepository<RedisSagaState> CreateRepository() =>
        new(_multiplexer, _options, _serializer);

    private static RedisSagaState CreateSaga(
        Guid? correlationId = null,
        string currentState = "Initial",
        int version = 0,
        string? payload = null)
        => new()
        {
            CorrelationId = correlationId ?? Guid.NewGuid(),
            CurrentState = currentState,
            Version = version,
            Payload = payload
        };

    // ── Constructor null-guards ───────────────────────────────────────────────

    [Fact]
    public void Constructor_NullMultiplexer_ThrowsArgumentNullException()
    {
        var act = () => new RedisSagaRepository<RedisSagaState>(null!, _options, _serializer);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("multiplexer");
    }

    [Fact]
    public void Constructor_NullOptions_ThrowsArgumentNullException()
    {
        var act = () => new RedisSagaRepository<RedisSagaState>(_multiplexer, null!, _serializer);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("options");
    }

    [Fact]
    public void Constructor_NullSerializer_ThrowsArgumentNullException()
    {
        var act = () => new RedisSagaRepository<RedisSagaState>(_multiplexer, _options, null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("serializer");
    }

    // ── BuildKey ──────────────────────────────────────────────────────────────

    [Fact]
    public void BuildKey_UsesKeyPrefixAndCorrelationId()
    {
        var repo = CreateRepository();
        var id = Guid.NewGuid();

        string key = repo.BuildKey(id);

        key.Should().Be($"RedisSagaState:{id:D}");
    }

    [Fact]
    public void BuildKey_DifferentSagaTypes_ProduceDifferentPrefixes()
    {
        var id = Guid.NewGuid();

        var repoA = new RedisSagaRepository<RedisSagaState>(
            _multiplexer,
            new RedisSagaRepositoryOptions { KeyPrefix = typeof(RedisSagaState).Name },
            new SagaStateSerializer<RedisSagaState>());

        var multiplexerB = Substitute.For<IConnectionMultiplexer>();
        multiplexerB.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(Substitute.For<IDatabase>());
        var repoB = new RedisSagaRepository<OtherRedisSagaState>(
            multiplexerB,
            new RedisSagaRepositoryOptions { KeyPrefix = typeof(OtherRedisSagaState).Name },
            new SagaStateSerializer<OtherRedisSagaState>());

        string keyA = repoA.BuildKey(id);
        string keyB = repoB.BuildKey(id);

        keyA.Should().NotBe(keyB);
        keyA.Should().StartWith("RedisSagaState:");
        keyB.Should().StartWith("OtherRedisSagaState:");
    }

    // ── FindAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindAsync_KeyDoesNotExist_ReturnsNull()
    {
        var repo = CreateRepository();
        var id = Guid.NewGuid();

        _database.HashGetAsync(Arg.Any<RedisKey>(), "state", Arg.Any<CommandFlags>())
            .Returns(RedisValue.Null);

        // Verify the negative path: if HashGetAsync returned non-null, this test would fail
        // (it explicitly tests the null/missing path).
        var result = await repo.FindAsync(id, CancellationToken.None);

        result.Should().BeNull();
        await _database.Received(1).HashGetAsync(Arg.Any<RedisKey>(), "state", Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task FindAsync_KeyExists_DeserializesAndReturnsSaga()
    {
        var repo = CreateRepository();
        var saga = CreateSaga(currentState: "Processing", payload: "test-payload");

        // Serialize a known saga and put it in the substitute's return
        RedisValue serialized = SagaStateSerializer<RedisSagaState>.Serialize(saga);
        _database.HashGetAsync(Arg.Any<RedisKey>(), "state", Arg.Any<CommandFlags>())
            .Returns(serialized);

        var result = await repo.FindAsync(saga.CorrelationId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.CorrelationId.Should().Be(saga.CorrelationId);
        result.CurrentState.Should().Be("Processing");
        result.Payload.Should().Be("test-payload");
    }

    // ── SaveAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_ScriptReturnsOne_Succeeds()
    {
        var repo = CreateRepository();
        var saga = CreateSaga();

        _database.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]?>(),
                Arg.Any<RedisValue[]?>(),
                Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)1L));

        // Success path: no exception
        var act = async () => await repo.SaveAsync(saga, CancellationToken.None);
        await act.Should().NotThrowAsync();

        await _database.Received(1).ScriptEvaluateAsync(
            RedisSagaScripts.SaveIfNotExists,
            Arg.Any<RedisKey[]?>(),
            Arg.Any<RedisValue[]?>(),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task SaveAsync_ScriptReturnsZero_ThrowsInvalidOperationException()
    {
        var repo = CreateRepository();
        var saga = CreateSaga();

        // Simulate "key already exists" — script returns 0
        _database.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]?>(),
                Arg.Any<RedisValue[]?>(),
                Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)0L));

        var act = async () => await repo.SaveAsync(saga, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{typeof(RedisSagaState).Name}*{saga.CorrelationId}*");
    }

    [Fact]
    public async Task SaveAsync_WhenStateTtlSet_PassesNonZeroTtlToScript()
    {
        var optionsWithTtl = new RedisSagaRepositoryOptions
        {
            KeyPrefix = "RedisSagaState",
            StateTtl = TimeSpan.FromMinutes(30)
        };
        var repo = new RedisSagaRepository<RedisSagaState>(_multiplexer, optionsWithTtl, _serializer);
        var saga = CreateSaga();

        RedisValue[]? capturedValues = null;
        _database.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]?>(),
                Arg.Do<RedisValue[]?>(v => capturedValues = v),
                Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)1L));

        await repo.SaveAsync(saga, CancellationToken.None);

        // ARGV[3] (index 2) must be a non-zero TTL in milliseconds
        capturedValues.Should().NotBeNull();
        capturedValues![2].Should().NotBe((RedisValue)"0");
        long.Parse((string)capturedValues[2]!, System.Globalization.CultureInfo.InvariantCulture).Should().BeGreaterThan(0);
    }

    // ── UpdateAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_ScriptReturnsOk_IncrementsVersion()
    {
        var repo = CreateRepository();
        var saga = CreateSaga(version: 3);

        _database.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]?>(),
                Arg.Any<RedisValue[]?>(),
                Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)"ok"));

        await repo.UpdateAsync(saga, CancellationToken.None);

        // Version must have been incremented from 3 → 4
        saga.Version.Should().Be(4);
    }

    [Fact]
    public async Task UpdateAsync_ScriptReturnsConflict_ThrowsConcurrencyExceptionAndRestoresVersion()
    {
        var repo = CreateRepository();
        var saga = CreateSaga(version: 2);

        // Simulate that Redis stored version is 5 (a conflict)
        _database.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]?>(),
                Arg.Any<RedisValue[]?>(),
                Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)"conflict:5"));

        var act = async () => await repo.UpdateAsync(saga, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ConcurrencyException>();
        ex.Which.ExpectedVersion.Should().Be(2);
        ex.Which.ActualVersion.Should().Be(5);

        // Version must be restored to the original value before throwing
        saga.Version.Should().Be(2);
    }

    [Fact]
    public async Task UpdateAsync_ScriptReturnsMissing_ThrowsConcurrencyExceptionWithNegativeActualVersion()
    {
        var repo = CreateRepository();
        var saga = CreateSaga(version: 1);

        _database.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]?>(),
                Arg.Any<RedisValue[]?>(),
                Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)"missing"));

        var act = async () => await repo.UpdateAsync(saga, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ConcurrencyException>();
        ex.Which.ExpectedVersion.Should().Be(1);
        ex.Which.ActualVersion.Should().Be(-1);

        // Version must be restored
        saga.Version.Should().Be(1);
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_CallsKeyDeleteAsync()
    {
        var repo = CreateRepository();
        var id = Guid.NewGuid();
        string expectedKey = $"RedisSagaState:{id:D}";

        _database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(true);

        await repo.DeleteAsync(id, CancellationToken.None);

        await _database.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey>(k => k == expectedKey),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task DeleteAsync_KeyDoesNotExist_DoesNotThrow()
    {
        var repo = CreateRepository();

        // Simulate "key was not present" — DEL returns false
        _database.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(false);

        var act = async () => await repo.DeleteAsync(Guid.NewGuid(), CancellationToken.None);

        // Must be idempotent: no exception even when the key is absent
        await act.Should().NotThrowAsync();
    }
}
