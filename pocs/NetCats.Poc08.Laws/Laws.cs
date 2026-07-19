using NetCats.Poc01.GeneratedKinds;
using NetCats.Poc04.Cancellation;
using NetCats.Poc05.Fibers;
using NetCats.Poc06.Scheduling;
using ColdEffects = NetCats.Poc02.Latent;

namespace NetCats.Poc08.Laws;

public sealed record LawFailure(string Law, string Detail);

public sealed class LawViolationException(IReadOnlyList<LawFailure> failures)
    : Exception(string.Join(Environment.NewLine, failures.Select(failure => $"{failure.Law}: {failure.Detail}")))
{
    public IReadOnlyList<LawFailure> Failures { get; } = failures;
}

public static class KindMonadLaws
{
    public static IReadOnlyList<LawFailure> Validate<F>(
        IMonad<F> monad,
        Func<K<F, int>, int> extract)
    {
        ArgumentNullException.ThrowIfNull(monad);
        ArgumentNullException.ThrowIfNull(extract);
        var failures = new List<LawFailure>();
        var source = monad.Pure(7);
        Func<int, int> first = value => value + 3;
        Func<int, int> second = value => value * 2;
        Func<int, K<F, int>> effectFirst = value => monad.Pure(value + 3);
        Func<int, K<F, int>> effectSecond = value => monad.Pure(value * 2);

        Check(failures, "functor identity", extract(monad.Map(source, static value => value)) == 7);
        Check(
            failures,
            "functor composition",
            extract(monad.Map(monad.Map(source, first), second)) ==
            extract(monad.Map(source, value => second(first(value)))));
        Check(
            failures,
            "monad left identity",
            extract(monad.Bind(monad.Pure(7), effectFirst)) == extract(effectFirst(7)));
        Check(failures, "monad right identity", extract(monad.Bind(source, monad.Pure)) == extract(source));
        Check(
            failures,
            "monad associativity",
            extract(monad.Bind(monad.Bind(source, effectFirst), effectSecond)) ==
            extract(monad.Bind(source, value => monad.Bind(effectFirst(value), effectSecond))));
        return failures;
    }

    public static void Assert<F>(IMonad<F> monad, Func<K<F, int>, int> extract)
    {
        var failures = Validate(monad, extract);
        if (failures.Count != 0)
        {
            throw new LawViolationException(failures);
        }
    }

    private static void Check(ICollection<LawFailure> failures, string law, bool condition)
    {
        if (!condition)
        {
            failures.Add(new LawFailure(law, "The two observable values differed."));
        }
    }
}

public static class LatentLaws
{
    public static async Task<IReadOnlyList<LawFailure>> ValidateAsync()
    {
        var failures = new List<LawFailure>();
        var executions = 0;
        var latent = ColdEffects.Latent<int>.Delay(() => ++executions).Select(static value => value * 10);
        Check(failures, "cold construction", executions == 0);
        var first = await latent.RunAsync().ConfigureAwait(false);
        var second = await latent.RunAsync().ConfigureAwait(false);
        Check(failures, "repeatability", first == 10 && second == 20 && executions == 2);

        var memoized = latent.MemoizeSuccess();
        var memoizedFirst = await memoized.RunAsync().ConfigureAwait(false);
        var memoizedSecond = await memoized.RunAsync().ConfigureAwait(false);
        Check(failures, "memoize success", memoizedFirst == memoizedSecond && executions == 3);
        return failures;
    }

    private static void Check(ICollection<LawFailure> failures, string law, bool condition)
    {
        if (!condition)
        {
            failures.Add(new LawFailure(law, "The observed execution trace violated the law."));
        }
    }
}

public static class RuntimeLaws
{
    public static async Task<IReadOnlyList<LawFailure>> ValidateAsync()
    {
        var failures = new List<LawFailure>();
        var order = new List<int>();
        var finalized = await ProtectedExecution.RunAsync<int>((_, stack) =>
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
            return Task.FromResult(42);
        }).ConfigureAwait(false);
        Check(failures, "finalizer reverse order", finalized is Outcome<int>.Succeeded && order.SequenceEqual([2, 1]));

        await using (var scope = new FiberScope())
        {
            var fiber = scope.Start(async signal =>
            {
                await signal.WhenRequested.ConfigureAwait(false);
                signal.Token.ThrowIfCancellationRequested();
                return 1;
            });
            var outcome = await fiber.CancelAsync().ConfigureAwait(false);
            Check(failures, "fiber cancel and join", outcome is Outcome<int>.Cancelled);
        }

        var time = new ManualTimeProvider();
        var runtime = new LogicalExecutionTree(time, 2).CreateRuntime();
        var delay = runtime.DelayAsync(TimeSpan.FromMinutes(1));
        Check(failures, "virtual delay initially parked", !delay.IsCompleted);
        time.Advance(TimeSpan.FromMinutes(1));
        await delay.ConfigureAwait(false);
        Check(failures, "virtual delay completes", delay.IsCompletedSuccessfully);
        return failures;
    }

    private static void Check(ICollection<LawFailure> failures, string law, bool condition)
    {
        if (!condition)
        {
            failures.Add(new LawFailure(law, "The deterministic runtime trace violated the law."));
        }
    }
}

public sealed class DeterministicRaceSchedule
{
    private readonly int[] choices;
    private int position;

    public DeterministicRaceSchedule(int seed, int length)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        Seed = seed;
        var random = new Random(seed);
        choices = Enumerable.Range(0, length).Select(_ => random.Next(2)).ToArray();
    }

    private DeterministicRaceSchedule(int seed, int[] choices)
    {
        Seed = seed;
        this.choices = choices;
    }

    public int Seed { get; }

    public string Trace => string.Join(string.Empty, choices.Select(static choice => (char)('0' + choice)));

    public int Next()
    {
        if (position == choices.Length)
        {
            throw new InvalidOperationException("The race schedule is exhausted.");
        }

        return choices[position++];
    }

    public static DeterministicRaceSchedule Replay(string trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        if (trace.Any(static value => value is not ('0' or '1')))
        {
            throw new FormatException("A race trace may contain only '0' and '1'.");
        }

        return new DeterministicRaceSchedule(0, trace.Select(static value => value - '0').ToArray());
    }
}
