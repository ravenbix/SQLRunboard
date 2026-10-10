namespace SqlRunboard.Core;

public enum ScriptStatus { Queued, Running, Succeeded, Failed, Cancelled }

public enum SqlMessageKind { Info, RowCount, Error }

public sealed record SqlMessage(SqlMessageKind Kind, string Text);

/// <summary>The target of the batch plus the password from the page. The page never gets this.</summary>
public sealed record SqlConnectionSettings(BatchTarget Target, string? Password);

/// <summary>One attempt of one script: the commands (the GO parts) to send on one new connection.</summary>
public sealed record SqlExecution(string ScriptName, IReadOnlyList<string> Commands, SqlConnectionSettings Connection);

/// <summary>Status is Succeeded or Failed. A cancel throws OperationCanceledException.</summary>
public sealed record SqlResult(ScriptStatus Status, long? Rows, string? Error);

/// <summary>The seam to SQL Server. The real adapter comes with ticket #7; tests use <see cref="FakeSqlExecutor"/>.</summary>
public interface ISqlExecutor
{
    Task<SqlResult> ExecuteAsync(SqlExecution execution, Action<SqlMessage> onMessage, CancellationToken ct);
}
