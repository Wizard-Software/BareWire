using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Saga;
using BareWire.Saga;

namespace BareWire.UnitTests.Saga;

// ── Test saga state ───────────────────────────────────────────────────────────

/// <summary>A saga state dedicated to InMemorySagaRepository tests.</summary>
public sealed class RepositorySagaState : ISagaState
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = "Initial";
    public int Version { get; set; }
    public string? ExtraData { get; set; }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

public sealed class InMemorySagaRepositoryTests
{
    private static InMemorySagaRepository<RepositorySagaState> CreateRepository()
        => new();

    private static RepositorySagaState CreateSaga(
        Guid? correlationId = null,
        string currentState = "Initial",
        int version = 0,
        string? extraData = null)
        => new()
        {
            CorrelationId = correlationId ?? Guid.NewGuid(),
            CurrentState = currentState,
            Version = version,
            ExtraData = extraData
        };

    // ── FindAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindAsync_ExistingSaga_ReturnsCopy()
    {
        var repo = CreateRepository();
        var saga = CreateSaga(extraData: "original");
        await repo.SaveAsync(saga, CancellationToken.None);

        var result = await repo.FindAsync(saga.CorrelationId, CancellationToken.None);

        result.Should().NotBeNull();
        result.Should().NotBeSameAs(saga);         // different instance
        result!.CorrelationId.Should().Be(saga.CorrelationId);
        result.CurrentState.Should().Be(saga.CurrentState);
        result.Version.Should().Be(saga.Version);
        result.ExtraData.Should().Be("original");
    }

    [Fact]
    public async Task FindAsync_NonExistent_ReturnsNull()
    {
        var repo = CreateRepository();

        var result = await repo.FindAsync(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeNull();
    }

    // ── SaveAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_NewSaga_PersistsSuccessfully()
    {
        var repo = CreateRepository();
        var saga = CreateSaga(currentState: "Processing", extraData: "some-data");

        await repo.SaveAsync(saga, CancellationToken.None);
        var stored = await repo.FindAsync(saga.CorrelationId, CancellationToken.None);

        stored.Should().NotBeNull();
        stored!.CorrelationId.Should().Be(saga.CorrelationId);
        stored.CurrentState.Should().Be("Processing");
        stored.ExtraData.Should().Be("some-data");
    }

    [Fact]
    public async Task SaveAsync_DuplicateCorrelationId_ThrowsInvalidOperation()
    {
        var repo = CreateRepository();
        var id = Guid.NewGuid();
        var first = CreateSaga(correlationId: id);
        var duplicate = CreateSaga(correlationId: id);

        await repo.SaveAsync(first, CancellationToken.None);

        var act = () => repo.SaveAsync(duplicate, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{id}*");
    }

    // ── UpdateAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_MatchingVersion_UpdatesAndIncrementsVersion()
    {
        var repo = CreateRepository();
        var saga = CreateSaga(version: 0);
        await repo.SaveAsync(saga, CancellationToken.None);

        // Retrieve a fresh copy and modify it, keeping Version == 0 to match stored.
        var copy = await repo.FindAsync(saga.CorrelationId, CancellationToken.None);
        copy!.CurrentState = "Processing";

        await repo.UpdateAsync(copy, CancellationToken.None);

        var updated = await repo.FindAsync(saga.CorrelationId, CancellationToken.None);
        updated.Should().NotBeNull();
        updated!.CurrentState.Should().Be("Processing");
        updated.Version.Should().Be(1);  // incremented from 0 to 1
        copy.Version.Should().Be(1);     // mutation also applied to the passed-in instance
    }

    [Fact]
    public async Task UpdateAsync_VersionMismatch_ThrowsConcurrencyException()
    {
        var repo = CreateRepository();
        var saga = CreateSaga(version: 0);
        await repo.SaveAsync(saga, CancellationToken.None);

        // Simulate stale version: the caller believes version is 99, but stored is 0.
        var stale = CreateSaga(correlationId: saga.CorrelationId, version: 99);

        var act = () => repo.UpdateAsync(stale, CancellationToken.None);

        await act.Should().ThrowAsync<ConcurrencyException>();
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_ExistingSaga_RemovesFromStore()
    {
        var repo = CreateRepository();
        var saga = CreateSaga();
        await repo.SaveAsync(saga, CancellationToken.None);

        await repo.DeleteAsync(saga.CorrelationId, CancellationToken.None);

        var result = await repo.FindAsync(saga.CorrelationId, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_NonExistent_DoesNotThrow()
    {
        var repo = CreateRepository();

        var act = () => repo.DeleteAsync(Guid.NewGuid(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
