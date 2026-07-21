using Microsoft.Extensions.Options;
using NetCats.Runtime;
using NetCats.Testing;

namespace NetCats.AspNetCore.Tests;

public sealed class FiberDiagnosticsRegistryTests
{
    [Fact]
    public void Parallel_lifecycle_burst_keeps_the_projection_within_its_hard_node_limit()
    {
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(now);
        var registry = CreateRegistry(time, maximumNodes: 64, completedRetention: TimeSpan.FromHours(1));
        var scopeId = Guid.NewGuid();
        registry.Observe(ScopeOpened(scopeId, now));

        Parallel.For(0, 5_000, index =>
        {
            var fiberId = Guid.NewGuid();
            registry.Observe(FiberStarted(scopeId, fiberId, $"fiber-{index}", now));
            registry.Observe(FiberTerminated(scopeId, fiberId, $"fiber-{index}", now));
        });

        var snapshot = registry.GetSnapshot();

        Assert.Equal(10_001, snapshot.Version);
        Assert.True(snapshot.IsTruncated);
        Assert.InRange(CountNodes(snapshot.Roots), 1, 64);
        Assert.Single(snapshot.Roots);
        Assert.Equal(63, snapshot.Roots[0].Fibers.Count);
    }

    [Fact]
    public void Capacity_truncation_retains_the_most_recent_completed_fibers_deterministically()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
        var registry = CreateRegistry(time, maximumNodes: 4, completedRetention: TimeSpan.FromHours(1));
        var scopeId = Guid.NewGuid();
        registry.Observe(ScopeOpened(scopeId, time.GetUtcNow()));
        for (var index = 0; index < 10; index++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            var fiberId = Guid.NewGuid();
            registry.Observe(FiberStarted(scopeId, fiberId, $"fiber-{index}", time.GetUtcNow()));
            registry.Observe(FiberTerminated(scopeId, fiberId, $"fiber-{index}", time.GetUtcNow()));
        }

        var snapshot = registry.GetSnapshot();

        Assert.True(snapshot.IsTruncated);
        Assert.Equal(["fiber-7", "fiber-8", "fiber-9"], snapshot.Roots[0].Fibers.Select(static fiber => fiber.Name).ToArray());
    }

    [Fact]
    public async Task Completed_nodes_expire_and_wake_snapshot_waiters_on_the_injected_clock()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
        var registry = CreateRegistry(time, completedRetention: TimeSpan.FromSeconds(15));
        var scopeId = Guid.NewGuid();
        var fiberId = Guid.NewGuid();
        registry.Observe(ScopeOpened(scopeId, time.GetUtcNow()));
        registry.Observe(FiberStarted(scopeId, fiberId, "short-lived", time.GetUtcNow()));
        registry.Observe(FiberTerminated(scopeId, fiberId, "short-lived", time.GetUtcNow()));
        var before = registry.GetSnapshot();
        var changed = registry.WaitForChangeAsync(before.Version, CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(15));
        var after = registry.GetSnapshot();

        await changed;
        Assert.Equal(before.Version + 1, after.Version);
        Assert.Empty(after.Roots[0].Fibers);
    }

    private static FiberDiagnosticsRegistry CreateRegistry(
        TimeProvider timeProvider,
        int maximumNodes = 2_000,
        TimeSpan? completedRetention = null) =>
        new(Options.Create(new FiberDiagnosticsOptions
        {
            MaximumNodes = maximumNodes,
            CompletedRetention = completedRetention ?? TimeSpan.FromSeconds(15),
        }), timeProvider);

    private static int CountNodes(IEnumerable<FiberScopeNode> scopes) => scopes.Sum(scope =>
        1 + scope.Fibers.Count + CountNodes(scope.Scopes));

    internal static FiberObservation ScopeOpened(Guid scopeId, DateTimeOffset observedAt) => new(
        FiberObservationKind.ScopeOpened,
        observedAt,
        scopeId,
        null,
        null,
        "root",
        FiberScopeState.Open);

    internal static FiberObservation FiberStarted(Guid scopeId, Guid fiberId, string name, DateTimeOffset observedAt) => new(
        FiberObservationKind.FiberStarted,
        observedAt,
        scopeId,
        null,
        null,
        "root",
        null,
        fiberId,
        name,
        FiberLifecycleState.Running);

    internal static FiberObservation FiberTerminated(Guid scopeId, Guid fiberId, string name, DateTimeOffset observedAt) => new(
        FiberObservationKind.FiberTerminated,
        observedAt,
        scopeId,
        null,
        null,
        "root",
        null,
        fiberId,
        name,
        FiberLifecycleState.Terminated,
        FiberOutcomeKind.Succeeded);
}
