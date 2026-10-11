namespace SqlRunboard.Core;

/// <summary>One attempt of one script. End is null while it runs.</summary>
public sealed record Attempt(DateTimeOffset Start, DateTimeOffset? End, ScriptStatus Status, long? Rows, string? Error);

/// <summary>One script of a run. Status is the status of the last attempt, or Queued. Error: why a script with 0 attempts did not start.</summary>
public sealed record ScriptRun(string Path, ScriptStatus Status, IReadOnlyList<Attempt> Attempts, string? Error = null);

/// <summary>
/// The current run of a batch, as the page sees it. Scripts are in the start order; Queue has the Queued paths in queue order.
/// Now is the server clock when this state was made, so the page can show the elapsed time of Running scripts.
/// Ended: the target or the file scan changed after the run. Reruns do nothing until the next Run.
/// </summary>
public sealed record RunState(DateTimeOffset Start, DateTimeOffset Now, IReadOnlyList<ScriptRun> Scripts, IReadOnlyList<string> Queue, bool Ended = false)
{
    public bool Active => Scripts.Any(s => s.Status is ScriptStatus.Queued or ScriptStatus.Running);
}

/// <summary>One message of an attempt, for the drawer. Time is the server clock.</summary>
public sealed record RunMessage(DateTimeOffset Time, SqlMessageKind Kind, string Text);

/// <summary>SSE "message": a new message of attempt number Attempt (1-based) of the script Path in the batch Id.</summary>
public sealed record ScriptMessage(int Id, string Path, int Attempt, DateTimeOffset Time, SqlMessageKind Kind, string Text);

/// <summary>The kept messages of one attempt: the last <see cref="BatchList.MaxMessages"/>. Earlier = the older ones, only in run.log.</summary>
public sealed record AttemptMessages(int Id, string Path, int Attempt, int Earlier, IReadOnlyList<RunMessage> Messages);
