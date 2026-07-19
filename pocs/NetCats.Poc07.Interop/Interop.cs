using NetCats.Poc02.Latent;
using NetCats.Poc04.Cancellation;

namespace NetCats.Poc07.Interop;

public static class TaskInterop
{
    public static Latent<T> FromTaskFactory<T>(Func<CancellationToken, Task<T>> factory) =>
        Latent<T>.DelayAsync(factory);

    public static Latent<T> FromValueTaskFactory<T>(Func<CancellationToken, ValueTask<T>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return Latent<T>.DelayAsync(token => factory(token).AsTask());
    }

    public static Latent<T> FromStartedTask<T>(Task<T> task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return Latent<T>.DelayAsync(_ => task);
    }

    public static Task<Outcome<T>> ToOutcomeAsync<T>(
        this Latent<T> latent,
        CancellationToken cancellationToken = default) =>
        Outcome<T>.CaptureAsync(() => latent.RunAsync(cancellationToken));
}

public static class AsyncDisposalInterop
{
    public static Latent<TResult> UseAsync<TResource, TResult>(
        Latent<TResource> acquire,
        Func<TResource, Latent<TResult>> use)
        where TResource : IAsyncDisposable
    {
        ArgumentNullException.ThrowIfNull(use);
        return Latent<TResult>.DelayAsync(async cancellationToken =>
        {
            var resource = await acquire.RunAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await use(resource).RunAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await resource.DisposeAsync().ConfigureAwait(false);
            }
        });
    }
}

public enum ObservableCardinality
{
    ExactlyOne,
    First,
    Last,
}

public interface ICompletionSubscription : IDisposable
{
    Task Completion { get; }
}

public static class ObservableInterop
{
    public static IObservable<T> ToObservable<T>(this Latent<T> latent) => new LatentObservable<T>(latent);

    public static Latent<T> FromObservable<T>(IObservable<T> source, ObservableCardinality cardinality)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Latent<T>.DelayAsync(cancellationToken => ImportAsync(source, cardinality, cancellationToken));
    }

    private static async Task<T> ImportAsync<T>(
        IObservable<T> source,
        ObservableCardinality cardinality,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = new DeferredDisposable();
        using var registration = cancellationToken.Register(() =>
        {
            completion.TrySetCanceled(cancellationToken);
            subscription.Dispose();
        });

        var count = 0;
        T? last = default;
        var observer = new AnonymousObserver<T>(
            value =>
            {
                count++;
                last = value;
                if (cardinality == ObservableCardinality.First)
                {
                    completion.TrySetResult(value);
                    subscription.Dispose();
                }
                else if (cardinality == ObservableCardinality.ExactlyOne && count > 1)
                {
                    completion.TrySetException(new InvalidOperationException("The observable produced more than one value."));
                    subscription.Dispose();
                }
            },
            error => completion.TrySetException(error),
            () =>
            {
                if (count == 0)
                {
                    completion.TrySetException(new InvalidOperationException("The observable completed without a value."));
                }
                else if (cardinality == ObservableCardinality.ExactlyOne && count != 1)
                {
                    completion.TrySetException(new InvalidOperationException("The observable did not produce exactly one value."));
                }
                else
                {
                    completion.TrySetResult(last!);
                }
            });

        subscription.Set(source.Subscribe(observer));
        return await completion.Task.ConfigureAwait(false);
    }

    private sealed class LatentObservable<T>(Latent<T> latent) : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            var source = new CancellationTokenSource();
            var completion = ExecuteAsync(observer, source.Token);
            return new CompletionSubscription(source, completion);
        }

        private async Task ExecuteAsync(IObserver<T> observer, CancellationToken cancellationToken)
        {
            try
            {
                observer.OnNext(await latent.RunAsync(cancellationToken).ConfigureAwait(false));
                observer.OnCompleted();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Subscription disposal is a cancellation request, not an Rx failure.
            }
            catch (Exception error)
            {
                observer.OnError(error);
            }
        }
    }

    private sealed class CompletionSubscription(CancellationTokenSource source, Task completion)
        : ICompletionSubscription
    {
        private int disposed;

        public Task Completion => completion;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                source.Cancel();
            }
        }
    }

    private sealed class AnonymousObserver<T>(
        Action<T> onNext,
        Action<Exception> onError,
        Action onCompleted) : IObserver<T>
    {
        public void OnCompleted() => onCompleted();

        public void OnError(Exception error) => onError(error);

        public void OnNext(T value) => onNext(value);
    }

    private sealed class DeferredDisposable : IDisposable
    {
        private readonly object gate = new();
        private IDisposable? inner;
        private bool disposed;

        public void Set(IDisposable value)
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (gate)
            {
                if (inner is not null)
                {
                    throw new InvalidOperationException("A subscription was already assigned.");
                }

                if (disposed)
                {
                    value.Dispose();
                }
                else
                {
                    inner = value;
                }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                inner?.Dispose();
                inner = null;
            }
        }
    }
}
