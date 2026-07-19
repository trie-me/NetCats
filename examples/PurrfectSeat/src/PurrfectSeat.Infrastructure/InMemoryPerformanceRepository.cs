using PurrfectSeat.Application;
using PurrfectSeat.Domain;

namespace PurrfectSeat.Infrastructure;

/// <summary>
/// Stores immutable aggregate mementos. Every load calls <see cref="Performance.Hydrate"/>, so no request
/// ever shares a mutable aggregate instance with another request.
/// </summary>
public sealed class InMemoryPerformanceRepository : IBookingUnitOfWorkFactory, IPerformanceReader
{
    private readonly object gate = new();
    private readonly Dictionary<PerformanceId, PerformanceSnapshot> snapshots = [];

    public void Seed(Performance performance)
    {
        ArgumentNullException.ThrowIfNull(performance);
        lock (gate)
        {
            snapshots[performance.Id] = performance.ToSnapshot();
        }
    }

    public ValueTask<IBookingUnitOfWork> OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IBookingUnitOfWork>(new UnitOfWork(this));
    }

    public Task<IReadOnlyCollection<Performance>> LoadAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            return Task.FromResult<IReadOnlyCollection<Performance>>(snapshots.Values.Select(Performance.Hydrate).ToArray());
        }
    }

    private Performance? Load(PerformanceId id)
    {
        lock (gate)
        {
            return snapshots.TryGetValue(id, out var snapshot) ? Performance.Hydrate(snapshot) : null;
        }
    }

    private bool CompareAndSwap(PerformanceSnapshot snapshot, PerformanceVersion expectedVersion)
    {
        lock (gate)
        {
            if (!snapshots.TryGetValue(snapshot.Id, out var current) || current.Version != expectedVersion)
            {
                return false;
            }

            snapshots[snapshot.Id] = snapshot;
            return true;
        }
    }

    private sealed class UnitOfWork(InMemoryPerformanceRepository repository) : IBookingUnitOfWork
    {
        private PerformanceSnapshot? staged;
        private PerformanceVersion expectedVersion;
        private bool committed;

        public Task<Performance?> LoadAsync(PerformanceId id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(repository.Load(id));
        }

        public Task<SaveAttempt> SaveAsync(
            Performance performance,
            PerformanceVersion expected,
            IReadOnlyCollection<DomainEvent> events,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (committed || staged is not null)
            {
                throw new InvalidOperationException("A unit of work may stage one aggregate change.");
            }

            staged = performance.ToSnapshot();
            expectedVersion = expected;
            return Task.FromResult(SaveAttempt.Success);
        }

        public Task<SaveAttempt> CommitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (staged is null)
            {
                throw new InvalidOperationException("A unit of work cannot commit without a staged change.");
            }

            committed = repository.CompareAndSwap(staged, expectedVersion);
            return Task.FromResult(committed ? SaveAttempt.Success : SaveAttempt.Conflict);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
