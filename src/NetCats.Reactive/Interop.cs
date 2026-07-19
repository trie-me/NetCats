using NetCats.Core;

namespace NetCats.Reactive;

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

public static class LatentInterop
{
    public static Latent<T> FromTaskFactory<T>(Func<CancellationToken, Task<T>> factory) => Latent<T>.DelayAsync(factory);

    public static Latent<T> FromStartedTask<T>(Task<T> task) => Latent<T>.FromStartedTask(task);

    public static IObservable<T> ToObservable<T>(this Latent<T> latent) => new LatentObservable<T>(latent);

    public static Latent<T> FromObservable<T>(IObservable<T> source, ObservableCardinality cardinality)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Latent<T>.DelayAsync(token => ImportAsync(source, cardinality, token));
    }

    private static async Task<T> ImportAsync<T>(IObservable<T> source, ObservableCardinality cardinality, CancellationToken token)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = new DeferredDisposable();
        using var registration = token.Register(() =>
        {
            completion.TrySetCanceled(token);
            subscription.Dispose();
        });
        var count = 0;
        T? last = default;
        subscription.Set(source.Subscribe(new Observer<T>(
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
                else
                {
                    completion.TrySetResult(last!);
                }
            })));
        return await completion.Task.ConfigureAwait(false);
    }

    private sealed class LatentObservable<T>(Latent<T> latent) : IObservable<T>
    {
        public IDisposable Subscribe(IObserver<T> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            var source = new CancellationTokenSource();
            return new CompletionSubscription(source, RunAsync(observer, source.Token));
        }

        private async Task RunAsync(IObserver<T> observer, CancellationToken token)
        {
            try
            {
                observer.OnNext(await latent.RunAsync(token).ConfigureAwait(false));
                observer.OnCompleted();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                observer.OnError(error);
            }
        }
    }

    private sealed class CompletionSubscription(CancellationTokenSource source, Task completion) : ICompletionSubscription
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

    private sealed class Observer<T>(Action<T> onNext, Action<Exception> onError, Action onCompleted) : IObserver<T>
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
