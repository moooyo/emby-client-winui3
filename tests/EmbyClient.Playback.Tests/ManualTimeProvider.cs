namespace EmbyClient.Playback.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ManualTimer> timers = [];
    private long elapsedTicks;

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate) return DateTimeOffset.UnixEpoch.AddTicks(elapsedTicks);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (gate) timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan amount)
    {
        List<ManualTimer> due;
        lock (gate)
        {
            elapsedTicks += amount.Ticks;
            due = timers.Where(timer => timer.DueTicks <= elapsedTicks).ToList();
            foreach (var timer in due)
                timer.DueTicks = timer.PeriodTicks == long.MaxValue ? long.MaxValue : elapsedTicks + timer.PeriodTicks;
        }
        foreach (var timer in due) timer.Invoke();
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private bool disposed;
        internal long DueTicks { get; set; } = long.MaxValue;
        internal long PeriodTicks { get; private set; } = long.MaxValue;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                if (disposed) return false;
                DueTicks = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.elapsedTicks + dueTime.Ticks;
                PeriodTicks = period == Timeout.InfiniteTimeSpan ? long.MaxValue : period.Ticks;
                return true;
            }
        }

        internal void Invoke()
        {
            lock (owner.gate)
            {
                if (disposed) return;
            }
            callback(state);
        }

        public void Dispose()
        {
            lock (owner.gate)
            {
                disposed = true;
                owner.timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
