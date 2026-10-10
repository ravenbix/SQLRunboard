using Microsoft.Data.SqlClient;
using SqlRunboard.Core;

/// <summary>The real adapter against the SQL Server in SQLRUNBOARD_TEST_SQL.</summary>
public class ExecutorTests
{
    readonly SqlServerExecutor executor = new();
    readonly List<SqlMessage> messages = [];

    Task<SqlResult> Run(string text, string name = "test.sql", CancellationToken? cancel = null) =>
        executor.ExecuteAsync(new SqlExecution(name, SqlScript.Split(text), TestSql.Settings()), m => { lock (messages) messages.Add(m); },
            cancel ?? TestContext.Current.CancellationToken);

    string[] Texts(SqlMessageKind kind) => [.. messages.Where(m => m.Kind == kind).Select(m => m.Text)];

    [Fact]
    public async Task A_script_reports_PRINT_RAISERROR_and_rows_affected_as_messages()
    {
        var r = await Run("""
            PRINT 'hello'
            CREATE TABLE #t (i int)
            INSERT #t VALUES (1), (2)
            RAISERROR('just info', 10, 1)
            GO
            UPDATE #t SET i = 3
            SELECT * FROM #t
            """);

        Assert.Equal(ScriptStatus.Succeeded, r.Status);
        Assert.Null(r.Error);
        Assert.Equal(6, r.Rows); // 2 inserted + 2 updated + 2 selected (and discarded)
        Assert.Equal(["hello", "just info"], Texts(SqlMessageKind.Info));
        Assert.Equal(["2 rows affected", "2 rows affected", "2 rows affected"], Texts(SqlMessageKind.RowCount));
    }

    [Fact]
    public async Task A_failed_script_stops_at_its_first_failed_GO_part_and_shows_the_file_line()
    {
        var r = await Run("""
            PRINT 'one'
            GO
            PRINT 'two'

            SELECT * FROM dbo.NoSuchTable
            GO
            PRINT 'three'
            """);

        Assert.Equal(ScriptStatus.Failed, r.Status);
        Assert.Equal("line 5 (part 2): Invalid object name 'dbo.NoSuchTable'.", r.Error);
        Assert.Equal(["one", "two"], Texts(SqlMessageKind.Info));
        Assert.Equal([r.Error], Texts(SqlMessageKind.Error));
    }

    [Fact]
    public async Task RAISERROR_with_severity_16_fails_the_script()
    {
        var r = await Run("\n\nRAISERROR('bad data', 16, 1)\nPRINT 'after'\nGO\nPRINT 'next part'");

        Assert.Equal(ScriptStatus.Failed, r.Status);
        Assert.Equal("line 3 (part 1): bad data", r.Error);
        Assert.DoesNotContain("next part", Texts(SqlMessageKind.Info));
    }

    [Fact]
    public async Task GO_n_runs_the_part_n_times_on_one_connection()
    {
        var r = await Run("""
            CREATE TABLE #t (i int)
            GO
            INSERT #t VALUES (1)
            GO 3
            IF (SELECT COUNT(*) FROM #t) <> 3 RAISERROR('wrong count', 16, 1)
            """);

        Assert.Equal(ScriptStatus.Succeeded, r.Status);
        Assert.Equal(3, r.Rows);
    }

    [Fact]
    public async Task Each_script_has_its_own_application_name_no_transaction_and_its_connection_closes_at_the_end()
    {
        var name = $"app-{Guid.NewGuid():N}.sql";
        var r = await Run("PRINT APP_NAME()\nPRINT @@TRANCOUNT", name);

        Assert.Equal(ScriptStatus.Succeeded, r.Status);
        Assert.Equal(["SQLRunboard " + name, "0"], Texts(SqlMessageKind.Info));
        await using var c = new SqlConnection(TestSql.ConnectionString);
        await c.OpenAsync(TestContext.Current.CancellationToken);
        await using var q = new SqlCommand("SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name = @p", c);
        q.Parameters.AddWithValue("@p", "SQLRunboard " + name);
        Assert.Equal(0, (int)(await q.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task A_cancel_ends_the_script_with_a_cancelled_task()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var started = DateTime.UtcNow;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run("WAITFOR DELAY '00:01:00'", cancel: cts.Token));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task A_wrong_password_is_a_logon_failure()
    {
        var settings = TestSql.Settings() with { Password = "wrong" };

        var r = await executor.ExecuteAsync(new SqlExecution("a.sql", SqlScript.Split("PRINT 1"), settings), messages.Add, TestContext.Current.CancellationToken);
        var logon = await executor.LogonAsync(settings, TestContext.Current.CancellationToken);

        Assert.Equal(ScriptStatus.Failed, r.Status);
        Assert.True(r.LogonFailed);
        Assert.StartsWith("Login failed for user", r.Error);
        Assert.True(logon.LogonFailed);
        Assert.StartsWith("Login failed for user", logon.Error);
    }

    [Fact]
    public async Task Test_connection_shows_the_server_name_the_database_and_the_login()
    {
        var target = TestSql.Settings();

        var logon = await executor.LogonAsync(target, TestContext.Current.CancellationToken);

        Assert.Null(logon.Error);
        Assert.False(string.IsNullOrEmpty(logon.Server));
        Assert.Equal(target.Target.Database, logon.Database);
        Assert.Equal(target.Target.User, logon.Login);
    }
}
