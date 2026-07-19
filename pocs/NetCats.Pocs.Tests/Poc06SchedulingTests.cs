using NetCats.Poc06.Scheduling;

namespace NetCats.Pocs.Tests;

public sealed class Poc06SchedulingTests
{
    [Fact]
    public async Task Virtual_time_completes_delays_without_wall_clock_sleep()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var runtime = new LogicalExecutionTree(time).CreateRuntime();
        var delay = runtime.DelayAsync(TimeSpan.FromHours(2));

        time.Advance(TimeSpan.FromHours(1));
        Assert.False(delay.IsCompleted);
        time.Advance(TimeSpan.FromHours(1));
        await delay;
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.Zero), time.GetUtcNow());
    }

    [Fact]
    public async Task Operation_budget_forces_bounded_cooperative_yields()
    {
        var tree = new LogicalExecutionTree(operationBudget: 5);
        var runtimes = new[] { tree.CreateRuntime(), tree.CreateRuntime() };
        var counts = new int[2];

        await Task.WhenAll(runtimes.Select((runtime, fiber) => RunBusyAsync(runtime, fiber)));

        Assert.Equal([100, 100], counts);
        Assert.All(runtimes, runtime => Assert.Equal(20, runtime.YieldCount));

        async Task RunBusyAsync(LogicalRuntime runtime, int fiber)
        {
            for (var index = 0; index < 100; index++)
            {
                counts[fiber]++;
                await runtime.CheckpointAsync();
            }
        }
    }

    [Fact]
    public void Scheduler_projection_uses_local_time_provider()
    {
        var time = new ManualTimeProvider();
        var scheduler = new TimeProviderSchedulerProjection(time);
        var invoked = false;
        using var registration = scheduler.Schedule(TimeSpan.FromSeconds(30), () => invoked = true);

        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(invoked);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(invoked);
    }

    [Fact]
    public async Task Cancellation_removes_a_virtual_timer_without_advancing_time()
    {
        var time = new ManualTimeProvider();
        var runtime = new LogicalExecutionTree(time).CreateRuntime();
        using var cancellation = new CancellationTokenSource();
        var delay = runtime.DelayAsync(TimeSpan.FromDays(1), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delay);
    }
}
