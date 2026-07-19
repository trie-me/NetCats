using System.Runtime.ExceptionServices;

namespace NetCats.Core;

/// <summary> A cold, repeatable description of asynchronous work. </summary>
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
        {
            var task = factory(cancellationToken) ??
                throw new InvalidOperationException("A Latent factory returned null.");
            return await task.ConfigureAwait(false);
        }));
    }

    public static Latent<T> FromStartedTask(Task<T> task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return DelayAsync(_ => task);
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
        Func<T, TIntermediate, TResult> projector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(projector);
        return SelectMany(value => selector(value).Select(intermediate => projector(value, intermediate)));
    }

    public Latent<T> RecoverWith(Func<Exception, Latent<T>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return new(new HandleErrorInstruction(GetInstruction(), error => handler(error).GetInstruction()));
    }

    public Latent<T> Recover(Func<Exception, T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RecoverWith(error => Delay(() => handler(error)));
    }

    public Latent<T> Repeat()
    {
        var source = this;
        return DelayAsync(source.RunAsync);
    }

    public Task<T> RunAsync(CancellationToken cancellationToken = default) =>
        RunAsync(cancellationToken, LatentExecutionOptions.Default);

    public Task<T> RunAsync(CancellationToken cancellationToken, LatentExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Interpreter.RunAsync<T>(GetInstruction(), cancellationToken, options.OperationBudget);
    }

    internal Instruction GetInstruction() => instruction ??
        new FailureInstruction(new InvalidOperationException("An uninitialized Latent value cannot be run."));
}

public sealed record LatentExecutionOptions
{
    public static LatentExecutionOptions Default { get; } = new();

    public int OperationBudget
    {
        get;
        init => field = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "The operation budget must be positive.");
    } = 512;
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
    public static async Task<T> RunAsync<T>(Instruction root, CancellationToken cancellationToken, int operationBudget)
    {
        var frames = new Stack<Frame>();
        var current = root;
        object? value = null;
        Exception? error = null;
        var operations = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++operations == operationBudget)
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
