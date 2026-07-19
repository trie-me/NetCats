using NetCats.Core;

namespace NetCats.Runtime;

public interface IFiber<T>
{
    Task<Outcome<T>> JoinAsync();

    bool RequestCancellation();

    Task<Outcome<T>> CancelAsync();
}

public enum ChildFailurePolicy
{
    Ignore,
    CancelSiblings,
}

public sealed class Fiber<T> : IFiber<T>
{
    private readonly CancelSignal signal = new();
    private readonly TaskCompletionSource<Outcome<T>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Latent<T> operation;
    private readonly Action<Fiber<T>, Outcome<T>> onCompleted;
    private int started;

    internal Fiber(Latent<T> operation, Action<Fiber<T>, Outcome<T>> onCompleted)
    {
        this.operation = operation;
        this.onCompleted = onCompleted;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public Task<Outcome<T>> JoinAsync() => completion.Task;

    public bool RequestCancellation() => signal.Request();

    public async Task<Outcome<T>> CancelAsync()
    {
        RequestCancellation();
        return await completion.Task.ConfigureAwait(false);
    }

    internal void Start()
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            throw new InvalidOperationException("A fiber can only be started once.");
        }

        _ = ExecuteAsync();
    }

    private async Task ExecuteAsync()
    {
        var outcome = await Outcome<T>.CaptureAsync(() => operation.RunAsync(signal.Token)).ConfigureAwait(false);
        completion.TrySetResult(outcome);
        onCompleted(this, outcome);
    }
}

public sealed class FiberScope : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, IOwnedFiber> children = new();
    private bool closing;
    private Task? closeTask;
    private TaskCompletionSource? drained;

    public FiberScope(ChildFailurePolicy failurePolicy = ChildFailurePolicy.CancelSiblings)
    {
        FailurePolicy = failurePolicy;
    }

    public ChildFailurePolicy FailurePolicy { get; }

    public int ActiveChildCount
    {
        get
        {
            lock (gate)
            {
                return children.Count;
            }
        }
    }

    public Fiber<T> Start<T>(Latent<T> operation)
    {
        Fiber<T> fiber;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            fiber = new Fiber<T>(operation, OnCompleted);
            children.Add(fiber.Id, new OwnedFiber<T>(fiber));
        }

        fiber.Start();
        return fiber;
    }

    public Task CloseAsync()
    {
        lock (gate)
        {
            if (closeTask is not null)
            {
                return closeTask;
            }

            closing = true;
            drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (children.Count == 0)
            {
                drained.TrySetResult();
            }

            closeTask = CloseCoreAsync(children.Values.ToArray(), drained.Task);
            return closeTask;
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    private static async Task CloseCoreAsync(IReadOnlyCollection<IOwnedFiber> fibers, Task drained)
    {
        foreach (var fiber in fibers)
        {
            fiber.RequestCancellation();
        }

        await Task.WhenAll(fibers.Select(static fiber => fiber.Completion)).ConfigureAwait(false);
        await drained.ConfigureAwait(false);
    }

    private void OnCompleted<T>(Fiber<T> fiber, Outcome<T> outcome)
    {
        IOwnedFiber[] siblings = [];
        lock (gate)
        {
            children.Remove(fiber.Id);
            if (closing && children.Count == 0)
            {
                drained?.TrySetResult();
            }

            if (FailurePolicy == ChildFailurePolicy.CancelSiblings && outcome is Outcome<T>.Faulted)
            {
                siblings = children.Values.ToArray();
            }
        }

        foreach (var sibling in siblings)
        {
            sibling.RequestCancellation();
        }
    }

    private interface IOwnedFiber
    {
        Task Completion { get; }

        void RequestCancellation();
    }

    private sealed class OwnedFiber<T>(Fiber<T> fiber) : IOwnedFiber
    {
        public Task Completion => fiber.JoinAsync();

        public void RequestCancellation() => fiber.RequestCancellation();
    }
}
