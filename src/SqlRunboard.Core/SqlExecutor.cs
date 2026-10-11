namespace SqlRunboard.Core;

public enum ScriptStatus { Queued, Running, Succeeded, Failed, Cancelled }

public enum SqlMessageKind { Info, RowCount, Error }

public sealed record SqlMessage(SqlMessageKind Kind, string Text);

/// <summary>The target of the batch plus the password from the page. The page never gets this.</summary>
public sealed record SqlConnectionSettings(BatchTarget Target, string? Password);

/// <summary>
/// One attempt of one script: the GO parts to send on one new connection, each part Repeat times, in order.
/// The executor stops at the first failed part, and shows its error line as "line N (part M)": N = part.Line + server line - 1, M = 1-based index.
/// </summary>
public sealed record SqlExecution(string ScriptName, IReadOnlyList<ScriptPart> Parts, SqlConnectionSettings Connection);

/// <summary>Status is Succeeded or Failed. A cancel throws OperationCanceledException. LogonFailed: the server rejected the logon (wrong password).</summary>
public sealed record SqlResult(ScriptStatus Status, long? Rows, string? Error, bool LogonFailed = false);

/// <summary>Test connection: @@SERVERNAME, the database and the login, or the Error. LogonFailed: ask for the password again.</summary>
public sealed record SqlLogon(string? Server, string? Database, string? Login, string? Error = null, bool LogonFailed = false);

/// <summary>The seam to SQL Server: <see cref="SqlServerExecutor"/>; tests use <see cref="FakeSqlExecutor"/>.</summary>
public interface ISqlExecutor
{
    Task<SqlResult> ExecuteAsync(SqlExecution execution, Action<SqlMessage> onMessage, CancellationToken ct);

    /// <summary>Opens a connection to the target and reads who and where it is. It does not throw for a logon or network error.</summary>
    Task<SqlLogon> LogonAsync(SqlConnectionSettings connection, CancellationToken ct);
}
