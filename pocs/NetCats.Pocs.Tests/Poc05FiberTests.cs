using NetCats.Poc04.Cancellation;
using NetCats.Poc05.Fibers;

namespace NetCats.Pocs.Tests;

public sealed class Poc05FiberTests
{
    [Fact]
    public async Task Scope_close_cancels_joins_and_waits_for_cleanup()
    {
        var scope = new FiberScope();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fiber = scope.Start<int>(async signal =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, signal.Token);
                return 1;
            }
            finally
            {
                cleanupStarted.TrySetResult();
                await releaseCleanup.Task;
            }
        });
        await entered.Task;

        var close = scope.CloseAsync();
        await cleanupStarted.Task;
        Assert.False(close.IsCompleted);
        releaseCleanup.TrySetResult();
        await close;

        Assert.IsType<Outcome<int>.Cancelled>(await fiber.JoinAsync());
        Assert.Equal(0, scope.ActiveChildCount);
        Assert.Throws<ObjectDisposedException>(() => scope.Start<int>(_ => Task.FromResult(1)));
    }

    [Fact]
    public async Task Nested_scopes_are_owned_and_closed_with_their_parent()
    {
        var parent = new FiberScope();
        var child = parent.CreateChild();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fiber = child.Start<int>(async signal =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, signal.Token);
            return 1;
        });
        await entered.Task;

        await parent.CloseAsync();

        Assert.IsType<Outcome<int>.Cancelled>(await fiber.JoinAsync());
        Assert.Equal(0, parent.ActiveChildCount);
    }

    [Fact]
    public async Task Child_failure_policy_cancels_siblings()
    {
        await using var scope = new FiberScope(ChildFailurePolicy.CancelSiblings);
        var siblingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sibling = scope.Start<int>(async signal =>
        {
            siblingEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, signal.Token);
            return 1;
        });
        await siblingEntered.Task;
        var failed = scope.Start<int>(_ => Task.FromException<int>(new InvalidOperationException("child")));

        Assert.IsType<Outcome<int>.Faulted>(await failed.JoinAsync());
        Assert.IsType<Outcome<int>.Cancelled>(await sibling.JoinAsync());
    }

    [Fact]
    public async Task Race_cancels_the_loser_and_parallel_tracks_every_child()
    {
        await using var scope = new FiberScope(ChildFailurePolicy.Ignore);
        var race = await FiberCombinators.RaceAsync(
            scope,
            _ => Task.FromResult(7),
            async signal =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, signal.Token);
                return 9;
            });
        var parallel = await FiberCombinators.ParallelAsync(
            scope,
            Enumerable.Range(0, 32).Select<int, Func<CancelSignal, Task<int>>>(value => _ => Task.FromResult(value)));

        Assert.Equal(7, Assert.IsType<Outcome<int>.Succeeded>(race).Value);
        Assert.Equal(32, parallel.Count);
        Assert.All(parallel, outcome => Assert.IsType<Outcome<int>.Succeeded>(outcome));
        Assert.Equal(0, scope.ActiveChildCount);
    }
}
