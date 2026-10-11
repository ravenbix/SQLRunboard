using SqlRunboard.Core;

/// <summary>The log folder of a run: run.log and summary.csv. Seen through BatchList and the files on disk. FakeTime local zone is +02:00.</summary>
public sealed class RunLogTests : IAsyncDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("sqlrunboard-log-").FullName;
    readonly string appData;
    readonly FakeTime time = new();
    readonly FakeSqlExecutor executor;
    readonly BatchList list;
    readonly BatchList.Subscription sub;

    public RunLogTests()
    {
        appData = Directory.CreateDirectory(Path.Combine(root, "appdata")).FullName;
        executor = new FakeSqlExecutor(time);
        list = new BatchList(executor, time, appData);
        sub = list.Subscribe();
    }

    /// <summary>Stops the runs first: a late script end writes its log in root.</summary>
    public async ValueTask DisposeAsync()
    {
        sub.Dispose();
        await list.StopAsync();
        Directory.Delete(root, recursive: true);
    }

    string Scripts => Directory.CreateDirectory(Path.Combine(root, "scripts")).FullName;

    void Script(string path, int seconds, ScriptStatus status = ScriptStatus.Succeeded, long? rows = null, string? error = null, params string[] messages) =>
        executor.Scripts[path] = new FakeScript
        {
            Duration = TimeSpan.FromSeconds(seconds), Status = status, Rows = rows, Error = error,
            Messages = [.. messages.Select(m => new SqlMessage(SqlMessageKind.Info, m))],
        };

    Batch NewBatch(BatchSettings settings, params string[] files)
    {
        foreach (var f in files) File.WriteAllText(Path.Combine(Scripts, f), "select 1");
        var b = list.NewBatch();
        return list.UpdateSettings(b.Id, settings with { Folder = Scripts })!;
    }

    async Task<RunState> Until(int id, Func<RunState, bool> ok)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var change = await sub.Changes.ReadAsync(cts.Token);
            if (change.Data is Batch { Run: { } run } b && b.Id == id && ok(run)) return run;
        }
    }

    static bool Ended(RunState r) => !r.Active;

    [Fact]
    public async Task An_unsaved_batch_writes_summary_csv_in_the_app_data_folder_with_local_times()
    {
        var target = new BatchTarget { Server = "sql01", Database = "Sales" };
        var b = NewBatch(new BatchSettings { ParallelLimit = 1, StopOnFirstFailure = true, Target = target }, "1.sql", "2.sql", "3.sql");
        Script("1.sql", 12, rows: 5);
        Script("2.sql", 3, ScriptStatus.Failed, error: "line 4 (part 1): Invalid object name \"x, y\"");

        list.Run(b.Id);
        await Until(b.Id, r => r.Scripts[0].Status == ScriptStatus.Running);
        time.Advance(TimeSpan.FromSeconds(12));
        await Until(b.Id, r => r.Scripts[1].Status == ScriptStatus.Running);
        time.Advance(TimeSpan.FromSeconds(3));
        await Until(b.Id, Ended);

        var folder = Path.Combine(appData, "logs", "Untitled 1_20261009-140000");
        Assert.Equal(
            """"
            script,attempt,status,start,end,duration,rows,error,server,database
            1.sql,1,Succeeded,2026-10-09T14:00:00+02:00,2026-10-09T14:00:12+02:00,12.000,5,,sql01,Sales
            2.sql,1,Failed,2026-10-09T14:00:12+02:00,2026-10-09T14:00:15+02:00,3.000,,"line 4 (part 1): Invalid object name ""x, y""",sql01,Sales
            3.sql,0,Cancelled,,,,,Not started: the queue stopped,sql01,Sales

            """".ReplaceLineEndings("\r\n"),
            File.ReadAllText(Path.Combine(folder, "summary.csv")));
    }

    [Fact]
    public async Task Run_log_has_all_messages_and_a_summary_at_the_end()
    {
        var b = NewBatch(new BatchSettings { ParallelLimit = 1 }, "1.sql", "2.sql");
        Script("1.sql", 5, rows: 5, messages: "Changed database context to 'Sales'.");
        Script("2.sql", 2, ScriptStatus.Failed, error: "line 3 (part 1): boom");

        list.Run(b.Id);
        await Until(b.Id, r => r.Scripts[0].Status == ScriptStatus.Running);
        time.Advance(TimeSpan.FromSeconds(5));
        await Until(b.Id, r => r.Scripts[1].Status == ScriptStatus.Running);
        time.Advance(TimeSpan.FromSeconds(2));
        await Until(b.Id, Ended);

        Assert.Equal(
            """
            2026-10-09T14:00:00+02:00 1.sql #1: Started
            2026-10-09T14:00:00+02:00 1.sql #1: Changed database context to 'Sales'.
            2026-10-09T14:00:05+02:00 1.sql #1: Succeeded, 5 rows
            2026-10-09T14:00:05+02:00 2.sql #1: Started
            2026-10-09T14:00:07+02:00 2.sql #1: Error: line 3 (part 1): boom
            2026-10-09T14:00:07+02:00 2.sql #1: Failed: line 3 (part 1): boom
            2026-10-09T14:00:07+02:00 Run ended: 2 scripts, 1 succeeded, 1 failed, 0 cancelled.
            2026-10-09T14:00:07+02:00   1.sql: Succeeded, attempts: 1, rows: 5
            2026-10-09T14:00:07+02:00   2.sql: Failed, attempts: 1, error: line 3 (part 1): boom

            """.ReplaceLineEndings(),
            File.ReadAllText(Path.Combine(appData, "logs", "Untitled 1_20261009-140000", "run.log")));
    }

    [Fact]
    public async Task Summary_csv_is_written_again_at_each_status_change_with_Running_and_Queued_rows()
    {
        var b = NewBatch(new BatchSettings { ParallelLimit = 1 }, "1.sql", "2.sql");
        Script("1.sql", 5);
        var csv = Path.Combine(appData, "logs", "Untitled 1_20261009-140000", "summary.csv");

        list.Run(b.Id);
        await Until(b.Id, r => r.Scripts[0].Status == ScriptStatus.Running);
        Assert.Equal(
            """
            script,attempt,status,start,end,duration,rows,error,server,database
            1.sql,1,Running,2026-10-09T14:00:00+02:00,,,,,,
            2.sql,0,Queued,,,,,,,

            """.ReplaceLineEndings("\r\n"),
            File.ReadAllText(csv));
        Assert.False(File.Exists(csv + ".tmp"));

        time.Advance(TimeSpan.FromSeconds(5));
        await Until(b.Id, Ended);
        Assert.Contains("2.sql,1,Succeeded,2026-10-09T14:00:05+02:00,2026-10-09T14:00:05+02:00,0.000,", File.ReadAllText(csv));
    }

    [Fact]
    public async Task A_rerun_adds_its_attempt_to_the_same_folder_and_run_log_gets_a_second_summary()
    {
        var b = NewBatch(new BatchSettings(), "1.sql");
        Script("1.sql", 2, ScriptStatus.Failed, error: "boom");
        list.Run(b.Id);
        await Until(b.Id, r => r.Scripts[0].Status == ScriptStatus.Running);
        time.Advance(TimeSpan.FromSeconds(2));
        await Until(b.Id, Ended);

        Script("1.sql", 3);
        time.Advance(TimeSpan.FromSeconds(60));
        list.Rerun(b.Id, "1.sql");
        await Until(b.Id, r => r.Scripts[0].Status == ScriptStatus.Running);
        time.Advance(TimeSpan.FromSeconds(3));
        await Until(b.Id, Ended);

        var folder = Assert.Single(Directory.GetDirectories(Path.Combine(appData, "logs")));
        Assert.EndsWith(
            """
            1.sql,1,Failed,2026-10-09T14:00:00+02:00,2026-10-09T14:00:02+02:00,2.000,,boom,,
            1.sql,2,Succeeded,2026-10-09T14:01:02+02:00,2026-10-09T14:01:05+02:00,3.000,,,,

            """.ReplaceLineEndings("\r\n"),
            File.ReadAllText(Path.Combine(folder, "summary.csv")));
        var log = File.ReadAllText(Path.Combine(folder, "run.log"));
        Assert.Contains("2026-10-09T14:01:02+02:00 1.sql #2: Started", log);
        Assert.Equal(2, log.Split("Run ended:").Length - 1);
        Assert.EndsWith("1.sql: Succeeded, attempts: 2" + Environment.NewLine, log);
    }

    [Fact]
    public async Task A_second_run_in_the_same_second_gets_the_suffix_2_then_3()
    {
        var b = NewBatch(new BatchSettings(), "1.sql");
        for (var n = 0; n < 3; n++)
        {
            list.Run(b.Id);
            await Until(b.Id, Ended);
        }

        Assert.Equal(["Untitled 1_20261009-140000", "Untitled 1_20261009-140000-2", "Untitled 1_20261009-140000-3"],
            Directory.GetDirectories(Path.Combine(appData, "logs")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_saved_batch_logs_next_to_its_file_and_a_save_after_a_run_leaves_that_log_in_app_data()
    {
        var b = NewBatch(new BatchSettings(), "1.sql");
        list.Run(b.Id);
        await Until(b.Id, Ended);

        list.Save(b.Id, Path.Combine(root, "deploy"), overwrite: false);
        time.Advance(TimeSpan.FromSeconds(61));
        list.Run(b.Id);
        await Until(b.Id, r => Ended(r) && r.Start == time.GetUtcNow()); // the second run: the Save event still has the first one

        Assert.True(File.Exists(Path.Combine(appData, "logs", "Untitled 1_20261009-140000", "summary.csv")));
        Assert.True(File.Exists(Path.Combine(root, "logs", "deploy_20261009-140101", "summary.csv")));
        Assert.True(File.Exists(Path.Combine(root, "logs", "deploy_20261009-140101", "run.log")));
    }

    [Fact]
    public async Task A_failed_rename_warns_in_run_log_and_on_the_page_and_the_next_status_change_tries_again()
    {
        var b = NewBatch(new BatchSettings { ParallelLimit = 1 }, "1.sql", "2.sql");
        Script("1.sql", 5); Script("2.sql", 5);
        var folder = Path.Combine(appData, "logs", "Untitled 1_20261009-140000");
        var csv = Path.Combine(folder, "summary.csv");

        using var page = list.Subscribe();
        list.Run(b.Id);
        await Until(b.Id, r => r.Scripts[0].Status == ScriptStatus.Running);
        File.Delete(csv);
        Directory.CreateDirectory(csv); // the rename cannot replace a folder: the same as a file that Excel locks
        time.Advance(TimeSpan.FromSeconds(5));
        var run = await Until(b.Id, r => r.Scripts[1].Status == ScriptStatus.Running);

        Assert.Contains("Warning: cannot write summary.csv", File.ReadAllText(Path.Combine(folder, "run.log")));
        Assert.Equal(ScriptStatus.Succeeded, run.Scripts[0].Status); // the run continues
        List<Change> changes = [];
        while (page.Changes.TryRead(out var c)) changes.Add(c);
        var warning = Assert.Single(changes, c => c.Kind == "warning");
        Assert.Contains($"{{ id = {b.Id}, text = Warning: cannot write summary.csv", warning.Data.ToString());

        Directory.Delete(csv);
        time.Advance(TimeSpan.FromSeconds(5));
        await Until(b.Id, Ended);
        Assert.Contains("2.sql,1,Succeeded", File.ReadAllText(csv));
    }

    [Fact]
    public async Task A_batch_removed_during_a_run_still_ends_its_log()
    {
        var b = NewBatch(new BatchSettings { ParallelLimit = 1 }, "1.sql", "2.sql");
        Script("1.sql", 60);
        list.Run(b.Id);

        Assert.True(list.Remove(b.Id));
        await list.StopAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); // waits for the late end of 1.sql

        var folder = Path.Combine(appData, "logs", "Untitled 1_20261009-140000");
        Assert.Contains("1.sql,1,Cancelled", File.ReadAllText(Path.Combine(folder, "summary.csv")));
        Assert.Contains("Run ended: 2 scripts, 0 succeeded, 0 failed, 2 cancelled.", File.ReadAllText(Path.Combine(folder, "run.log")));
    }

    [Fact]
    public async Task Stop_cancels_all_scripts_of_all_batches_and_ends_when_the_logs_are_written()
    {
        var a = NewBatch(new BatchSettings { ParallelLimit = 1 }, "1.sql", "2.sql");
        Script("1.sql", 60); Script("2.sql", 60);
        list.Run(a.Id);
        await Until(a.Id, r => r.Scripts[0].Status == ScriptStatus.Running);

        await list.StopAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var folder = Path.Combine(appData, "logs", "Untitled 1_20261009-140000");
        var csv = File.ReadAllText(Path.Combine(folder, "summary.csv"));
        Assert.Contains("1.sql,1,Cancelled", csv);
        Assert.Contains("2.sql,0,Cancelled", csv);
        Assert.Contains("Run ended: 2 scripts, 0 succeeded, 0 failed, 2 cancelled.", File.ReadAllText(Path.Combine(folder, "run.log")));
    }
}
