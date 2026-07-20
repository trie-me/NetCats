using NetCats.Core;

namespace NetCats.Runtime;

/// <summary>Describes how failures in one owned fiber affect its siblings.</summary>
public enum ChildFailurePolicy
{
    Ignore,
    CancelSiblings,
}

/// <summary>The lifecycle state of a scope.</summary>
public enum FiberScopeState
{
    Open,
    Closing,
    Closed,
}

/// <summary>The lifecycle state of a fiber.</summary>
public enum FiberLifecycleState
{
    Running,
    CancellationRequested,
    Terminated,
}

/// <summary>The terminal outcome retained by fiber diagnostics.</summary>
public enum FiberOutcomeKind
{
    Succeeded,
    Cancelled,
    Faulted,
}

/// <summary>The kind of lifecycle transition observed by a <see cref="IFiberObserver"/>.</summary>
public enum FiberObservationKind
{
    ScopeOpened,
    ScopeClosing,
    ScopeClosed,
    FiberStarted,
    FiberCancellationRequested,
    FiberTerminated,
}

/// <summary>A bounded, display-only label for a scope or fiber.</summary>
public sealed record FiberDescriptor(string? Name = null)
{
    internal string DisplayName => FiberScopeOptions.NormalizeName(Name, "fiber");
}

/// <summary>Configures structured ownership and optional non-blocking observation for a scope.</summary>
public sealed record FiberScopeOptions(
    string? Name = null,
    IFiberObserver? Observer = null,
    ChildFailurePolicy FailurePolicy = ChildFailurePolicy.CancelSiblings,
    Func<Exception, string?>? ErrorCategoryProvider = null)
{
    internal string DisplayName => NormalizeName(Name, "scope");

    internal static string NormalizeName(string? value, string fallback)
    {
        if (String.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Trim() is { Length: > 128 } name ? name[..128] : value.Trim();
    }
}

/// <summary>A synchronous, best-effort lifecycle observation. It never includes effect values or exceptions.</summary>
public sealed record FiberObservation(
    FiberObservationKind Kind,
    DateTimeOffset ObservedAt,
    Guid ScopeId,
    Guid? ParentScopeId,
    Guid? ParentFiberId,
    string ScopeName,
    FiberScopeState? ScopeState,
    Guid? FiberId = null,
    string? FiberName = null,
    FiberLifecycleState? FiberState = null,
    FiberOutcomeKind? Outcome = null,
    string? ErrorCategory = null);

/// <summary>
/// Receives lifecycle facts after the runtime releases its ownership locks. Implementations must be synchronous,
/// non-blocking, and must not throw.
/// </summary>
public interface IFiberObserver
{
    void Observe(FiberObservation observation);
}

public interface IFiber<T>
{
    Task<Outcome<T>> JoinAsync();

    bool RequestCancellation();

    Task<Outcome<T>> CancelAsync();
}

public sealed class Fiber<T> : IFiber<T>
{
    private readonly CancelSignal signal = new();
    private readonly TaskCompletionSource<Outcome<T>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Latent<T> operation;
    private readonly Action<Fiber<T>, Outcome<T>> onCompleted;
    private readonly Action<Fiber<T>> onCancellationRequested;
    private int started;

    internal Fiber(
        Latent<T> operation,
        Action<Fiber<T>, Outcome<T>> onCompleted,
        Action<Fiber<T>> onCancellationRequested,
        Guid scopeId,
        string name,
        IFiberObserver? observer,
        Func<Exception, string?>? errorCategoryProvider)
    {
        this.operation = operation;
        this.onCompleted = onCompleted;
        this.onCancellationRequested = onCancellationRequested;
        ScopeId = scopeId;
        Name = name;
        Observer = observer;
        ErrorCategoryProvider = errorCategoryProvider;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public Guid ScopeId { get; }

    public string Name { get; }

    internal IFiberObserver? Observer { get; }

    internal Func<Exception, string?>? ErrorCategoryProvider { get; }

    public Task<Outcome<T>> JoinAsync() => completion.Task;

    public bool RequestCancellation()
    {
        if (!signal.Request())
        {
            return false;
        }

        onCancellationRequested(this);
        return true;
    }

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
        onCompleted(this, outcome);
        completion.TrySetResult(outcome);
    }
}

/// <summary>
/// Owns fibers and nested scopes. Closing a scope requests cancellation and joins every owned child.
/// </summary>
public sealed class FiberScope : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, IOwnedFiber> fibers = [];
    private readonly Dictionary<Guid, FiberScope> scopes = [];
    private readonly Action<FiberScope>? onClosed;
    private bool closing;
    private Task? closeTask;

    public FiberScope(ChildFailurePolicy failurePolicy = ChildFailurePolicy.CancelSiblings)
        : this(new FiberScopeOptions(FailurePolicy: failurePolicy), null, null, null)
    {
        Publish(FiberObservationKind.ScopeOpened, FiberScopeState.Open);
    }

    private FiberScope(FiberScopeOptions options, FiberScope? parent, Guid? parentFiberId, Action<FiberScope>? onClosed)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = parent is null || options.Observer is not null
            ? options
            : options with
            {
                Observer = parent.Options.Observer,
                ErrorCategoryProvider = options.ErrorCategoryProvider ?? parent.Options.ErrorCategoryProvider,
            };
        ParentScopeId = parent?.Id;
        ParentFiberId = parentFiberId;
        this.onClosed = onClosed;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public Guid? ParentScopeId { get; }

    public Guid? ParentFiberId { get; }

    public FiberScopeOptions Options { get; }

    public string Name => Options.DisplayName;

    public ChildFailurePolicy FailurePolicy => Options.FailurePolicy;

    public static FiberScope CreateRoot(FiberScopeOptions? options = null)
    {
        var scope = new FiberScope(options ?? new FiberScopeOptions(), null, null, null);
        scope.Publish(FiberObservationKind.ScopeOpened, FiberScopeState.Open);
        return scope;
    }

    public int ActiveChildCount
    {
        get
        {
            lock (gate)
            {
                return fibers.Count + scopes.Count;
            }
        }
    }

    public Fiber<T> Start<T>(Latent<T> operation) => Start(operation, new FiberDescriptor());

    public Fiber<T> Start<T>(Latent<T> operation, FiberDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Fiber<T> fiber;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            fiber = new Fiber<T>(
                operation,
                OnCompleted,
                OnCancellationRequested,
                Id,
                descriptor.DisplayName,
                Options.Observer,
                Options.ErrorCategoryProvider);
            fibers.Add(fiber.Id, new OwnedFiber<T>(fiber));
        }

        Publish(
            FiberObservationKind.FiberStarted,
            fiberId: fiber.Id,
            fiberName: fiber.Name,
            fiberState: FiberLifecycleState.Running);
        fiber.Start();
        return fiber;
    }

    public FiberScope CreateChild(FiberScopeOptions? options = null, Guid? parentFiberId = null)
    {
        FiberScope child;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            child = new FiberScope(options ?? new FiberScopeOptions(FailurePolicy: FailurePolicy), this, parentFiberId, OnNestedScopeClosed);
            scopes.Add(child.Id, child);
        }

        child.Publish(FiberObservationKind.ScopeOpened, FiberScopeState.Open);
        return child;
    }

    public Task CloseAsync()
    {
        IOwnedFiber[] ownedFibers;
        FiberScope[] ownedScopes;
        lock (gate)
        {
            if (closeTask is not null)
            {
                return closeTask;
            }

            closing = true;
            ownedFibers = fibers.Values.ToArray();
            ownedScopes = scopes.Values.ToArray();
            closeTask = CloseCoreAsync(ownedFibers, ownedScopes);
        }

        Publish(FiberObservationKind.ScopeClosing, FiberScopeState.Closing);
        return closeTask;
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    private async Task CloseCoreAsync(
        IReadOnlyCollection<IOwnedFiber> ownedFibers,
        IReadOnlyCollection<FiberScope> ownedScopes)
    {
        foreach (var fiber in ownedFibers)
        {
            fiber.RequestCancellation();
        }

        await Task.WhenAll(ownedScopes.Select(static scope => scope.CloseAsync())).ConfigureAwait(false);
        await Task.WhenAll(ownedFibers.Select(static fiber => fiber.Completion)).ConfigureAwait(false);

        lock (gate)
        {
            fibers.Clear();
            scopes.Clear();
        }

        Publish(FiberObservationKind.ScopeClosed, FiberScopeState.Closed);
        onClosed?.Invoke(this);
    }

    private void OnCancellationRequested<T>(Fiber<T> fiber)
    {
        Publish(
            FiberObservationKind.FiberCancellationRequested,
            fiberId: fiber.Id,
            fiberName: fiber.Name,
            fiberState: FiberLifecycleState.CancellationRequested);
    }

    private void OnCompleted<T>(Fiber<T> fiber, Outcome<T> outcome)
    {
        IOwnedFiber[] siblings = [];
        var (outcomeKind, errorCategory) = ToObservationOutcome(outcome, fiber.ErrorCategoryProvider);
        lock (gate)
        {
            fibers.Remove(fiber.Id);
            if (FailurePolicy == ChildFailurePolicy.CancelSiblings && outcome is Outcome<T>.Faulted)
            {
                siblings = fibers.Values.ToArray();
            }
        }

        Publish(
            FiberObservationKind.FiberTerminated,
            fiberId: fiber.Id,
            fiberName: fiber.Name,
            fiberState: FiberLifecycleState.Terminated,
            outcome: outcomeKind,
            errorCategory: errorCategory);

        foreach (var sibling in siblings)
        {
            sibling.RequestCancellation();
        }
    }

    private void OnNestedScopeClosed(FiberScope scope)
    {
        lock (gate)
        {
            scopes.Remove(scope.Id);
        }
    }

    private static (FiberOutcomeKind Outcome, string? ErrorCategory) ToObservationOutcome<T>(
        Outcome<T> outcome,
        Func<Exception, string?>? errorCategoryProvider) => outcome switch
    {
        Outcome<T>.Succeeded => (FiberOutcomeKind.Succeeded, null),
        Outcome<T>.Cancelled => (FiberOutcomeKind.Cancelled, null),
        Outcome<T>.Faulted faulted => (FiberOutcomeKind.Faulted, GetErrorCategory(faulted.Error, errorCategoryProvider)),
        _ => throw new InvalidOperationException("Unknown fiber outcome."),
    };

    private static string? GetErrorCategory(Exception error, Func<Exception, string?>? errorCategoryProvider)
    {
        try
        {
            return FiberScopeOptions.NormalizeName(errorCategoryProvider?.Invoke(error) ?? error.GetType().Name, "error");
        }
        catch
        {
            return "error";
        }
    }

    private void Publish(
        FiberObservationKind kind,
        FiberScopeState? scopeState = null,
        Guid? fiberId = null,
        string? fiberName = null,
        FiberLifecycleState? fiberState = null,
        FiberOutcomeKind? outcome = null,
        string? errorCategory = null)
    {
        var observer = Options.Observer;
        if (observer is null)
        {
            return;
        }

        var observation = new FiberObservation(
            kind,
            DateTimeOffset.UtcNow,
            Id,
            ParentScopeId,
            ParentFiberId,
            Name,
            scopeState,
            fiberId,
            fiberName,
            fiberState,
            outcome,
            errorCategory);
        try
        {
            observer.Observe(observation);
        }
        catch
        {
            // Observation is diagnostic only. An observer must not affect structured concurrency.
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
