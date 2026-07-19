namespace NetCats.Poc04.Cancellation;

public sealed class CancelSignal : IDisposable
{
    private readonly CancellationTokenSource source = new();
    private readonly TaskCompletionSource requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int isRequested;

    public bool IsRequested => Volatile.Read(ref isRequested) != 0;

    public CancellationToken Token => source.Token;

    public Task WhenRequested => requested.Task;

    public bool Request()
    {
        if (Interlocked.Exchange(ref isRequested, 1) != 0)
        {
            return false;
        }

        requested.TrySetResult();
        source.Cancel();
        return true;
    }

    public void Dispose() => source.Dispose();
}

public sealed class CancellationContext
{
    private int maskDepth;

    public CancellationContext(CancelSignal signal)
    {
        Signal = signal ?? throw new ArgumentNullException(nameof(signal));
    }

    public CancelSignal Signal { get; }

    public bool IsMasked => maskDepth != 0;

    public CancellationToken NativeToken => IsMasked ? CancellationToken.None : Signal.Token;

    public void Checkpoint()
    {
        if (!IsMasked && Signal.IsRequested)
        {
            throw new OperationCanceledException(Signal.Token);
        }
    }

    public async Task<T> UncancelableAsync<T>(Func<Poll, Task<T>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var restoreDepth = maskDepth;
        maskDepth++;
        T result;
        try
        {
            result = await body(new Poll(this, restoreDepth)).ConfigureAwait(false);
        }
        finally
        {
            maskDepth = restoreDepth;
        }

        Checkpoint();
        return result;
    }

    public readonly struct Poll
    {
        private readonly CancellationContext context;
        private readonly int restoreDepth;

        internal Poll(CancellationContext context, int restoreDepth)
        {
            this.context = context;
            this.restoreDepth = restoreDepth;
        }

        public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation)
        {
            ArgumentNullException.ThrowIfNull(operation);
            var protectedDepth = context.maskDepth;
            context.maskDepth = restoreDepth;
            try
            {
                context.Checkpoint();
                return await operation(context.NativeToken).ConfigureAwait(false);
            }
            finally
            {
                context.maskDepth = protectedDepth;
            }
        }
    }
}

public abstract record Outcome<T>
{
    private Outcome()
    {
    }

    public sealed record Succeeded(T Value) : Outcome<T>;

    public sealed record Faulted(Exception Error) : Outcome<T>;

    public sealed record Cancelled : Outcome<T>;

    public static async Task<Outcome<T>> CaptureAsync(Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            return new Succeeded(await operation().ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            return new Cancelled();
        }
        catch (Exception error)
        {
            return new Faulted(error);
        }
    }
}

public enum ExitCase
{
    Succeeded,
    Faulted,
    Cancelled,
}

public sealed class FinalizerStack
{
    private readonly object gate = new();
    private readonly Stack<Func<ExitCase, Task>> finalizers = new();
    private Task<IReadOnlyList<Exception>>? execution;

    public void Register(Func<ExitCase, Task> finalizer)
    {
        ArgumentNullException.ThrowIfNull(finalizer);
        lock (gate)
        {
            if (execution is not null)
            {
                throw new InvalidOperationException("Finalizers cannot be registered after finalization starts.");
            }

            finalizers.Push(finalizer);
        }
    }

    public Task<IReadOnlyList<Exception>> RunAsync(ExitCase exitCase)
    {
        lock (gate)
        {
            return execution ??= RunCoreAsync(finalizers.ToArray(), exitCase);
        }
    }

    private static async Task<IReadOnlyList<Exception>> RunCoreAsync(
        IReadOnlyList<Func<ExitCase, Task>> pending,
        ExitCase exitCase)
    {
        List<Exception>? errors = null;
        foreach (var finalizer in pending)
        {
            try
            {
                await finalizer(exitCase).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                (errors ??= []).Add(error);
            }
        }

        return errors ?? [];
    }
}

public static class ProtectedExecution
{
    public static async Task<Outcome<T>> RunAsync<T>(
        Func<CancellationContext, FinalizerStack, Task<T>> operation,
        CancelSignal? signal = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var ownsSignal = signal is null;
        signal ??= new CancelSignal();
        try
        {
            var context = new CancellationContext(signal);
            var finalizers = new FinalizerStack();
            Outcome<T> outcome;
            try
            {
                context.Checkpoint();
                outcome = new Outcome<T>.Succeeded(
                    await operation(context, finalizers).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (signal.IsRequested)
            {
                outcome = new Outcome<T>.Cancelled();
            }
            catch (Exception error)
            {
                outcome = new Outcome<T>.Faulted(error);
            }

            var exitCase = outcome switch
            {
                Outcome<T>.Succeeded => ExitCase.Succeeded,
                Outcome<T>.Faulted => ExitCase.Faulted,
                _ => ExitCase.Cancelled,
            };
            var finalizerErrors = await finalizers.RunAsync(exitCase).ConfigureAwait(false);
            return Combine(outcome, finalizerErrors);
        }
        finally
        {
            if (ownsSignal)
            {
                signal.Dispose();
            }
        }
    }

    public static Task<Outcome<T>> GuaranteeCaseAsync<T>(
        Func<CancellationContext, Task<T>> operation,
        Func<ExitCase, Task> finalizer,
        CancelSignal? signal = null) =>
        RunAsync<T>(
            (context, stack) =>
            {
                stack.Register(finalizer);
                return operation(context);
            },
            signal);

    private static Outcome<T> Combine<T>(Outcome<T> outcome, IReadOnlyList<Exception> finalizerErrors)
    {
        if (finalizerErrors.Count == 0)
        {
            return outcome;
        }

        var errors = outcome is Outcome<T>.Faulted faulted
            ? new[] { faulted.Error }.Concat(finalizerErrors)
            : finalizerErrors;
        return new Outcome<T>.Faulted(new AggregateException(errors));
    }
}
