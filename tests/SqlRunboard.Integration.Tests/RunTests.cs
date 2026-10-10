using SqlRunboard.Core;

/// <summary>The run engine with the real adapter: a logon, then real script files against SQL Server.</summary>
public sealed class RunTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("sqlrunboard-it-").FullName;
    readonly BatchList list = new(new SqlServerExecutor(), TimeProvider.System);
    readonly BatchList.Subscription sub;

    public RunTests() => sub = list.Subscribe();

    async Task<int> NewBatch(int? timeoutSeconds, params (string Path, string Text)[] files)
    {
        foreach (var (path, text) in files) File.WriteAllText(Path.Combine(folder, path), text);
        var connection = TestSql.Settings();
        var id = list.NewBatch().Id;
        list.UpdateSettings(id, new BatchSettings { Folder = folder, TimeoutSeconds = timeoutSeconds, Target = connection.Target });
        var logon = await list.LogonAsync(id, connection.Password, TestContext.Current.CancellationToken);
        Assert.Null(logon!.Error);
        return id;
    }

    async Task<RunState> UntilEnded(int id)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
            if ((await sub.Changes.ReadAsync(cts.Token)).Data is Batch { Run: { Active: false } run } b && b.Id == id) return run;
    }

    static Attempt Last(RunState run, string path) => run.Scripts.Single(s => s.Path == path).Attempts[^1];

    [Fact]
    public async Task A_run_reports_success_the_file_line_of_an_error_and_the_timeout()
    {
        var id = await NewBatch(5,
            ("ok.sql", "CREATE TABLE #t (i int)\nINSERT #t VALUES (1), (2), (3)"),
            ("bad.sql", "PRINT 1\nGO\n\nSELECT 1 / 0\nGO\nPRINT 'never'"),
            ("slow.sql", "WAITFOR DELAY '00:01:00'"));

        list.Run(id);
        var run = await UntilEnded(id);

        Assert.Equal((ScriptStatus.Succeeded, 3L), (Last(run, "ok.sql").Status, Last(run, "ok.sql").Rows!.Value));
        Assert.Equal((ScriptStatus.Failed, "line 4 (part 2): Divide by zero error encountered."), (Last(run, "bad.sql").Status, Last(run, "bad.sql").Error));
        Assert.Equal((ScriptStatus.Failed, "Timeout after 5 s"), (Last(run, "slow.sql").Status, Last(run, "slow.sql").Error));
    }

    [Fact]
    public async Task Cancel_stops_a_running_script()
    {
        var id = await NewBatch(null, ("slow.sql", "WAITFOR DELAY '00:01:00'"));

        list.Run(id);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken); // the script is on the server
        list.Cancel(id, "slow.sql");
        var run = await UntilEnded(id);

        Assert.Equal(ScriptStatus.Cancelled, Last(run, "slow.sql").Status);
    }

    public void Dispose()
    {
        sub.Dispose();
        Directory.Delete(folder, recursive: true);
    }
}
