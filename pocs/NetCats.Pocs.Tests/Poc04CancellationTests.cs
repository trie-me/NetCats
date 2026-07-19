using NetCats.Poc04.Cancellation;

namespace NetCats.Pocs.Tests;

public sealed class Poc04CancellationTests
{
    [Fact]
    public async Task Cancel_signal_is_one_shot_latched_and_visible_to_late_observers()
    {
        using var signal = new CancelSignal();

        Assert.True(signal.Request());
        Assert.False(signal.Request());
        Assert.True(signal.IsRequested);
        Assert.True(signal.Token.IsCancellationRequested);
        await signal.WhenRequested;
    }

    [Fact]
    public void Concurrent_requesters_produce_exactly_one_transition()
    {
        using var signal = new CancelSignal();
        var transitions = 0;

        Parallel.For(0, 1_000, _ =>
        {
            if (signal.Request())
            {
                Interlocked.Increment(ref transitions);
            }
        });

        Assert.Equal(1, transitions);
        Assert.True(signal.IsRequested);
    }

    [Fact]
    public async Task Masked_request_is_deferred_until_restore_and_masked_native_token_is_protected()
    {
        using var signal = new CancelSignal();
        var context = new CancellationContext(signal);
        var bodyCompleted = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.UncancelableAsync(async _ =>
        {
            Assert.False(context.NativeToken.CanBeCanceled);
            signal.Request();
            await Task.Yield();
            bodyCompleted = true;
            return 1;
        }));
        Assert.True(bodyCompleted);
    }

    [Fact]
    public async Task Poll_temporarily_restores_cancellation_inside_a_mask()
    {
        using var signal = new CancelSignal();
        var context = new CancellationContext(signal);
        var operationEntered = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.UncancelableAsync(async poll =>
        {
            signal.Request();
            return await poll.RunAsync<int>(_ =>
            {
                operationEntered = true;
                return Task.FromResult(1);
            });
        }));
        Assert.False(operationEntered);
    }

    [Fact]
    public async Task Finalizers_run_once_in_reverse_order_and_are_not_aborted_by_cancellation()
    {
        using var signal = new CancelSignal();
        var order = new List<int>();
        var outcome = await ProtectedExecution.RunAsync<int>((context, stack) =>
        {
            stack.Register(_ =>
            {
                order.Add(1);
                return Task.CompletedTask;
            });
            stack.Register(async _ =>
            {
                await Task.Yield();
                order.Add(2);
            });
            signal.Request();
            context.Checkpoint();
            return Task.FromResult(0);
        }, signal);

        Assert.IsType<Outcome<int>.Cancelled>(outcome);
        Assert.Equal([2, 1], order);
    }

    [Fact]
    public async Task Operation_and_finalizer_failures_have_deterministic_aggregate_precedence()
    {
        var outcome = await ProtectedExecution.RunAsync<int>((_, stack) =>
        {
            stack.Register(_ => Task.FromException(new ApplicationException("finalizer")));
            return Task.FromException<int>(new InvalidOperationException("operation"));
        });

        var fault = Assert.IsType<Outcome<int>.Faulted>(outcome);
        var aggregate = Assert.IsType<AggregateException>(fault.Error);
        Assert.Collection(
            aggregate.InnerExceptions,
            error => Assert.IsType<InvalidOperationException>(error),
            error => Assert.IsType<ApplicationException>(error));
    }
}
