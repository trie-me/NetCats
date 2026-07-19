using NetCats.Core;
using NetCats.Runtime;
using NetCats.Testing;

namespace NetCats.Runtime.Tests;

public sealed class RuntimeTests
{
    [Fact]
    public async Task Cancel_signal_is_latched_and_masked_requests_observe_at_restore()
    {
        using var signal = new CancelSignal();
        var context = new CancellationContext(signal);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.UncancelableAsync(async _ =>
        {
            signal.Request();
            await Task.Yield();
            return 1;
        }));
        Assert.True(signal.IsRequested);
        await signal.WhenRequested;
    }

    [Fact]
    public async Task Protected_execution_runs_finalizers_once_in_reverse_order()
    {
        var order = new List<int>();
        var outcome = await ProtectedExecution.RunAsync<int>((_, stack) =>
        {
            stack.Register(_ =>
            {
                order.Add(1);
                return Task.CompletedTask;
            });
            stack.Register(_ =>
            {
                order.Add(2);
                return Task.CompletedTask;
            });
            return Task.FromResult(1);
        });

        Assert.IsType<Outcome<int>.Succeeded>(outcome);
        Assert.Equal([2, 1], order);
    }

    [Fact]
    public async Task Scope_close_cancels_and_joins_its_owned_fibers()
    {
        await using var scope = new FiberScope();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fiber = scope.Start(Latent<int>.DelayAsync(async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 1;
        }));
        await entered.Task;

        await scope.CloseAsync();

        Assert.IsType<Outcome<int>.Cancelled>(await fiber.JoinAsync());
        Assert.Equal(0, scope.ActiveChildCount);
    }

    [Fact]
    public async Task Manual_time_completes_delay_without_wall_clock_time()
    {
        var time = new ManualTimeProvider();
        var delay = Task.Delay(TimeSpan.FromMinutes(1), time);

        time.Advance(TimeSpan.FromMinutes(1));

        await delay;
    }
}
