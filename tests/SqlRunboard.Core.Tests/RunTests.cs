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

    [Fact]
    public async Task Cancel_stops_a_Running_script_and_a_Queued_script_never_starts()
    {
        var b = NewBatch(1, "a.sql", "b.sql", "c.sql");
        Script("a.sql", 60); Script("b.sql", 5); Script("c.sql", 5);
        list.Run(b.Id);

        var run = list.Cancel(b.Id, "b.sql")!.Run!;
        Assert.Equal(ScriptStatus.Cancelled, StatusOf(run, "b.sql"));
        Assert.Empty(run.Scripts.Single(s => s.Path == "b.sql").Attempts);
        Assert.Equal(["c.sql"], run.Queue);

        list.Cancel(b.Id, "a.sql");
        run = await Until(b.Id, r => StatusOf(r, "a.sql") == ScriptStatus.Cancelled);
        Assert.Equal(ScriptStatus.Cancelled, run.Scripts[0].Attempts.Single().Status);
        Assert.Equal(ScriptStatus.Running, StatusOf(run, "c.sql")); // the others continue
        Assert.DoesNotContain(executor.Calls, c => c.ScriptName == "b.sql");
    }

    [Fact]
    public async Task Cancel_all_cancels_the_Running_and_Queued_scripts_and_ends_the_run()
    {
        var b = NewBatch(2, "a.sql", "b.sql", "c.sql");
        Script("a.sql", 60); Script("b.sql", 60);
        list.Run(b.Id);

        list.CancelAll(b.Id);

        var run = await Until(b.Id, r => !r.Active);
        Assert.All(run.Scripts, s => Assert.Equal(ScriptStatus.Cancelled, s.Status));
        Assert.Equal([1, 1, 0], run.Scripts.Select(s => s.Attempts.Count));
        Assert.Equal(2, executor.Calls.Count);
    }

    [Fact]
    public async Task A_script_that_reaches_the_timeout_fails_with_Timeout_after_N_s()
    {
        var b = NewBatch(4, "a.sql", "b.sql");
        list.UpdateSettings(b.Id, b.Settings with { TimeoutSeconds = 30 });
        Script("a.sql", 60); Script("b.sql", 20);
        list.Run(b.Id);

        time.Advance(TimeSpan.FromSeconds(30));

        var run = await Until(b.Id, r => !r.Active);
        var a = run.Scripts[0].Attempts.Single();
        Assert.Equal((ScriptStatus.Failed, "Timeout after 30 s"), (a.Status, a.Error));
        Assert.Equal(ScriptStatus.Succeeded, StatusOf(run, "b.sql"));
    }

    [Theory]
    [InlineData("fails")]
    [InlineData("is missing")]
    public async Task Stop_the_queue_at_the_first_failure_cancels_the_Queued_scripts_and_the_Running_ones_continue(string how)
    {
        var b = NewBatch(2, "a.sql", "b.sql", "c.sql", "d.sql");
        list.UpdateSettings(b.Id, b.Settings with { StopOnFirstFailure = true });
        Script("a.sql", 5, ScriptStatus.Failed); Script("b.sql", 30);
        if (how == "is missing") Script("a.sql", 5);

        list.Run(b.Id);
        if (how == "is missing") File.Delete(Path.Combine(root, "c.sql"));
        time.Advance(TimeSpan.FromSeconds(10));

        var run = await Until(b.Id, r => !r.Queue.Contains("d.sql"));
        Assert.Equal(ScriptStatus.Running, StatusOf(run, "b.sql"));
        var d = run.Scripts.Single(s => s.Path == "d.sql");
        Assert.Equal((ScriptStatus.Cancelled, "Not started: the queue stopped"), (d.Status, d.Error));
        Assert.Empty(d.Attempts);

        time.Advance(TimeSpan.FromSeconds(20));
        run = await Until(b.Id, r => !r.Active);
        Assert.Equal(ScriptStatus.Succeeded, StatusOf(run, "b.sql"));
        Assert.DoesNotContain(executor.Calls, c => c.ScriptName == "d.sql");
    }

    [Fact]
    public async Task A_rerun_reads_the_file_again_goes_to_the_bottom_of_the_queue_and_adds_an_attempt()
    {
        var b = NewBatch(1, "a.sql", "b.sql", "c.sql");
        Script("a.sql", 5, ScriptStatus.Failed); Script("b.sql", 5); Script("c.sql", 5);
        list.Run(b.Id);
        time.Advance(TimeSpan.FromSeconds(5));
        await Until(b.Id, r => StatusOf(r, "b.sql") == ScriptStatus.Running);

        Write("a.sql", "select 'fixed'");
        Script("a.sql", 5);
        var run = list.Rerun(b.Id, "a.sql")!.Run!;
        Assert.Equal(["c.sql", "a.sql"], run.Queue);
        Assert.Equal(ScriptStatus.Queued, StatusOf(run, "a.sql"));
        Assert.Equal(["c.sql", "a.sql"], list.Rerun(b.Id, "b.sql")!.Run!.Queue); // a Running script does not rerun

        time.Advance(TimeSpan.FromSeconds(5));
        await Until(b.Id, r => StatusOf(r, "c.sql") == ScriptStatus.Running);
        time.Advance(TimeSpan.FromSeconds(5));
        await Until(b.Id, r => StatusOf(r, "a.sql") == ScriptStatus.Running);
        time.Advance(TimeSpan.FromSeconds(5));
        run = await Until(b.Id, r => !r.Active);

        Assert.Equal([ScriptStatus.Failed, ScriptStatus.Succeeded], run.Scripts[0].Attempts.Select(a => a.Status));
        Assert.Equal(["a.sql", "b.sql", "c.sql", "a.sql"], executor.Calls.Select(c => c.ScriptName));
        Assert.Equal([new ScriptPart("select 'fixed'", 1, 1)], executor.Calls.Last().Parts);
    }

    [Fact]
    public async Task Rerun_all_failed_opens_the_run_again_in_the_start_order_with_the_current_settings_and_ignores_the_check_boxes()
    {
        var b = NewBatch(4, "a.sql", "b.sql", "c.sql", "d.sql");
        Script("a.sql", 5); Script("b.sql", 10, ScriptStatus.Failed); Script("c.sql", 5, ScriptStatus.Failed); Script("d.sql", 60);
        list.Run(b.Id);
        list.Cancel(b.Id, "d.sql");
        time.Advance(TimeSpan.FromSeconds(10));
        await Until(b.Id, r => !r.Active);
        list.SetInclude(b.Id, "b.sql", false);
        list.UpdateSettings(b.Id, b.Settings with { ParallelLimit = 1, StopOnFirstFailure = true });

        var run = list.RerunFailed(b.Id)!.Run!;
        Assert.True(run.Active);
        Assert.Equal(ScriptStatus.Running, StatusOf(run, "b.sql"));
        Assert.Equal(["c.sql", "d.sql"], run.Queue);

        time.Advance(TimeSpan.FromSeconds(10)); // b fails again: the stop setting applies to reruns
        run = await Until(b.Id, r => !r.Active && r.Scripts[1].Attempts.Count == 2);
        Assert.Equal([1, 2, 1, 1], run.Scripts.Select(s => s.Attempts.Count));
        Assert.Equal("Not started: the queue stopped", run.Scripts[2].Error);
    }

    [Theory]
    [InlineData("server")]
    [InlineData("database")]
    [InlineData("auth")]
    [InlineData("user")]
    [InlineData("encrypt")]
    [InlineData("extra")]
    [InlineData("folder")]
    [InlineData("filters")]
    [InlineData("recurse")]
    public async Task A_change_to_the_target_or_the_file_scan_ends_the_run_and_reruns_do_nothing(string change)
    {
        var b = NewBatch(4, "a.sql");
        Script("a.sql", 5, ScriptStatus.Failed);
        list.Run(b.Id);
        time.Advance(TimeSpan.FromSeconds(5));
        await Until(b.Id, r => !r.Active);
        var s = b.Settings with { ParallelLimit = 2, StopOnFirstFailure = true, TimeoutSeconds = 9, ConfirmBeforeRun = true };
        Assert.False(list.UpdateSettings(b.Id, s)!.Run!.Ended);

        var t = s.Target;
        var run = list.UpdateSettings(b.Id, change switch
        {
            "server" => s with { Target = t with { Server = "other" } },
            "database" => s with { Target = t with { Database = "other" } },
            "auth" => s with { Target = t with { Auth = Auth.SqlServer } },
            "user" => s with { Target = t with { User = "other" } },
            "encrypt" => s with { Target = t with { Encrypt = Encrypt.Strict } },
            "extra" => s with { Target = t with { ExtraConnectionString = "App=x" } },
            "folder" => s with { Folder = root + Path.DirectorySeparatorChar },
            "filters" => s with { Filters = "*.txt" },
            _ => s with { Recurse = true },
        })!.Run!;

        Assert.True(run.Ended);
        Assert.Equal(run, list.RerunFailed(b.Id)!.Run);
        Assert.Equal(run, list.Rerun(b.Id, "a.sql")!.Run);
        Assert.Single(executor.Calls);
        var next = list.Run(b.Id)!.Run!; // Run starts a new run, with attempt counts from 0
        Assert.False(next.Ended);
        Assert.All(next.Scripts, x => Assert.Single(x.Attempts));
    }

    public void Dispose()
    {
        sub.Dispose();
        Directory.Delete(root, recursive: true);
    }
}
