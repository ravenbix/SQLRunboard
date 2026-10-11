using SqlRunboard.Core;

/// <summary>A restart: a new BatchList gets the batch file paths that the old one reported, and reads the last runs from disk.</summary>
public sealed class RestoreTests : IAsyncDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("sqlrunboard-restore-").FullName;
    readonly FakeTime time = new();
    readonly FakeSqlExecutor executor;
    readonly BatchList list;
    readonly BatchList.Subscription sub;
    IReadOnlyList<string> files = [];
    readonly List<BatchList> lists = [];

    public RestoreTests()
    {
        executor = new FakeSqlExecutor(time);
        list = new BatchList(executor, time) { FilesChanged = f => files = f };
        sub = list.Subscribe();
        lists.Add(list);
    }

    /// <summary>Stops the runs of all lists first: a late script end writes its log in root.</summary>
    public async ValueTask DisposeAsync()
    {
        sub.Dispose();
        foreach (var l in lists) await l.StopAsync();
        Directory.Delete(root, recursive: true);
    }

    /// <summary>A saved batch with these scripts in root/&lt;name&gt;/.</summary>
    Batch Saved(string name, BatchTarget? target = null, params string[] scripts)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        foreach (var s in scripts) File.WriteAllText(Path.Combine(folder, s), "select 1");
        var b = list.NewBatch();
        list.UpdateSettings(b.Id, new BatchSettings { Folder = folder, ParallelLimit = 1, Target = target ?? new() });
        return list.Save(b.Id, Path.Combine(folder, name), overwrite: false)!;
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

    BatchList Restart()
    {
        var again = new BatchList(executor, time);
        lists.Add(again);
        again.Restore(files);
        return again;
    }

    [Fact]
    public void The_side_list_comes_back_in_the_same_order_without_the_unsaved_batches()
    {
        var a = Saved("a");
        list.NewBatch();
        var b = Saved("b");
        list.MoveBatch(b.Id, MoveTo.Top);

        var batches = Restart().Subscribe().Snapshot;

        Assert.Equal(["b", "a"], batches.Select(x => x.Name));
        Assert.Equal([b.File, a.File], batches.Select(x => x.File));
        Assert.All(batches, x => Assert.Null(x.FileError));
    }

    [Fact]
    public void A_missing_batch_file_stays_with_File_not_found_until_you_remove_it()
    {
        var a = Saved("a");
        Saved("b");
        File.Delete(a.File!);

        var again = Restart();
        var missing = again.Subscribe().Snapshot[0];

        Assert.Equal("a", missing.Name);
        Assert.Equal("File not found", missing.FileError);
        List<IReadOnlyList<string>> reported = [];
        again.FilesChanged = reported.Add;
        Assert.True(again.Remove(missing.Id));
        Assert.Equal(["b"], again.Subscribe().Snapshot.Select(x => x.Name));
        Assert.Equal([Path.Combine(root, "b", "b.sqlrunboard.json")], reported.Single());
    }

    /// <summary>Runs "a" twice. The second (newest) run stops with 1.sql Succeeded, 2.sql Failed, 3.sql Running and 4.sql Queued.</summary>
    async Task<Batch> InterruptedRun(BatchTarget? target = null)
    {
        var a = Saved("a", target, "1.sql", "2.sql", "3.sql", "4.sql");
        list.Run(a.Id);
        await Until(a.Id, r => !r.Active);
        time.Advance(TimeSpan.FromMinutes(1));
        executor.Scripts["1.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(5), Rows = 7 };
        executor.Scripts["2.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(5), Status = ScriptStatus.Failed, Error = "boom, \"quoted\"\nline 2" };
        executor.Scripts["3.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(100) };
        list.Run(a.Id);
        await Until(a.Id, r => r.Scripts[0].Status == ScriptStatus.Running);
        time.Advance(TimeSpan.FromSeconds(5));
        await Until(a.Id, r => r.Scripts[1].Status == ScriptStatus.Running);
        time.Advance(TimeSpan.FromSeconds(5));
        await Until(a.Id, r => r.Scripts[2].Status == ScriptStatus.Running);
        return a;
    }

    [Fact]
    public async Task Each_batch_shows_its_last_run_and_Queued_or_Running_scripts_show_as_Cancelled()
    {
        await InterruptedRun();

        var run = Restart().Subscribe().Snapshot[0].Run!;

        var start = new DateTimeOffset(2026, 10, 9, 12, 1, 0, TimeSpan.Zero);
        Assert.Equal(start, run.Start);
        Assert.False(run.Ended);
        Assert.False(run.Active);
        Assert.Empty(run.Queue);
        Assert.Equal(["1.sql", "2.sql", "3.sql", "4.sql"], run.Scripts.Select(s => s.Path));
        Assert.Equal([ScriptStatus.Succeeded, ScriptStatus.Failed, ScriptStatus.Cancelled, ScriptStatus.Cancelled], run.Scripts.Select(s => s.Status));
        Assert.Equal(new Attempt(start, start.AddSeconds(5), ScriptStatus.Succeeded, 7, null), run.Scripts[0].Attempts.Single());
        Assert.Equal("boom, \"quoted\"\nline 2", run.Scripts[1].Attempts.Single().Error);
        var stopped = run.Scripts[2].Attempts.Single();
        Assert.Equal((start.AddSeconds(10), ScriptStatus.Cancelled, "The app stopped"), (stopped.Start, stopped.Status, stopped.Error));
        Assert.NotNull(stopped.End);
        Assert.Equal(("4.sql", ScriptStatus.Cancelled, "The app stopped"), (run.Scripts[3].Path, run.Scripts[3].Status, run.Scripts[3].Error));
        Assert.Empty(run.Scripts[3].Attempts);
    }

    [Fact]
    public async Task Rerun_all_failed_continues_an_interrupted_run_in_its_log_folder()
    {
        var a = await InterruptedRun();
        executor.Scripts.Clear();

        var again = Restart();
        using var sub2 = again.Subscribe();
        var id = sub2.Snapshot[0].Id;
        again.RerunFailed(id);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        RunState run;
        do run = ((Batch)(await sub2.Changes.ReadAsync(cts.Token)).Data).Run!;
        while (run.Active || run.Scripts[3].Attempts.Count == 0);
        Assert.Equal([1, 2, 2, 1], run.Scripts.Select(s => s.Attempts.Count));
        Assert.All(run.Scripts, s => Assert.Equal(ScriptStatus.Succeeded, s.Status));

        var logs = Directory.GetDirectories(Path.Combine(Path.GetDirectoryName(a.File)!, "logs")).Order().ToArray();
        Assert.Equal(2, logs.Length); // no new log folder
        var csv = File.ReadAllText(Path.Combine(logs[1], "summary.csv"));
        Assert.Contains("3.sql,2,Succeeded", csv);
        Assert.Contains("4.sql,1,Succeeded", csv);
    }

    [Fact]
    public async Task A_target_change_while_the_app_was_stopped_ends_the_run()
    {
        var a = await InterruptedRun(new BatchTarget { Server = "sql01", Database = "Sales" });
        File.WriteAllText(a.File!, File.ReadAllText(a.File!).Replace("\"sql01\"", "\"sql02\"")); // for example after a pull from the repository

        var again = Restart();
        var b = again.Subscribe().Snapshot[0];

        Assert.True(b.Run!.Ended);
        Assert.Equal(b, again.RerunFailed(b.Id)); // the rerun commands do nothing
    }
}
