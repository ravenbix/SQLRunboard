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

    public void Dispose() => Directory.Delete(root, recursive: true);
}
