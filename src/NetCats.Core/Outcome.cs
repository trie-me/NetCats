namespace NetCats.Core;

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

public static class Outcome
{
    public static ExitCase ToExitCase<T>(Outcome<T> outcome) => outcome switch
    {
        Outcome<T>.Succeeded => ExitCase.Succeeded,
        Outcome<T>.Faulted => ExitCase.Faulted,
        _ => ExitCase.Cancelled,
    };
}
