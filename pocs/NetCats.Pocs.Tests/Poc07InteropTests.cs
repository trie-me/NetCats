using ColdEffect = NetCats.Poc02.Latent.Latent<int>;
using NetCats.Poc04.Cancellation;
using NetCats.Poc07.Interop;

namespace NetCats.Pocs.Tests;

public sealed class Poc07InteropTests
{
    [Fact]
    public async Task Task_factory_is_latent_while_started_task_import_is_explicitly_eager()
    {
        var factoryRuns = 0;
        var latent = TaskInterop.FromTaskFactory<int>(_ => Task.FromResult(++factoryRuns));
        Assert.Equal(0, factoryRuns);

        var eagerRuns = 0;
        var started = Start();
        var imported = TaskInterop.FromStartedTask(started);
        Assert.Equal(1, eagerRuns);

        Assert.Equal(1, await latent.RunAsync());
        Assert.Equal(2, await latent.RunAsync());
        Assert.Equal(99, await imported.RunAsync());

        Task<int> Start()
        {
            eagerRuns++;
            return Task.FromResult(99);
        }
    }

    [Fact]
    public async Task Tasks_map_success_fault_and_cancellation_to_explicit_outcomes()
    {
        var succeeded = await ColdEffect.Pure(1).ToOutcomeAsync();
        var faulted = await ColdEffect.Delay(() => throw new InvalidOperationException()).ToOutcomeAsync();
        using var cancellation = new CancellationTokenSource();
        var cancelledTask = ColdEffect.DelayAsync(async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        });
        var cancelled = cancelledTask.ToOutcomeAsync(cancellation.Token);
        cancellation.Cancel();

        Assert.IsType<Outcome<int>.Succeeded>(succeeded);
        Assert.IsType<Outcome<int>.Faulted>(faulted);
        Assert.IsType<Outcome<int>.Cancelled>(await cancelled);
    }

    [Fact]
    public async Task Async_disposal_is_awaited_on_success_failure_and_cancellation()
    {
        var succeededResource = new RecordingAsyncDisposable();
        var succeeded = AsyncDisposalInterop.UseAsync(
            NetCats.Poc02.Latent.Latent<RecordingAsyncDisposable>.Pure(succeededResource),
            _ => ColdEffect.Pure(7));
        Assert.Equal(7, await succeeded.RunAsync());
        Assert.True(succeededResource.IsDisposed);

        var failedResource = new RecordingAsyncDisposable();
        var failed = AsyncDisposalInterop.UseAsync(
            NetCats.Poc02.Latent.Latent<RecordingAsyncDisposable>.Pure(failedResource),
            _ => ColdEffect.Delay(() => throw new ApplicationException()));
        await Assert.ThrowsAsync<ApplicationException>(() => failed.RunAsync());
        Assert.True(failedResource.IsDisposed);

        var cancelledResource = new RecordingAsyncDisposable();
        var cancelled = AsyncDisposalInterop.UseAsync(
            NetCats.Poc02.Latent.Latent<RecordingAsyncDisposable>.Pure(cancelledResource),
            _ => ColdEffect.DelayAsync(async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            }));
        using var cancellation = new CancellationTokenSource();
        var run = cancelled.RunAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(cancelledResource.IsDisposed);
    }

    [Fact]
    public async Task Observable_import_requires_an_explicit_cardinality_policy()
    {
        var source = new SequenceObservable<int>([1, 2, 3]);

        Assert.Equal(1, await ObservableInterop.FromObservable(source, ObservableCardinality.First).RunAsync());
        Assert.Equal(3, await ObservableInterop.FromObservable(source, ObservableCardinality.Last).RunAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ObservableInterop.FromObservable(source, ObservableCardinality.ExactlyOne).RunAsync());
        Assert.Equal(
            42,
            await ObservableInterop.FromObservable(
                new SequenceObservable<int>([42]),
                ObservableCardinality.ExactlyOne).RunAsync());
    }

    [Fact]
    public async Task Observable_projection_is_repeatable_and_disposal_is_distinct_from_termination()
    {
        var runs = 0;
        var observable = ColdEffect.Delay(() => ++runs).ToObservable();
        var first = new RecordingObserver<int>();
        var second = new RecordingObserver<int>();
        var firstSubscription = Assert.IsAssignableFrom<ICompletionSubscription>(observable.Subscribe(first));
        var secondSubscription = Assert.IsAssignableFrom<ICompletionSubscription>(observable.Subscribe(second));
        await Task.WhenAll(firstSubscription.Completion, secondSubscription.Completion);
        Assert.Equal([1], first.Values);
        Assert.Equal([2], second.Values);

        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = ColdEffect.DelayAsync(async token =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            }
            finally
            {
                cleanupStarted.TrySetResult();
                await releaseCleanup.Task;
            }
        }).ToObservable();
        var subscription = Assert.IsAssignableFrom<ICompletionSubscription>(slow.Subscribe(new RecordingObserver<int>()));
        subscription.Dispose();
        await cleanupStarted.Task;
        Assert.False(subscription.Completion.IsCompleted);
        releaseCleanup.TrySetResult();
        await subscription.Completion;
    }

    private sealed class RecordingAsyncDisposable : IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            IsDisposed = true;
        }
    }

    private sealed class SequenceObservable<T>(IReadOnlyList<T> values) : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer)
        {
            foreach (var value in values)
            {
                observer.OnNext(value);
            }

            observer.OnCompleted();
            return EmptyDisposable.Instance;
        }
    }

    private sealed class RecordingObserver<T> : IObserver<T>
    {
        public List<T> Values { get; } = [];

        public Exception? Error { get; private set; }

        public bool IsCompleted { get; private set; }

        public void OnCompleted() => IsCompleted = true;

        public void OnError(Exception error) => Error = error;

        public void OnNext(T value) => Values.Add(value);
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
