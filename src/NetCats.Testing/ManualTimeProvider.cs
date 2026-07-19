namespace NetCats.Testing;

/// <summary> A deterministic <see cref="TimeProvider"/> for NetCats tests. </summary>
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

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
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
                next = timers.Where(timer => !timer.IsDisposed && timer.DueTimestamp <= target)
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

    private bool Change(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        Validate(dueTime, nameof(dueTime));
        Validate(period, nameof(period));
        lock (gate)
        {
            if (timer.IsDisposed)
            {
                return false;
            }

            timers.Remove(timer);
            timer.Period = period;
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                timer.DueTimestamp = long.MaxValue;
            }
            else
            {
                timer.DueTimestamp = checked(timestamp + dueTime.Ticks);
                timer.Sequence = ManualTimer.NextSequence();
                timers.Add(timer);
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

    private void MoveClock(long nextTimestamp)
    {
        utcNow = utcNow.AddTicks(nextTimestamp - timestamp);
        timestamp = nextTimestamp;
    }

    private static void Validate(TimeSpan timeout, string parameterName)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
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
