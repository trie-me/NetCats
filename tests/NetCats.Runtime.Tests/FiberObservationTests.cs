using NetCats.Core;
using NetCats.Runtime;

namespace NetCats.Runtime.Tests;

public sealed class FiberObservationTests
{
    [Fact]
    public async Task Scope_and_fiber_observations_describe_ownership_without_effect_values()
    {
        var observer = new RecordingObserver();
        await using var root = FiberScope.CreateRoot(new FiberScopeOptions("root", observer));
        await using var child = root.CreateChild(new FiberScopeOptions("child"));

        var fiber = child.Start(Latent<int>.Pure(42), new FiberDescriptor("work"));
        await fiber.JoinAsync();
        await child.CloseAsync();

        var childOpened = Assert.Single(observer.Observations, observation =>
            observation.Kind is FiberObservationKind.ScopeOpened && observation.ScopeName is "child");
        Assert.Equal(root.Id, childOpened.ParentScopeId);
        var completed = Assert.Single(observer.Observations, observation =>
            observation.Kind is FiberObservationKind.FiberTerminated);
        Assert.Equal(FiberOutcomeKind.Succeeded, completed.Outcome);
        Assert.Null(completed.ErrorCategory);
        Assert.DoesNotContain("42", observer.Observations.Select(static observation => observation.ToString()));
    }

    [Fact]
    public async Task Observer_failure_does_not_change_a_fiber_outcome()
    {
        await using var scope = FiberScope.CreateRoot(new FiberScopeOptions("root", new ThrowingObserver()));
        var fiber = scope.Start(Latent<int>.Pure(1));

        Assert.IsType<Outcome<int>.Succeeded>(await fiber.JoinAsync());
    }

    private sealed class RecordingObserver : IFiberObserver
    {
        public List<FiberObservation> Observations { get; } = [];

        public void Observe(FiberObservation observation) => Observations.Add(observation);
    }

    private sealed class ThrowingObserver : IFiberObserver
    {
        public void Observe(FiberObservation observation) => throw new InvalidOperationException("diagnostics failure");
    }
}
