using Microsoft.Data.SqlClient;

namespace SqlRunboard.Core;

/// <summary>The real <see cref="ISqlExecutor"/>: Microsoft.Data.SqlClient. See docs/SPEC.md "Execution".</summary>
public sealed class SqlServerExecutor : ISqlExecutor
{
    /// <summary>
    /// The additional text first, then the target settings over it. Pooling is always off, and the application name is
    /// "SQLRunboard" plus the script name (null for Test connection).
    /// </summary>
    public static string ConnectionString(SqlConnectionSettings connection, string? scriptName)
    {
        var t = connection.Target;
        var b = new SqlConnectionStringBuilder(t.ExtraConnectionString)
        {
            Encrypt = t.Encrypt switch { Encrypt.Optional => SqlConnectionEncryptOption.Optional, Encrypt.Strict => SqlConnectionEncryptOption.Strict, _ => SqlConnectionEncryptOption.Mandatory },
            TrustServerCertificate = t.TrustServerCertificate,
            Pooling = false,
            ApplicationName = scriptName is null ? "SQLRunboard" : ("SQLRunboard " + scriptName)[..Math.Min(128, 12 + scriptName.Length)],
        };
        if (t.Server is not null) b.DataSource = t.Server;
        if (t.Database is not null) b.InitialCatalog = t.Database;
        switch (t.Auth)
        {
            case Auth.Integrated: b.IntegratedSecurity = true; break;
            case Auth.SqlServer: b.UserID = t.User ?? ""; b.Password = connection.Password ?? ""; break;
            default: throw new NotSupportedException("Microsoft Entra ID logon comes in a later version. Use Integrated or SQL Server.");
        }
        return b.ConnectionString;
    }

    /// <summary>
    /// One new connection for the script. The parts run in order, each Repeat times, with no transaction from the app.
    /// Result sets are discarded; each statement's row count comes as a message. The first failed part stops the script.
    /// The engine owns the timeout: a cancel of the token always ends as a cancelled task, never as a SQL error.
    /// </summary>
    public async Task<SqlResult> ExecuteAsync(SqlExecution execution, Action<SqlMessage> onMessage, CancellationToken ct)
    {
        try
        {
            await using var connection = new SqlConnection(ConnectionString(execution.Connection, execution.ScriptName));
            connection.InfoMessage += (_, e) => { foreach (SqlError info in e.Errors) onMessage(new(SqlMessageKind.Info, info.Message)); };
            try { await connection.OpenAsync(ct); }
            catch (SqlException e) when (!ct.IsCancellationRequested)
            {
                onMessage(new(SqlMessageKind.Error, e.Message));
                return new(ScriptStatus.Failed, null, e.Message, IsLogonFailure(e));
            }
            long? rows = null;
            for (var i = 0; i < execution.Parts.Count; i++)
            {
                var part = execution.Parts[i];
                for (var n = 0; n < part.Repeat; n++)
                {
                    await using var command = new SqlCommand(part.Text, connection) { CommandTimeout = 0 };
                    command.StatementCompleted += (_, e) =>
                    {
                        rows = (rows ?? 0) + e.RecordCount;
                        onMessage(new(SqlMessageKind.RowCount, e.RecordCount == 1 ? "1 row affected" : $"{e.RecordCount} rows affected"));
                    };
                    try { await command.ExecuteNonQueryAsync(ct); }
                    catch (SqlException e) when (!ct.IsCancellationRequested)
                    {
                        string? first = null;
                        foreach (SqlError error in e.Errors)
                        {
                            if (error.Class <= 10) { onMessage(new(SqlMessageKind.Info, error.Message)); continue; }
                            // The server counts lines in the part; the file line adds the line where the part starts.
                            var text = $"line {part.Line + Math.Max(error.LineNumber, 1) - 1} (part {i + 1}): {error.Message}";
                            first ??= text;
                            onMessage(new(SqlMessageKind.Error, text));
                        }
                        return new(ScriptStatus.Failed, rows, first ?? e.Message);
                    }
                }
            }
            return new(ScriptStatus.Succeeded, rows, null);
        }
        catch (Exception e) when (e is not OperationCanceledException && ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct); // SqlClient reports a cancel as a SqlException
        }
    }

    public async Task<SqlLogon> LogonAsync(SqlConnectionSettings connection, CancellationToken ct)
    {
        try
        {
            await using var c = new SqlConnection(ConnectionString(connection, scriptName: null));
            await c.OpenAsync(ct);
            await using var command = new SqlCommand("SELECT @@SERVERNAME, DB_NAME(), SUSER_SNAME()", c);
            await using var r = await command.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return new(r.IsDBNull(0) ? null : r.GetString(0), r.GetString(1), r.GetString(2));
        }
        catch (SqlException e) { return new(null, null, null, e.Message, IsLogonFailure(e)); }
        catch (Exception e) when (e is not OperationCanceledException) { return new(null, null, null, e.Message); }
    }

    /// <summary>18456: "Login failed for user". Another password can help; a network or database error cannot.</summary>
    static bool IsLogonFailure(SqlException e) => e.Number == 18456;
}
