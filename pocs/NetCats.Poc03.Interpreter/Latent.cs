using System.Runtime.ExceptionServices;

namespace NetCats.Poc03.Interpreter;

public readonly struct Latent<T>
{
    private readonly Instruction? instruction;

    private Latent(Instruction instruction)
    {
        this.instruction = instruction;
    }

    public static Latent<T> Pure(T value) => new(new PureInstruction(value));

    public static Latent<T> Delay(Func<T> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new(new DelayInstruction(() => factory()));
    }

    public static Latent<T> DelayAsync(Func<CancellationToken, Task<T>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new(new AsyncInstruction(async cancellationToken =>
            await factory(cancellationToken).ConfigureAwait(false)));
    }

    public static Latent<T> Fail(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(new FailureInstruction(error));
    }

    public Latent<TResult> Select<TResult>(Func<T, TResult> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return SelectMany(value => Latent<TResult>.Delay(() => selector(value)));
    }

    public Latent<TResult> Map<TResult>(Func<T, TResult> selector) => Select(selector);

    public Latent<TResult> SelectMany<TResult>(Func<T, Latent<TResult>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return new Latent<TResult>(new BindInstruction(
            GetInstruction(),
            value => selector((T)value!).GetInstruction()));
    }

    public Latent<TResult> Bind<TResult>(Func<T, Latent<TResult>> selector) => SelectMany(selector);

    public Latent<TResult> SelectMany<TIntermediate, TResult>(
        Func<T, Latent<TIntermediate>> selector,
        Func<T, TIntermediate, TResult> projector) =>
        SelectMany(value => selector(value).Select(intermediate => projector(value, intermediate)));

    public Latent<T> RecoverWith(Func<Exception, Latent<T>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return new Latent<T>(new HandleErrorInstruction(GetInstruction(), error => handler(error).GetInstruction()));
    }

    public Task<T> RunAsync(CancellationToken cancellationToken = default, int operationBudget = 512)
    {
        if (operationBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(operationBudget));
        }

        return Interpreter.RunAsync<T>(GetInstruction(), cancellationToken, operationBudget);
    }

    internal Instruction GetInstruction() => instruction ??
        new FailureInstruction(new InvalidOperationException("An uninitialized Latent value cannot be run."));
}

internal abstract record Instruction;

internal sealed record PureInstruction(object? Value) : Instruction;

internal sealed record DelayInstruction(Func<object?> Factory) : Instruction;

internal sealed record AsyncInstruction(Func<CancellationToken, Task<object?>> Factory) : Instruction;

internal sealed record FailureInstruction(Exception Error) : Instruction;

internal sealed record BindInstruction(Instruction Source, Func<object?, Instruction> Continuation) : Instruction;

internal sealed record HandleErrorInstruction(Instruction Source, Func<Exception, Instruction> Handler) : Instruction;

internal static class Interpreter
{
    public static async Task<T> RunAsync<T>(
        Instruction root,
        CancellationToken cancellationToken,
        int operationBudget)
    {
        var frames = new Stack<Frame>();
        var current = root;
        object? value = null;
        Exception? error = null;
        var operations = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operations++;
            if (operations == operationBudget)
            {
                operations = 0;
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
            }

            switch (current)
            {
                case BindInstruction bind:
                    frames.Push(new BindFrame(bind.Continuation));
                    current = bind.Source;
                    continue;

                case HandleErrorInstruction handle:
                    frames.Push(new ErrorFrame(handle.Handler));
                    current = handle.Source;
                    continue;

                case PureInstruction pure:
                    value = pure.Value;
                    error = null;
                    break;

                case FailureInstruction failure:
                    error = failure.Error;
                    break;

                case DelayInstruction delay:
                    try
                    {
                        value = delay.Factory();
                        error = null;
                    }
                    catch (Exception caught)
                    {
                        error = caught;
                    }

                    break;

                case AsyncInstruction asynchronous:
                    try
                    {
                        value = await asynchronous.Factory(cancellationToken).ConfigureAwait(false);
                        error = null;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception caught)
                    {
                        error = caught;
                    }

                    break;

                default:
                    throw new InvalidOperationException($"Unknown instruction {current.GetType()}.");
            }

            var selectedNext = false;
            while (frames.TryPop(out var frame))
            {
                if (error is null)
                {
                    if (frame is ErrorFrame)
                    {
                        continue;
                    }

                    try
                    {
                        current = ((BindFrame)frame).Continuation(value);
                    }
                    catch (Exception caught)
                    {
                        current = new FailureInstruction(caught);
                    }

                    selectedNext = true;
                    break;
                }

                if (frame is BindFrame)
                {
                    continue;
                }

                try
                {
                    current = ((ErrorFrame)frame).Handler(error);
                }
                catch (Exception caught)
                {
                    current = new FailureInstruction(caught);
                }

                error = null;
                selectedNext = true;
                break;
            }

            if (selectedNext)
            {
                continue;
            }

            if (error is not null)
            {
                ExceptionDispatchInfo.Capture(error).Throw();
            }

            return (T)value!;
        }
    }

    private abstract record Frame;

    private sealed record BindFrame(Func<object?, Instruction> Continuation) : Frame;

    private sealed record ErrorFrame(Func<Exception, Instruction> Handler) : Frame;
}

public readonly struct DelegateLatent<T>
{
    private readonly Func<CancellationToken, Task<T>> run;

    private DelegateLatent(Func<CancellationToken, Task<T>> run)
    {
        this.run = run;
    }

    public static DelegateLatent<T> Pure(T value) => new(_ => Task.FromResult(value));

    public DelegateLatent<TResult> Bind<TResult>(Func<T, DelegateLatent<TResult>> selector)
    {
        var source = run;
        return new DelegateLatent<TResult>(async cancellationToken =>
            await selector(await source(cancellationToken).ConfigureAwait(false))
                .run(cancellationToken)
                .ConfigureAwait(false));
    }

    public Task<T> RunAsync(CancellationToken cancellationToken = default) => run(cancellationToken);
}
