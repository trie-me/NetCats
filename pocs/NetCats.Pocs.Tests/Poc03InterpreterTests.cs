using InterpretedLatent = NetCats.Poc03.Interpreter.Latent<int>;

namespace NetCats.Pocs.Tests;

public sealed class Poc03InterpreterTests
{
    [Fact]
    public async Task One_million_left_associated_binds_are_stack_safe()
    {
        var effect = InterpretedLatent.Pure(0);
        for (var index = 0; index < 1_000_000; index++)
        {
            effect = effect.Bind(static value => InterpretedLatent.Pure(value + 1));
        }

        Assert.Equal(1_000_000, await effect.RunAsync(operationBudget: 4_096));
    }

    [Fact]
    public async Task Mixed_async_chains_preserve_values_and_yield_without_changing_results()
    {
        var effect = InterpretedLatent.Pure(0);
        for (var index = 0; index < 10_000; index++)
        {
            effect = index % 100 == 0
                ? effect.Bind(static value => InterpretedLatent.DelayAsync(_ => Task.FromResult(value + 1)))
                : effect.Bind(static value => InterpretedLatent.Pure(value + 1));
        }

        Assert.Equal(10_000, await effect.RunAsync(operationBudget: 17));
    }

    [Fact]
    public async Task Right_associated_binds_are_also_stack_safe()
    {
        Func<int, InterpretedLatent> chain = InterpretedLatent.Pure;
        for (var index = 0; index < 100_000; index++)
        {
            var tail = chain;
            chain = value => InterpretedLatent.Pure(value + 1).Bind(tail);
        }

        Assert.Equal(100_000, await chain(0).RunAsync(operationBudget: 1_024));
    }

    [Fact]
    public async Task Deep_failures_are_handled_once_at_the_correct_boundary()
    {
        var recoveries = 0;
        var effect = InterpretedLatent.Pure(0);
        for (var index = 0; index < 20_000; index++)
        {
            effect = effect.Bind(static value => InterpretedLatent.Pure(value + 1));
        }

        effect = effect
            .Bind(_ => InterpretedLatent.Fail(new InvalidOperationException("deep")))
            .RecoverWith(error =>
            {
                recoveries++;
                return InterpretedLatent.Pure(error.Message.Length);
            });

        Assert.Equal(4, await effect.RunAsync());
        Assert.Equal(1, recoveries);
    }

    [Fact]
    public async Task Cancellation_is_observed_at_run_loop_checkpoints()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => InterpretedLatent.Pure(1).RunAsync(cancellation.Token, operationBudget: 1));
    }
}
