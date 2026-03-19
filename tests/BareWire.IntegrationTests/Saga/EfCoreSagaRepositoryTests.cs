using AwesomeAssertions;
using BareWire.Abstractions.Exceptions;
using BareWire.Abstractions.Saga;
using BareWire.Saga.EntityFramework;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BareWire.IntegrationTests.Saga;

public sealed class EfCoreSagaRepositoryTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SagaDbContext _dbContext = null!;
    private EfCoreSagaRepository<TestSagaState> _repository = null!;

    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        ISagaModelConfiguration[] configurations = [new SagaModelConfiguration<TestSagaState>()];

        var options = new DbContextOptionsBuilder<SagaDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new SagaDbContext(options, configurations);
        await _dbContext.Database.EnsureCreatedAsync();

        _repository = new EfCoreSagaRepository<TestSagaState>(_dbContext);
    }

    public async ValueTask DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task FindAsync_ExistingSaga_ReturnsSaga()
    {
        var saga = new TestSagaState
        {
            CorrelationId = Guid.NewGuid(),
            CurrentState = "Initial",
            OrderNumber = "ORD-001"
        };

        await _repository.SaveAsync(saga);

        var found = await _repository.FindAsync(saga.CorrelationId);

        found.Should().NotBeNull();
        found!.CorrelationId.Should().Be(saga.CorrelationId);
        found.CurrentState.Should().Be("Initial");
        found.OrderNumber.Should().Be("ORD-001");
    }

    [Fact]
    public async Task SaveAsync_NewSaga_PersistsToDatabase()
    {
        var correlationId = Guid.NewGuid();
        var saga = new TestSagaState
        {
            CorrelationId = correlationId,
            CurrentState = "Processing",
            OrderNumber = "ORD-002"
        };

        await _repository.SaveAsync(saga);

        // Clear the change tracker and reload via EF to verify what was actually persisted to the DB
        _dbContext.ChangeTracker.Clear();
        var persisted = await _dbContext.Set<TestSagaState>().FindAsync(correlationId);

        persisted.Should().NotBeNull();
        persisted!.CurrentState.Should().Be("Processing");
        persisted.OrderNumber.Should().Be("ORD-002");
    }

    [Fact]
    public async Task UpdateAsync_MatchingVersion_UpdatesSuccessfully()
    {
        var saga = new TestSagaState
        {
            CorrelationId = Guid.NewGuid(),
            CurrentState = "Initial",
            OrderNumber = "ORD-003"
        };

        await _repository.SaveAsync(saga);

        // Detach so FindAsync loads a fresh tracked instance
        _dbContext.ChangeTracker.Clear();

        var loaded = await _repository.FindAsync(saga.CorrelationId);
        loaded!.CurrentState = "Processing";

        await _repository.UpdateAsync(loaded);

        _dbContext.ChangeTracker.Clear();
        var updated = await _dbContext.Set<TestSagaState>().FindAsync(saga.CorrelationId);

        updated.Should().NotBeNull();
        updated!.CurrentState.Should().Be("Processing");
        updated.Version.Should().Be(1);
    }

    [Fact]
    public async Task UpdateAsync_ConcurrentModification_ThrowsConcurrencyException()
    {
        var saga = new TestSagaState
        {
            CorrelationId = Guid.NewGuid(),
            CurrentState = "Initial"
        };

        await _repository.SaveAsync(saga);
        _dbContext.ChangeTracker.Clear();

        // Load the saga — it is now tracked with Version = 0
        var loaded = await _repository.FindAsync(saga.CorrelationId);
        loaded!.CurrentState = "Processing";

        // Simulate a concurrent writer by bumping Version directly in the DB,
        // bypassing the EF change tracker. UpdateAsync will generate:
        //   UPDATE ... WHERE CorrelationId = @id AND Version = 0
        // but the DB now has Version = 1, so 0 rows match → DbUpdateConcurrencyException.
        Guid id = saga.CorrelationId;
        await _dbContext.Database.ExecuteSqlAsync(
            $"UPDATE \"TestSagaState\" SET \"Version\" = 1 WHERE \"CorrelationId\" = {id}");

        var act = async () => await _repository.UpdateAsync(loaded);

        await act.Should().ThrowAsync<ConcurrencyException>();
    }

    [Fact]
    public async Task DeleteAsync_ExistingSaga_RemovesFromDatabase()
    {
        var saga = new TestSagaState
        {
            CorrelationId = Guid.NewGuid(),
            CurrentState = "Completed"
        };

        await _repository.SaveAsync(saga);
        _dbContext.ChangeTracker.Clear();

        await _repository.DeleteAsync(saga.CorrelationId);

        _dbContext.ChangeTracker.Clear();
        var found = await _dbContext.Set<TestSagaState>().FindAsync(saga.CorrelationId);

        found.Should().BeNull();
    }

    [Fact]
    public async Task FindSingleAsync_MatchingPredicate_ReturnsSaga()
    {
        var orderNumber = $"ORD-{Guid.NewGuid():N}";
        var saga = new TestSagaState
        {
            CorrelationId = Guid.NewGuid(),
            CurrentState = "Initial",
            OrderNumber = orderNumber
        };

        await _repository.SaveAsync(saga);
        _dbContext.ChangeTracker.Clear();

        var found = await _repository.FindSingleAsync(s => s.OrderNumber == orderNumber);

        found.Should().NotBeNull();
        found!.CorrelationId.Should().Be(saga.CorrelationId);
        found.OrderNumber.Should().Be(orderNumber);
    }

    [Fact]
    public async Task FindSingleAsync_MultipleSagasMatch_ThrowsInvalidOperation()
    {
        var sharedOrderNumber = $"ORD-SHARED-{Guid.NewGuid():N}";

        var saga1 = new TestSagaState
        {
            CorrelationId = Guid.NewGuid(),
            CurrentState = "Initial",
            OrderNumber = sharedOrderNumber
        };

        var saga2 = new TestSagaState
        {
            CorrelationId = Guid.NewGuid(),
            CurrentState = "Processing",
            OrderNumber = sharedOrderNumber
        };

        await _repository.SaveAsync(saga1);
        _dbContext.ChangeTracker.Clear();
        await _repository.SaveAsync(saga2);
        _dbContext.ChangeTracker.Clear();

        var act = async () => await _repository.FindSingleAsync(s => s.OrderNumber == sharedOrderNumber);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}

public sealed class TestSagaState : ISagaState
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = "Initial";
    public int Version { get; set; }
    public string? OrderNumber { get; set; }
}
