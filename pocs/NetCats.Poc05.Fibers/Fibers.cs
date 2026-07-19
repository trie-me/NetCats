using NetCats.Poc04.Cancellation;

namespace NetCats.Poc05.Fibers;

public enum ChildFailurePolicy
{
    Ignore,
    CancelSiblings,
}

public sealed class Fiber<T>
{
    private readonly CancelSignal signal = new();
    private readonly TaskCompletionSource<Outcome<T>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<CancelSignal, Task<T>> operation;
    private readonly Action<Fiber<T>, Outcome<T>> onCompleted;
    private int started;

    internal Fiber(Func<CancelSignal, Task<T>> operation, Action<Fiber<T>, Outcome<T>> onCompleted, bool isDetached)
    {
        this.operation = operation;
        this.onCompleted = onCompleted;
        IsDetached = isDetached;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public bool IsDetached { get; }

    public bool IsCancellationRequested => signal.IsRequested;

    public static Fiber<T> StartDetached(Func<CancelSignal, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var fiber = new Fiber<T>(operation, static (_, _) => { }, isDetached: true);
        fiber.Start();
        return fiber;
    }

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
        Outcome<T> outcome;
        try
        {
            signal.Token.ThrowIfCancellationRequested();
            outcome = new Outcome<T>.Succeeded(await operation(signal).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (signal.IsRequested)
        {
            outcome = new Outcome<T>.Cancelled();
        }
        catch (Exception error)
        {
            outcome = new Outcome<T>.Faulted(error);
        }

        completion.TrySetResult(outcome);
        onCompleted(this, outcome);
    }
}

public sealed class FiberScope : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, IOwnedFiber> children = new();
    private readonly Dictionary<Guid, FiberScope> nestedScopes = new();
    private readonly Action<FiberScope>? onClosed;
    private bool closing;
    private Task? closeTask;

    public FiberScope(ChildFailurePolicy failurePolicy = ChildFailurePolicy.CancelSiblings)
        : this(failurePolicy, null)
    {
    }

    private FiberScope(ChildFailurePolicy failurePolicy, Action<FiberScope>? onClosed)
    {
        FailurePolicy = failurePolicy;
        this.onClosed = onClosed;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public ChildFailurePolicy FailurePolicy { get; }

    public int ActiveChildCount
    {
        get
        {
            lock (gate)
            {
                return children.Count + nestedScopes.Count;
            }
        }
    }

    public IReadOnlyList<Guid> ActiveChildIds
    {
        get
        {
            lock (gate)
            {
                return children.Keys.Concat(nestedScopes.Keys).ToArray();
            }
        }
    }

    public Fiber<T> Start<T>(Func<CancelSignal, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Fiber<T> fiber;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            fiber = new Fiber<T>(operation, OnFiberCompleted, isDetached: false);
            children.Add(fiber.Id, new OwnedFiber<T>(fiber));
        }

        fiber.Start();
        return fiber;
    }

    public FiberScope CreateChild(ChildFailurePolicy? failurePolicy = null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            var child = new FiberScope(failurePolicy ?? FailurePolicy, OnNestedScopeClosed);
            nestedScopes.Add(child.Id, child);
            return child;
        }
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
            closeTask = CloseCoreAsync(children.Values.ToArray(), nestedScopes.Values.ToArray());
            return closeTask;
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    private async Task CloseCoreAsync(
        IReadOnlyCollection<IOwnedFiber> fibers,
        IReadOnlyCollection<FiberScope> scopes)
    {
        foreach (var fiber in fibers)
        {
            fiber.RequestCancellation();
        }

        await Task.WhenAll(scopes.Select(static scope => scope.CloseAsync())).ConfigureAwait(false);
        await Task.WhenAll(fibers.Select(static fiber => fiber.Completion)).ConfigureAwait(false);
        onClosed?.Invoke(this);
    }

    private void OnFiberCompleted<T>(Fiber<T> fiber, Outcome<T> outcome)
    {
        IOwnedFiber[] siblings = [];
        lock (gate)
        {
            children.Remove(fiber.Id);
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

    private void OnNestedScopeClosed(FiberScope scope)
    {
        lock (gate)
        {
            nestedScopes.Remove(scope.Id);
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

public static class FiberCombinators
{
    public static async Task<Outcome<T>> RaceAsync<T>(
        FiberScope scope,
        Func<CancelSignal, Task<T>> left,
        Func<CancelSignal, Task<T>> right)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var leftFiber = scope.Start(left);
        var rightFiber = scope.Start(right);
        var winnerTask = await Task.WhenAny(leftFiber.JoinAsync(), rightFiber.JoinAsync()).ConfigureAwait(false);
        var loser = ReferenceEquals(winnerTask, leftFiber.JoinAsync()) ? rightFiber : leftFiber;
        var winner = await winnerTask.ConfigureAwait(false);
        await loser.CancelAsync().ConfigureAwait(false);
        return winner;
    }

    public static async Task<IReadOnlyList<Outcome<T>>> ParallelAsync<T>(
        FiberScope scope,
        IEnumerable<Func<CancelSignal, Task<T>>> operations)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(operations);
        var fibers = operations.Select(scope.Start).ToArray();
        return await Task.WhenAll(fibers.Select(static fiber => fiber.JoinAsync())).ConfigureAwait(false);
    }
}
