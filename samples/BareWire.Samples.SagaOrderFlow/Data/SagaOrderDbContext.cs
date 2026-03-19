using Microsoft.EntityFrameworkCore;

namespace BareWire.Samples.SagaOrderFlow.Data;

/// <summary>
/// Application-level database context for the SagaOrderFlow sample.
/// Provides access to application data beyond the saga state managed by
/// <see cref="BareWire.Saga.EntityFramework.SagaDbContext"/>.
/// </summary>
/// <remarks>
/// For this sample the context is minimal — the saga state is persisted by BareWire's own
/// <c>SagaDbContext</c> registered via <c>AddBareWireSaga</c>. This context exists as the
/// standard entry point for adding custom application tables in future iterations.
/// </remarks>
public sealed class SagaOrderDbContext(DbContextOptions<SagaOrderDbContext> options)
    : DbContext(options);
