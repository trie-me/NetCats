using System.Diagnostics;
using System.Runtime.CompilerServices;
using NetCats.Poc03.Interpreter;
using NetCats.Poc04.Cancellation;
using NetCats.Poc05.Fibers;
using NetCats.Poc07.Interop;
using ColdEffects = NetCats.Poc02.Latent;
using Kinds = NetCats.Poc01.GeneratedKinds;

namespace NetCats.Poc09.Performance;

public sealed record PerformanceMeasurement(
    string Name,
    int Operations,
    TimeSpan Elapsed,
    long AllocatedBytes)
{
    public double NanosecondsPerOperation => Elapsed.TotalNanoseconds / Math.Max(1, Operations);

    public double BytesPerOperation => (double)AllocatedBytes / Math.Max(1, Operations);
}

public sealed record DeploymentCapabilities(
    bool IsDynamicCodeSupported,
    bool IsDynamicCodeCompiled,
    string FrameworkDescription);

public static class PerformanceProbe
{
    public static async Task<PerformanceMeasurement> MeasureAsync(
        string name,
        int operations,
        Func<Task> workload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(operations);
        ArgumentNullException.ThrowIfNull(workload);
        await workload().ConfigureAwait(false);
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        await workload().ConfigureAwait(false);
        stopwatch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        return new PerformanceMeasurement(name, operations, stopwatch.Elapsed, allocated);
    }

    public static async Task<IReadOnlyList<PerformanceMeasurement>> RunRepresentativeAsync(
        int bindDepth = 10_000,
        int fanOut = 32)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bindDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fanOut);
        var direct = await MeasureAsync("direct-task-bind-loop", bindDepth, async () =>
        {
            var value = 0;
            for (var index = 0; index < bindDepth; index++)
            {
                value = await Task.FromResult(value + 1).ConfigureAwait(false);
            }

            GC.KeepAlive(value);
        }).ConfigureAwait(false);

        var interpreted = await MeasureAsync("instruction-tree-binds", bindDepth, async () =>
        {
            var latent = Latent<int>.Pure(0);
            for (var index = 0; index < bindDepth; index++)
            {
                latent = latent.Bind(static value => Latent<int>.Pure(value + 1));
            }

            GC.KeepAlive(await latent.RunAsync(operationBudget: 512).ConfigureAwait(false));
        }).ConfigureAwait(false);

        var fibers = await MeasureAsync("fiber-fan-out-in", fanOut, async () =>
        {
            await using var scope = new FiberScope(ChildFailurePolicy.Ignore);
            var outcomes = await FiberCombinators.ParallelAsync(
                scope,
                Enumerable.Range(0, fanOut)
                    .Select<int, Func<CancelSignal, Task<int>>>(value => _ => Task.FromResult(value)))
                .ConfigureAwait(false);
            GC.KeepAlive(outcomes);
        }).ConfigureAwait(false);

        var cancellation = await MeasureAsync("cancellation-storm", fanOut, async () =>
        {
            await using var scope = new FiberScope(ChildFailurePolicy.Ignore);
            var active = Enumerable.Range(0, fanOut)
                .Select(_ => scope.Start<int>(async signal =>
                {
                    await signal.WhenRequested.ConfigureAwait(false);
                    signal.Token.ThrowIfCancellationRequested();
                    return 0;
                }))
                .ToArray();
            await Task.WhenAll(active.Select(static fiber => fiber.CancelAsync())).ConfigureAwait(false);
        }).ConfigureAwait(false);

        var generatedKinds = await MeasureAsync("generated-kind-map", bindDepth, () =>
        {
            var monad = new Kinds.LatentKMonad();
            for (var index = 0; index < bindDepth; index++)
            {
                var kind = Kinds.KindFunctions.MapTwice(
                    monad,
                    Kinds.LatentKExtensions.ToKind(new Kinds.Latent<int>(index)),
                    static value => value + 1,
                    static value => value * 2);
                GC.KeepAlive(kind.Get<Kinds.Latent<int>>().Value);
            }

            return Task.CompletedTask;
        }).ConfigureAwait(false);

        var resourceIterations = Math.Max(1, bindDepth / 10);
        var resources = await MeasureAsync("protected-finalizers", resourceIterations, async () =>
        {
            for (var index = 0; index < resourceIterations; index++)
            {
                var outcome = await ProtectedExecution.RunAsync<int>((_, stack) =>
                {
                    stack.Register(_ => Task.CompletedTask);
                    return Task.FromResult(1);
                }).ConfigureAwait(false);
                GC.KeepAlive(outcome);
            }
        }).ConfigureAwait(false);

        var observable = await MeasureAsync("observable-projection", fanOut, async () =>
        {
            for (var index = 0; index < fanOut; index++)
            {
                var subscription = (ICompletionSubscription)ColdEffects.Latent<int>
                    .Pure(index)
                    .ToObservable()
                    .Subscribe(NullObserver<int>.Instance);
                await subscription.Completion.ConfigureAwait(false);
                subscription.Dispose();
            }
        }).ConfigureAwait(false);

        var tracing = await MeasureAsync("tracing-enabled", bindDepth, () =>
        {
            var traceEvents = 0;
            Action<string> trace = _ => traceEvents++;
            for (var index = 0; index < bindDepth; index++)
            {
                trace("bind");
            }

            GC.KeepAlive(traceEvents);
            return Task.CompletedTask;
        }).ConfigureAwait(false);

        return [direct, interpreted, fibers, cancellation, generatedKinds, resources, observable, tracing];
    }

    public static DeploymentCapabilities GetDeploymentCapabilities() => new(
        RuntimeFeature.IsDynamicCodeSupported,
        RuntimeFeature.IsDynamicCodeCompiled,
        System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);

    private sealed class NullObserver<T> : IObserver<T>
    {
        public static NullObserver<T> Instance { get; } = new();

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(T value)
        {
        }
    }
}
