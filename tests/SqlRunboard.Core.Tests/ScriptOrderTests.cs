using SqlRunboard.Core;

/// <summary>docs/SPEC.md "Script order": move commands, Sort by file name, New and Missing files. Real temp folders.</summary>
public sealed class ScriptOrderTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("sqlrunboard-order-").FullName;
    readonly FakeTime time = new();
    readonly FakeSqlExecutor executor;
    readonly BatchList list;

    public ScriptOrderTests()
    {
        executor = new FakeSqlExecutor(time);
        list = new BatchList(executor, time);
    }

    void Touch(params string[] paths) { foreach (var p in paths) File.WriteAllText(Path.Combine(root, p), "select 1"); }

    Batch NewBatch(int limit, params string[] files)
    {
        Touch(files);
        var b = list.NewBatch();
        return list.UpdateSettings(b.Id, new BatchSettings { Folder = root, ParallelLimit = limit })!;
    }

    static string[] Order(Batch b) => [.. b.Scripts.Select(s => s.Path)];

    [Fact]
    public void Before_a_run_the_move_commands_change_the_start_order()
    {
        var b = NewBatch(4, "a.sql", "b.sql", "c.sql", "d.sql");

        Assert.Equal(["a.sql", "c.sql", "b.sql", "d.sql"], Order(list.Move(b.Id, "c.sql", MoveTo.Up)!));
        Assert.Equal(["a.sql", "c.sql", "d.sql", "b.sql"], Order(list.Move(b.Id, "b.sql", MoveTo.Down)!));
        Assert.Equal(["b.sql", "a.sql", "c.sql", "d.sql"], Order(list.Move(b.Id, "b.sql", MoveTo.Top)!));
        var moved = list.Move(b.Id, "a.sql", MoveTo.Bottom)!;
        Assert.Equal(["b.sql", "c.sql", "d.sql", "a.sql"], Order(moved));
        Assert.True(moved.Dirty);

        var saved = list.Save(b.Id, Path.Combine(root, "x"), overwrite: false)!;
        Assert.Equal(["b.sql", "c.sql", "d.sql", "a.sql"], Order(list.Reload(saved.Id)!));
    }

    [Fact]
    public void During_a_run_the_move_commands_change_the_queue_of_that_run_only()
    {
        var b = NewBatch(1, "a.sql", "b.sql", "c.sql", "d.sql");
        executor.Scripts["a.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(60) };
        list.Run(b.Id);
        var dirty = list.Move(b.Id, "a.sql", MoveTo.Bottom)!.Dirty; // a Running script does not move

        var moved = list.Move(b.Id, "d.sql", MoveTo.Top)!;

        Assert.Equal(["d.sql", "b.sql", "c.sql"], moved.Run!.Queue);
        Assert.Equal(["a.sql", "b.sql", "c.sql", "d.sql"], Order(moved)); // the start order stays
        Assert.Equal(dirty, moved.Dirty);
        Assert.Equal(["d.sql", "b.sql", "c.sql"], list.Move(b.Id, "a.sql", MoveTo.Bottom)!.Run!.Queue);
    }

    [Fact]
    public void Sort_by_file_name_resets_the_order_to_the_natural_sort()
    {
        var b = NewBatch(4, "2_x.sql", "10_x.sql", "1_x.sql");
        list.Move(b.Id, "1_x.sql", MoveTo.Bottom);
        list.SetInclude(b.Id, "2_x.sql", false);

        var sorted = list.SortByFileName(b.Id)!;

        Assert.Equal([("1_x.sql", true), ("2_x.sql", false), ("10_x.sql", true)], sorted.Scripts.Select(s => (s.Path, s.Include)));
    }

    [Fact]
    public void A_new_file_is_marked_New_and_a_missing_file_stays_with_a_warning_until_the_next_save()
    {
        var b = NewBatch(4, "a.sql", "b.sql");
        list.Save(b.Id, Path.Combine(root, "x"), overwrite: false);
        Touch("d.sql", "c.sql");
        File.Delete(Path.Combine(root, "a.sql"));

        var scanned = list.Run(b.Id)!;

        Assert.Equal([new("a.sql", true, ScriptState.Missing), new("b.sql", true), new("c.sql", true, ScriptState.New), new("d.sql", true, ScriptState.New)], scanned.Scripts);
        Assert.Equal(["b.sql", "c.sql", "d.sql"], scanned.Run!.Scripts.Select(s => s.Path)); // a missing file does not run

        var saved = list.Save(b.Id, null, overwrite: false)!;

        Assert.Equal([new("b.sql", true), new("c.sql", true), new("d.sql", true)], saved.Scripts);
        Assert.DoesNotContain("a.sql", File.ReadAllText(saved.File!));
        Assert.DoesNotContain("state", File.ReadAllText(saved.File!));
    }

    [Fact]
    public void A_missing_file_that_comes_back_loses_its_warning()
    {
        var b = NewBatch(4, "a.sql");
        File.Move(Path.Combine(root, "a.sql"), Path.Combine(root, "a.bak"));
        Assert.Equal(ScriptState.Missing, list.UpdateSettings(b.Id, b.Settings)!.Scripts.Single().State);
        File.Move(Path.Combine(root, "a.bak"), Path.Combine(root, "a.sql"));

        Assert.NotEqual(ScriptState.Missing, list.UpdateSettings(b.Id, b.Settings)!.Scripts.Single().State);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
