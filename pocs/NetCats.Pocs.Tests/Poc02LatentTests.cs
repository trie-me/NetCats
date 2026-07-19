using ColdLatent = NetCats.Poc02.Latent.Latent<int>;

namespace NetCats.Pocs.Tests;

public sealed class Poc02LatentTests
{
    [Fact]
    public async Task Construction_composition_and_query_syntax_are_cold_and_repeatable()
    {
        var runs = 0;
        var source = ColdLatent.Delay(() => ++runs);
        var direct = source.Bind(value => ColdLatent.Pure(value + 10));
        var query =
            from value in source
            from increment in ColdLatent.Pure(10)
            select value + increment;

        Assert.Equal(0, runs);
        Assert.Equal(11, await direct.RunAsync());
        Assert.Equal(12, await direct.RunAsync());
        Assert.Equal(13, await query.RunAsync());
        Assert.Equal(3, runs);
    }

    [Fact]
    public async Task Synchronous_factory_exceptions_are_effect_failures_and_recoverable()
    {
        var effect = ColdLatent.Delay(() => throw new InvalidOperationException("boom"));

        Assert.Equal(42, await effect.Recover(_ => 42).RunAsync());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => effect.RunAsync());
        Assert.Equal("boom", error.Message);
    }

    [Fact]
    public async Task Repeated_runs_have_independent_cancellation()
    {
        var runs = 0;
        var effect = ColdLatent.DelayAsync(async token =>
        {
            var run = Interlocked.Increment(ref runs);
            if (run == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            return run;
        });
        using var cancellation = new CancellationTokenSource();
        var first = effect.RunAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(2, await effect.RunAsync());
    }

    [Fact]
    public async Task Memoization_policies_distinguish_success_from_outcome()
    {
        var successRuns = 0;
        var successOnly = ColdLatent.Delay(() =>
        {
            if (Interlocked.Increment(ref successRuns) == 1)
            {
                throw new InvalidOperationException("first");
            }

            return 9;
        }).MemoizeSuccess();
        await Assert.ThrowsAsync<InvalidOperationException>(() => successOnly.RunAsync());
        Assert.Equal(9, await successOnly.RunAsync());
        Assert.Equal(9, await successOnly.RunAsync());
        Assert.Equal(2, successRuns);

        var outcomeRuns = 0;
        var outcome = ColdLatent.Delay(() =>
        {
            Interlocked.Increment(ref outcomeRuns);
            throw new ApplicationException("cached");
        }).MemoizeOutcome();
        await Assert.ThrowsAsync<ApplicationException>(() => outcome.RunAsync());
        await Assert.ThrowsAsync<ApplicationException>(() => outcome.RunAsync());
        Assert.Equal(1, outcomeRuns);
    }
}
