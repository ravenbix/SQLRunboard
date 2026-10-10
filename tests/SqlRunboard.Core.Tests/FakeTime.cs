/// <summary>A manual clock for tests. Time moves only when you call <see cref="Advance"/>. Timers (Task.Delay, CancelAfter) fire in due order.</summary>
public sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    readonly Lock gate = new();
    readonly List<FakeTimer> timers = [];
    DateTimeOffset now = start;

    public FakeTime() : this(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero)) { }

    public override DateTimeOffset GetUtcNow() { lock (gate) return now; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public void Advance(TimeSpan delta)
    {
        DateTimeOffset end;
        lock (gate) end = now + delta;
        while (true)
        {
            FakeTimer? next;
            lock (gate)
            {
                next = timers.Where(t => t.Due <= end).MinBy(t => t.Due);
                if (next is null) { now = end; return; }
                now = next.Due;
                next.Reschedule(now);
            }
            next.Fire(); // outside the lock: the callback can create or change timers
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new FakeTimer(this, callback, state);
        t.Change(dueTime, period);
        return t;
    }

    sealed class FakeTimer(FakeTime clock, TimerCallback callback, object? state) : ITimer
    {
        TimeSpan period;
        public DateTimeOffset Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock.gate)
            {
                clock.timers.Remove(this);
                this.period = period;
                if (dueTime == Timeout.InfiniteTimeSpan) return true;
                Due = clock.now + dueTime;
                clock.timers.Add(this);
            }
            return true;
        }

        // Called under the clock lock when the timer fires.
        public void Reschedule(DateTimeOffset now)
        {
            if (period == Timeout.InfiniteTimeSpan || period <= TimeSpan.Zero) clock.timers.Remove(this);
            else Due = now + period;
        }

        public void Fire() => callback(state);
        public void Dispose() { lock (clock.gate) clock.timers.Remove(this); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
