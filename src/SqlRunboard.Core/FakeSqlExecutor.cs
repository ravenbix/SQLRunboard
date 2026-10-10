using System.Collections.Concurrent;

namespace SqlRunboard.Core;

/// <summary>What the fake does for one script. The default succeeds at once.</summary>
public sealed record FakeScript
{
    public TimeSpan Duration { get; init; }
    public ScriptStatus Status { get; init; } = ScriptStatus.Succeeded;
    public long? Rows { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<SqlMessage> Messages { get; init; } = [];
}

/// <summary>
/// Plays a <see cref="FakeScript"/> for each script name, and records each call.
/// The duration waits on the <see cref="TimeProvider"/>, so a fake clock controls when a script ends.
/// </summary>
public sealed class FakeSqlExecutor(TimeProvider time) : ISqlExecutor
{
    public ConcurrentDictionary<string, FakeScript> Scripts { get; } = new();
    public ConcurrentQueue<SqlExecution> Calls { get; } = new();

    public async Task<SqlResult> ExecuteAsync(SqlExecution execution, Action<SqlMessage> onMessage, CancellationToken ct)
    {
        Calls.Enqueue(execution);
        var script = Scripts.GetValueOrDefault(execution.ScriptName) ?? new FakeScript();
        foreach (var m in script.Messages) onMessage(m);
        await Task.Delay(script.Duration, time, ct);
        if (script.Error is not null) onMessage(new SqlMessage(SqlMessageKind.Error, script.Error));
        return new SqlResult(script.Status, script.Rows, script.Error);
    }
}
