using SqlRunboard.Core;

/// <summary>The run engine, seen through BatchList: Run, then the published batch changes. Fake executor + fake clock.</summary>
public sealed class RunTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("sqlrunboard-run-").FullName;
    readonly FakeTime time = new();
    readonly FakeSqlExecutor executor;
    readonly BatchList list;
    readonly BatchList.Subscription sub;

    public RunTests()
    {
        executor = new FakeSqlExecutor(time);
        list = new BatchList(executor, time);
        sub = list.Subscribe();
    }

    void Write(string path, string text = "select 1") => File.WriteAllText(Path.Combine(root, path), text);

    void Script(string path, int seconds, ScriptStatus status = ScriptStatus.Succeeded, long? rows = null) =>
        executor.Scripts[path] = new FakeScript { Duration = TimeSpan.FromSeconds(seconds), Status = status, Rows = rows };

    Batch NewBatch(int limit, params string[] files)
    {
        foreach (var f in files) Write(f);
        var b = list.NewBatch();
        return list.UpdateSettings(b.Id, new BatchSettings { Folder = root, ParallelLimit = limit })!;
    }

    /// <summary>Waits for the next published state of the batch that matches.</summary>
    async Task<RunState> Until(int id, Func<RunState, bool> ok)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var change = await sub.Changes.ReadAsync(cts.Token);
            if (change.Data is Batch { Run: { } run } b && b.Id == id && ok(run)) return run;
        }
    }

    static ScriptStatus StatusOf(RunState run, string path) => run.Scripts.Single(s => s.Path == path).Status;

    [Fact]
    public async Task Run_queues_the_included_scripts_in_the_start_order_and_obeys_the_parallel_limit()
    {
        var b = NewBatch(2, "1.sql", "2.sql", "3.sql", "4.sql");
        list.SetInclude(b.Id, "2.sql", false);
        Script("1.sql", 10, rows: 5); Script("3.sql", 20); Script("4.sql", 5);

        var run = list.Run(b.Id)!.Run!;

        Assert.Equal(["1.sql", "3.sql", "4.sql"], run.Scripts.Select(s => s.Path));
        Assert.Equal([ScriptStatus.Running, ScriptStatus.Running, ScriptStatus.Queued], run.Scripts.Select(s => s.Status));
        Assert.Equal(["4.sql"], run.Queue);
        Assert.Equal(["1.sql", "3.sql"], executor.Calls.Select(c => c.ScriptName));

        time.Advance(TimeSpan.FromSeconds(10));
        run = await Until(b.Id, r => StatusOf(r, "1.sql") == ScriptStatus.Succeeded);
        Assert.Equal(ScriptStatus.Running, StatusOf(run, "4.sql"));
        var a = Assert.Single(run.Scripts[0].Attempts);
        Assert.Equal((time.GetUtcNow() - TimeSpan.FromSeconds(10), time.GetUtcNow(), 5L), (a.Start, a.End, a.Rows));

        time.Advance(TimeSpan.FromSeconds(10));
        run = await Until(b.Id, r => r.Scripts.All(s => s.Status == ScriptStatus.Succeeded));
        Assert.Empty(run.Queue);
    }

    [Fact]
    public async Task A_limit_of_1_gives_an_ordered_run()
    {
        var b = NewBatch(1, "a.sql", "b.sql", "c.sql");
        Script("a.sql", 5); Script("b.sql", 5); Script("c.sql", 5);

        list.Run(b.Id);
        for (var n = 0; n < 3; n++)
        {
            var run = await Until(b.Id, r => r.Scripts.Count(s => s.Status == ScriptStatus.Running) == 1 && r.Scripts.Count(s => s.Status == ScriptStatus.Succeeded) == n);
            Assert.Equal(1, run.Scripts.Count(s => s.Status == ScriptStatus.Running));
            time.Advance(TimeSpan.FromSeconds(5));
        }

        await Until(b.Id, r => !r.Active);
        Assert.Equal(["a.sql", "b.sql", "c.sql"], executor.Calls.Select(c => c.ScriptName));
    }

    [Fact]
    public async Task A_script_is_sent_as_GO_parts_and_a_BOM_selects_the_encoding()
    {
        const string text = "select N'é'\r\nGO 2\r\nselect 2";
        var b = NewBatch(4);
        File.WriteAllText(Path.Combine(root, "utf8.sql"), text, new System.Text.UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "utf8bom.sql"), text, new System.Text.UTF8Encoding(true));
        File.WriteAllText(Path.Combine(root, "utf16le.sql"), text, new System.Text.UnicodeEncoding(false, true));
        File.WriteAllText(Path.Combine(root, "utf16be.sql"), text, new System.Text.UnicodeEncoding(true, true));

        list.Run(b.Id);
        await Until(b.Id, r => !r.Active);

        Assert.Equal(4, executor.Calls.Count);
        foreach (var call in executor.Calls)
            Assert.Equal([new ScriptPart("select N'é'", 2, 1), new ScriptPart("select 2", 1, 3)], call.Parts);
    }

    [Fact]
    public async Task Each_script_reads_its_file_when_it_starts_and_a_missing_file_fails()
    {
        var b = NewBatch(1, "a.sql", "b.sql", "c.sql");
        Script("a.sql", 5);

        list.Run(b.Id);
        Write("b.sql", "select 'fixed'");
        File.Delete(Path.Combine(root, "c.sql"));
        time.Advance(TimeSpan.FromSeconds(5));

        var run = await Until(b.Id, r => !r.Active);
        Assert.Equal([new ScriptPart("select 'fixed'", 1, 1)], executor.Calls.Single(c => c.ScriptName == "b.sql").Parts);
        Assert.Equal(ScriptStatus.Failed, StatusOf(run, "c.sql"));
        Assert.Equal("File not found", run.Scripts.Single(s => s.Path == "c.sql").Attempts.Single().Error);
        Assert.DoesNotContain(executor.Calls, c => c.ScriptName == "c.sql");
    }

    [Fact]
    public void Run_scans_again_and_a_new_file_goes_at_the_bottom_with_its_check_box_set()
    {
        var b = NewBatch(4, "2.sql", "3.sql");
        list.SetInclude(b.Id, "3.sql", false);
        Write("1.sql");

        b = list.Run(b.Id)!;

        Assert.Equal([new ScriptEntry("2.sql", true), new ScriptEntry("3.sql", false), new ScriptEntry("1.sql", true)], b.Scripts);
        Assert.Equal(["2.sql", "1.sql"], b.Run!.Scripts.Select(s => s.Path));
    }

    [Fact]
    public async Task The_run_locks_the_settings_and_the_check_boxes_and_Run_until_it_ends()
    {
        var b = NewBatch(4, "a.sql");
        Script("a.sql", 5);
        list.Run(b.Id);

        Assert.Equal(4, list.UpdateSettings(b.Id, b.Settings with { ParallelLimit = 8 })!.Settings.ParallelLimit);
        Assert.True(list.SetInclude(b.Id, "a.sql", false)!.Scripts.Single().Include);
        list.Run(b.Id);
        Assert.Single(executor.Calls);

        time.Advance(TimeSpan.FromSeconds(5));
        await Until(b.Id, r => !r.Active);
        Assert.Equal(8, list.UpdateSettings(b.Id, b.Settings with { ParallelLimit = 8 })!.Settings.ParallelLimit);
    }

    [Fact]
    public void Many_batches_run_at_the_same_time_each_with_its_own_limit()
    {
        var one = NewBatch(1, "a.sql", "b.sql");
        var two = NewBatch(2, "a.sql", "b.sql");
        Script("a.sql", 5); Script("b.sql", 5);

        Assert.Equal([ScriptStatus.Running, ScriptStatus.Queued], list.Run(one.Id)!.Run!.Scripts.Select(s => s.Status));
        Assert.Equal([ScriptStatus.Running, ScriptStatus.Running], list.Run(two.Id)!.Run!.Scripts.Select(s => s.Status));
    }

    public void Dispose()
    {
        sub.Dispose();
        Directory.Delete(root, recursive: true);
    }
}
