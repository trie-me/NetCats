using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NetCats.Runtime;

namespace NetCats.AspNetCore;

public sealed class FiberDiagnosticsOptions
{
    public TimeSpan CompletedRetention { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan MaximumPublishRate { get; set; } = TimeSpan.FromMilliseconds(100);

    public int MaximumNodes { get; set; } = 2_000;

    /// <summary>Enables mapping outside Development and Demo. Pair with <see cref="AuthorizationPolicy"/> in production.</summary>
    public bool EnableInProduction { get; set; }

    public string? AuthorizationPolicy { get; set; }

    internal void Validate()
    {
        if (CompletedRetention < TimeSpan.Zero)
        {
            throw new OptionsValidationException(nameof(CompletedRetention), typeof(FiberDiagnosticsOptions), ["Completed retention cannot be negative."]);
        }

        if (MaximumPublishRate <= TimeSpan.Zero)
        {
            throw new OptionsValidationException(nameof(MaximumPublishRate), typeof(FiberDiagnosticsOptions), ["Maximum publish rate must be positive."]);
        }

        if (MaximumNodes <= 0)
        {
            throw new OptionsValidationException(nameof(MaximumNodes), typeof(FiberDiagnosticsOptions), ["Maximum nodes must be positive."]);
        }
    }
}

public sealed record FiberTreeSnapshot(
    long Version,
    DateTimeOffset ObservedAt,
    IReadOnlyList<FiberScopeNode> Roots,
    bool IsTruncated);

public sealed record FiberScopeNode(
    Guid ScopeId,
    Guid? ParentScopeId,
    Guid? ParentFiberId,
    string Name,
    FiberScopeState State,
    IReadOnlyList<FiberScopeNode> Scopes,
    IReadOnlyList<FiberNode> Fibers);

public sealed record FiberNode(
    Guid FiberId,
    string Name,
    FiberLifecycleState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    FiberOutcomeKind? Outcome,
    string? ErrorCategory);

/// <summary>
/// A bounded process-local projection. It owns no fibers and receives no effect values or exceptions.
/// </summary>
public sealed class FiberDiagnosticsRegistry : IFiberObserver
{
    private readonly object gate = new();
    private readonly FiberDiagnosticsOptions options;
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<Guid, ScopeEntry> scopes = [];
    private readonly Dictionary<Guid, FiberEntry> fibers = [];
    private TaskCompletionSource<long> changed = NewChangeSource();
    private long version;

    public FiberDiagnosticsRegistry(IOptions<FiberDiagnosticsOptions> options, TimeProvider timeProvider)
    {
        this.options = options.Value;
        this.options.Validate();
        this.timeProvider = timeProvider;
    }

    public void Observe(FiberObservation observation)
    {
        lock (gate)
        {
            Apply(observation);
            Prune(observation.ObservedAt);
            version = checked(version + 1);
            var previous = changed;
            changed = NewChangeSource();
            previous.TrySetResult(version);
        }
    }

    public FiberTreeSnapshot GetSnapshot()
    {
        lock (gate)
        {
            var now = timeProvider.GetUtcNow();
            var changedByPrune = Prune(now);
            if (changedByPrune)
            {
                version = checked(version + 1);
                var previous = changed;
                changed = NewChangeSource();
                previous.TrySetResult(version);
            }

            return BuildSnapshot(now);
        }
    }

    public Task WaitForChangeAsync(long observedVersion, CancellationToken cancellationToken)
    {
        Task wait;
        lock (gate)
        {
            if (version != observedVersion)
            {
                return Task.CompletedTask;
            }

            wait = changed.Task;
        }

        return wait.WaitAsync(cancellationToken);
    }

    private void Apply(FiberObservation observation)
    {
        switch (observation.Kind)
        {
            case FiberObservationKind.ScopeOpened:
                scopes[observation.ScopeId] = new ScopeEntry(
                    observation.ScopeId,
                    observation.ParentScopeId,
                    observation.ParentFiberId,
                    observation.ScopeName,
                    FiberScopeState.Open,
                    observation.ObservedAt,
                    null);
                break;
            case FiberObservationKind.ScopeClosing:
            case FiberObservationKind.ScopeClosed:
                if (scopes.TryGetValue(observation.ScopeId, out var scope))
                {
                    scopes[observation.ScopeId] = scope with
                    {
                        State = observation.ScopeState ?? scope.State,
                        CompletedAt = observation.Kind == FiberObservationKind.ScopeClosed ? observation.ObservedAt : scope.CompletedAt,
                    };
                }

                break;
            case FiberObservationKind.FiberStarted:
                if (observation.FiberId is Guid startedFiberId && observation.FiberName is not null)
                {
                    fibers[startedFiberId] = new FiberEntry(
                        startedFiberId,
                        observation.ScopeId,
                        observation.FiberName,
                        FiberLifecycleState.Running,
                        observation.ObservedAt,
                        null,
                        null,
                        null);
                }

                break;
            case FiberObservationKind.FiberCancellationRequested:
                UpdateFiber(observation, static (fiber, value) => fiber with { State = value.FiberState ?? fiber.State });
                break;
            case FiberObservationKind.FiberTerminated:
                UpdateFiber(observation, static (fiber, value) => fiber with
                {
                    State = value.FiberState ?? fiber.State,
                    CompletedAt = value.ObservedAt,
                    Outcome = value.Outcome,
                    ErrorCategory = value.ErrorCategory,
                });
                break;
            default:
                throw new InvalidOperationException("Unknown fiber observation.");
        }
    }

    private void UpdateFiber(
        FiberObservation observation,
        Func<FiberEntry, FiberObservation, FiberEntry> update)
    {
        if (observation.FiberId is Guid fiberId && fibers.TryGetValue(fiberId, out var fiber))
        {
            fibers[fiberId] = update(fiber, observation);
        }
    }

    private bool Prune(DateTimeOffset now)
    {
        var changedState = false;
        var expiredFibers = fibers.Values
            .Where(fiber => fiber.CompletedAt is DateTimeOffset completed && completed + options.CompletedRetention <= now)
            .Select(static fiber => fiber.Id)
            .ToArray();
        foreach (var id in expiredFibers)
        {
            fibers.Remove(id);
            changedState = true;
        }

        var expiredScopes = scopes.Values
            .Where(scope => scope.CompletedAt is DateTimeOffset completed && completed + options.CompletedRetention <= now)
            .OrderBy(static scope => scope.CompletedAt)
            .Select(static scope => scope.Id)
            .ToArray();
        foreach (var id in expiredScopes)
        {
            scopes.Remove(id);
            changedState = true;
        }

        return changedState;
    }

    private FiberTreeSnapshot BuildSnapshot(DateTimeOffset now)
    {
        var activeScopeIds = scopes.Values
            .Where(static scope => scope.State is not FiberScopeState.Closed)
            .Select(static scope => scope.Id)
            .ToHashSet();
        var activeFibers = fibers.Values.Where(static fiber => fiber.CompletedAt is null).OrderByDescending(static fiber => fiber.StartedAt).ToList();
        var completedFibers = fibers.Values.Where(static fiber => fiber.CompletedAt is not null).OrderByDescending(static fiber => fiber.CompletedAt).ToList();
        var includedFiberIds = new HashSet<Guid>();
        var remaining = options.MaximumNodes - activeScopeIds.Count;
        var truncated = remaining < activeFibers.Count;
        foreach (var fiber in activeFibers.Concat(completedFibers).Take(Math.Max(remaining, 0)))
        {
            includedFiberIds.Add(fiber.Id);
        }

        if (includedFiberIds.Count < activeFibers.Count + completedFibers.Count)
        {
            truncated = true;
        }

        var displayedScopeIds = scopes.Values
            .OrderByDescending(static scope => scope.State is not FiberScopeState.Closed)
            .ThenByDescending(static scope => scope.OpenedAt)
            .Take(Math.Max(options.MaximumNodes - includedFiberIds.Count, 0))
            .Select(static scope => scope.Id)
            .ToHashSet();
        if (displayedScopeIds.Count < scopes.Count)
        {
            truncated = true;
        }

        var children = displayedScopeIds.ToDictionary(static id => id, static _ => new List<Guid>());
        var roots = new List<Guid>();
        foreach (var scope in scopes.Values.Where(scope => displayedScopeIds.Contains(scope.Id)))
        {
            if (scope.ParentScopeId is Guid parentId && children.TryGetValue(parentId, out var parentChildren))
            {
                parentChildren.Add(scope.Id);
            }
            else
            {
                roots.Add(scope.Id);
            }
        }

        var fibersByScope = includedFiberIds
            .Select(id => fibers[id])
            .Where(fiber => displayedScopeIds.Contains(fiber.ScopeId))
            .GroupBy(static fiber => fiber.ScopeId)
            .ToDictionary(static group => group.Key, static group => group.OrderBy(static fiber => fiber.StartedAt).ToArray());
        FiberScopeNode CreateNode(Guid id)
        {
            var scope = scopes[id];
            var nested = children[id].OrderBy(child => scopes[child].OpenedAt).Select(CreateNode).ToArray();
            var ownedFibers = fibersByScope.TryGetValue(id, out var owned)
                ? owned.Select(static fiber => new FiberNode(
                    fiber.Id,
                    fiber.Name,
                    fiber.State,
                    fiber.StartedAt,
                    fiber.CompletedAt,
                    fiber.Outcome,
                    fiber.ErrorCategory)).ToArray()
                : [];
            return new FiberScopeNode(scope.Id, scope.ParentScopeId, scope.ParentFiberId, scope.Name, scope.State, nested, ownedFibers);
        }

        return new FiberTreeSnapshot(
            version,
            now,
            roots.OrderBy(id => scopes[id].OpenedAt).Select(CreateNode).ToArray(),
            truncated);
    }

    private static TaskCompletionSource<long> NewChangeSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record ScopeEntry(
        Guid Id,
        Guid? ParentScopeId,
        Guid? ParentFiberId,
        string Name,
        FiberScopeState State,
        DateTimeOffset OpenedAt,
        DateTimeOffset? CompletedAt);

    private sealed record FiberEntry(
        Guid Id,
        Guid ScopeId,
        string Name,
        FiberLifecycleState State,
        DateTimeOffset StartedAt,
        DateTimeOffset? CompletedAt,
        FiberOutcomeKind? Outcome,
        string? ErrorCategory);
}

public static class FiberDiagnosticsServiceCollectionExtensions
{
    public static IServiceCollection AddNetCatsFiberDiagnostics(
        this IServiceCollection services,
        Action<FiberDiagnosticsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<FiberDiagnosticsOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.AddSingleton<FiberDiagnosticsRegistry>();
        services.AddSingleton<IFiberObserver>(static provider => provider.GetRequiredService<FiberDiagnosticsRegistry>());
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}

public static class FiberDiagnosticsEndpointRouteBuilderExtensions
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static IEndpointRouteBuilder MapNetCatsFiberDiagnostics(this IEndpointRouteBuilder endpoints, string pattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        var environment = endpoints.ServiceProvider.GetRequiredService<IHostEnvironment>();
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<FiberDiagnosticsOptions>>().Value;
        options.Validate();
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Demo") && !options.EnableInProduction)
        {
            return endpoints;
        }

        var group = endpoints.MapGroup(pattern);
        var snapshot = group.MapGet("/snapshot", GetSnapshot);
        var stream = group.MapGet("/stream", StreamSnapshots);
        if (!String.IsNullOrWhiteSpace(options.AuthorizationPolicy))
        {
            snapshot.RequireAuthorization(options.AuthorizationPolicy);
            stream.RequireAuthorization(options.AuthorizationPolicy);
        }

        return endpoints;
    }

    private static IResult GetSnapshot(FiberDiagnosticsRegistry registry) => Results.Json(registry.GetSnapshot(), SerializerOptions);

    internal static async Task StreamSnapshots(
        HttpContext context,
        FiberDiagnosticsRegistry registry,
        IOptions<FiberDiagnosticsOptions> options,
        TimeProvider timeProvider,
        IHostApplicationLifetime applicationLifetime)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted,
            applicationLifetime.ApplicationStopping);
        var cancellationToken = stopping.Token;
        var version = -1L;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var snapshot = registry.GetSnapshot();
                if (snapshot.Version != version)
                {
                    var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
                    await context.Response.WriteAsync($"event: snapshot\ndata: {json}\n\n", cancellationToken).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                    version = snapshot.Version;
                }

                await Task.Delay(options.Value.MaximumPublishRate, timeProvider, cancellationToken).ConfigureAwait(false);
                await registry.WaitForChangeAsync(version, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The browser closed the overlay or the host is stopping.
        }
    }
}
