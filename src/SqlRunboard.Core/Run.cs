namespace SqlRunboard.Core;

/// <summary>One attempt of one script. End is null while it runs.</summary>
public sealed record Attempt(DateTimeOffset Start, DateTimeOffset? End, ScriptStatus Status, long? Rows, string? Error);

/// <summary>One script of a run. Status is the status of the last attempt, or Queued. Error: why a script with 0 attempts did not start.</summary>
public sealed record ScriptRun(string Path, ScriptStatus Status, IReadOnlyList<Attempt> Attempts, string? Error = null);

/// <summary>
/// The current run of a batch, as the page sees it. Scripts are in the start order; Queue has the Queued paths in queue order.
/// Now is the server clock when this state was made, so the page can show the elapsed time of Running scripts.
/// </summary>
public sealed record RunState(DateTimeOffset Start, DateTimeOffset Now, IReadOnlyList<ScriptRun> Scripts, IReadOnlyList<string> Queue)
{
    public bool Active => Scripts.Any(s => s.Status is ScriptStatus.Queued or ScriptStatus.Running);
}
