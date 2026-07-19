namespace NetCats.Poc02.Latent;

public readonly struct Latent<T>
{
    private readonly Func<CancellationToken, Task<T>> run;

    private Latent(Func<CancellationToken, Task<T>> run)
    {
        this.run = run;
    }

    public static Latent<T> Pure(T value) => new(_ => Task.FromResult(value));

    public static Latent<T> Delay(Func<T> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new(_ => Invoke(factory));
    }

    public static Latent<T> DelayAsync(Func<CancellationToken, Task<T>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new(token => InvokeAsync(factory, token));
    }

    public Task<T> RunAsync(CancellationToken cancellationToken = default)
    {
        if (run is null)
        {
            return Task.FromException<T>(new InvalidOperationException("An uninitialized Latent value cannot be run."));
        }

        return InvokeAsync(run, cancellationToken);
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
        var source = this;
        return Latent<TResult>.DelayAsync(async cancellationToken =>
        {
            var value = await source.RunAsync(cancellationToken).ConfigureAwait(false);
            var next = selector(value);
            return await next.RunAsync(cancellationToken).ConfigureAwait(false);
        });
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

    public Latent<T> Recover(Func<Exception, T> recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        return RecoverWith(error => Delay(() => recovery(error)));
    }

    public Latent<T> RecoverWith(Func<Exception, Latent<T>> recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        var source = this;
        return DelayAsync(async cancellationToken =>
        {
            try
            {
                return await source.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return await recovery(error).RunAsync(cancellationToken).ConfigureAwait(false);
            }
        });
    }

    public Latent<T> Repeat()
    {
        var source = this;
        return DelayAsync(source.RunAsync);
    }

    public Latent<T> MemoizeSuccess() => MemoizedSuccess<T>.Create(this);

    public Latent<T> MemoizeOutcome() => MemoizedOutcome<T>.Create(this);

    private static Task<T> Invoke(Func<T> factory)
    {
        try
        {
            return Task.FromResult(factory());
        }
        catch (Exception error)
        {
            return Task.FromException<T>(error);
        }
    }

    private static Task<T> InvokeAsync(Func<CancellationToken, Task<T>> factory, CancellationToken token)
    {
        try
        {
            return factory(token) ?? Task.FromException<T>(new InvalidOperationException("A Latent factory returned null."));
        }
        catch (Exception error)
        {
            return Task.FromException<T>(error);
        }
    }
}

internal sealed class MemoizedOutcome<T>
{
    private readonly object gate = new();
    private readonly Latent<T> source;
    private Task<T>? cached;

    private MemoizedOutcome(Latent<T> source)
    {
        this.source = source;
    }

    public static Latent<T> Create(Latent<T> source)
    {
        var state = new MemoizedOutcome<T>(source);
        return Latent<T>.DelayAsync(state.RunAsync);
    }

    private Task<T> RunAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return cached ??= source.RunAsync(cancellationToken);
        }
    }
}

internal sealed class MemoizedSuccess<T>
{
    private readonly object gate = new();
    private readonly Latent<T> source;
    private Task<T>? current;

    private MemoizedSuccess(Latent<T> source)
    {
        this.source = source;
    }

    public static Latent<T> Create(Latent<T> source)
    {
        var state = new MemoizedSuccess<T>(source);
        return Latent<T>.DelayAsync(state.RunAsync);
    }

    private Task<T> RunAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (current is { IsCompletedSuccessfully: true })
            {
                return current;
            }

            if (current is null || current.IsCompleted)
            {
                current = source.RunAsync(cancellationToken);
            }

            return current;
        }
    }
}
