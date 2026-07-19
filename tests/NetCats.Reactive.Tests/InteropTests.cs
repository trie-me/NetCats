using NetCats.Core;
using NetCats.Reactive;

namespace NetCats.Reactive.Tests;

public sealed class InteropTests
{
    [Fact]
    public async Task Task_factories_stay_cold_and_started_tasks_are_explicit_imports()
    {
        var factories = 0;
        var latent = LatentInterop.FromTaskFactory<int>(_ => Task.FromResult(++factories));

        Assert.Equal(0, factories);
        Assert.Equal(1, await latent.RunAsync());
        Assert.Equal(2, await latent.RunAsync());
    }

    [Fact]
    public async Task Observable_import_uses_explicit_cardinality()
    {
        var source = new SequenceObservable<int>([1, 2, 3]);

        Assert.Equal(1, await LatentInterop.FromObservable(source, ObservableCardinality.First).RunAsync());
        Assert.Equal(3, await LatentInterop.FromObservable(source, ObservableCardinality.Last).RunAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => LatentInterop.FromObservable(source, ObservableCardinality.ExactlyOne).RunAsync());
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

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
