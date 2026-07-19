namespace NetCats.Poc06.Scheduling;

public sealed class LogicalExecutionTree
{
    public LogicalExecutionTree(TimeProvider? timeProvider = null, int operationBudget = 512)
    {
        if (operationBudget <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(operationBudget));
        }

        TimeProvider = timeProvider ?? TimeProvider.System;
        OperationBudget = operationBudget;
    }

    public TimeProvider TimeProvider { get; }

    public int OperationBudget { get; }

    public LogicalRuntime CreateRuntime() => new(this);

    public async Task RunConcurrentlyAsync(params Func<LogicalRuntime, Task>[] operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        await Task.WhenAll(operations.Select(operation => operation(CreateRuntime()))).ConfigureAwait(false);
    }
}

public sealed class LogicalRuntime
{
    private readonly LogicalExecutionTree tree;
    private int remaining;
    private int yieldCount;

    internal LogicalRuntime(LogicalExecutionTree tree)
    {
        this.tree = tree;
        remaining = tree.OperationBudget;
    }

    public int YieldCount => Volatile.Read(ref yieldCount);

    public TimeProvider TimeProvider => tree.TimeProvider;

    public ValueTask CheckpointAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (--remaining != 0)
        {
            return ValueTask.CompletedTask;
        }

        remaining = tree.OperationBudget;
        Interlocked.Increment(ref yieldCount);
        return YieldAsync(cancellationToken);
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
        Task.Delay(delay, tree.TimeProvider, cancellationToken);

    private static async ValueTask YieldAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
    }
}

public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset utcNow;
    private long timestamp;

    public ManualTimeProvider(DateTimeOffset? start = null)
    {
        utcNow = start ?? DateTimeOffset.UnixEpoch;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return utcNow;
        }
    }

    public override long GetTimestamp()
    {
        lock (gate)
        {
            return timestamp;
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ValidateTimeout(dueTime, nameof(dueTime));
        ValidateTimeout(period, nameof(period));
        var timer = new ManualTimer(this, callback, state);
        Change(timer, dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        long target;
        lock (gate)
        {
            target = checked(timestamp + amount.Ticks);
        }

        while (true)
        {
            ManualTimer? next;
            lock (gate)
            {
                next = timers
                    .Where(timer => !timer.IsDisposed && timer.DueTimestamp <= target)
                    .OrderBy(timer => timer.DueTimestamp)
                    .ThenBy(timer => timer.Sequence)
                    .FirstOrDefault();
                if (next is null)
                {
                    MoveClock(target);
                    return;
                }

                MoveClock(next.DueTimestamp);
                if (next.Period == Timeout.InfiniteTimeSpan)
                {
                    timers.Remove(next);
                    next.DueTimestamp = long.MaxValue;
                }
                else
                {
                    next.DueTimestamp = checked(next.DueTimestamp + next.Period.Ticks);
                }
            }

            next.Invoke();
        }
    }

    private void MoveClock(long target)
    {
        var delta = target - timestamp;
        timestamp = target;
        utcNow = utcNow.AddTicks(delta);
    }

    private bool Change(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        ValidateTimeout(dueTime, nameof(dueTime));
        ValidateTimeout(period, nameof(period));
        lock (gate)
        {
            if (timer.IsDisposed)
            {
                return false;
            }

            timers.Remove(timer);
            timer.Period = period;
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                timer.DueTimestamp = checked(timestamp + dueTime.Ticks);
                timer.Sequence = ManualTimer.NextSequence();
                timers.Add(timer);
            }
            else
            {
                timer.DueTimestamp = long.MaxValue;
            }

            return true;
        }
    }

    private void Dispose(ManualTimer timer)
    {
        lock (gate)
        {
            timer.MarkDisposed();
            timers.Remove(timer);
        }
    }

    private static void ValidateTimeout(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private static long sequenceSource;
        private readonly ManualTimeProvider owner;
        private readonly TimerCallback callback;
        private readonly object? state;
        private int disposed;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            this.owner = owner;
            this.callback = callback;
            this.state = state;
        }

        public long DueTimestamp { get; set; }

        public TimeSpan Period { get; set; }

        public long Sequence { get; set; }

        public bool IsDisposed => Volatile.Read(ref disposed) != 0;

        public static long NextSequence() => Interlocked.Increment(ref sequenceSource);

        public bool Change(TimeSpan dueTime, TimeSpan period) => owner.Change(this, dueTime, period);

        public void Dispose() => owner.Dispose(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Invoke()
        {
            if (!IsDisposed)
            {
                callback(state);
            }
        }

        public void MarkDisposed() => Interlocked.Exchange(ref disposed, 1);
    }
}

public interface IRxSchedulerProjection
{
    DateTimeOffset Now { get; }

    IDisposable Schedule(TimeSpan dueTime, Action action);
}

public sealed class TimeProviderSchedulerProjection(TimeProvider timeProvider) : IRxSchedulerProjection
{
    public DateTimeOffset Now => timeProvider.GetUtcNow();

    public IDisposable Schedule(TimeSpan dueTime, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ITimer? timer = null;
        timer = timeProvider.CreateTimer(
            _ =>
            {
                action();
                timer?.Dispose();
            },
            null,
            dueTime,
            Timeout.InfiniteTimeSpan);
        return timer;
    }
}
