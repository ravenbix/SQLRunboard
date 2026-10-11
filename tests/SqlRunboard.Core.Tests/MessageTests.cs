using SqlRunboard.Core;

/// <summary>The messages of each attempt, for the drawer: live "message" events, and the kept messages in a new subscription.</summary>
public sealed class MessageTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("sqlrunboard-msg-").FullName;
    readonly FakeTime time = new();
    readonly FakeSqlExecutor executor;
    readonly BatchList list;
    readonly BatchList.Subscription sub;

    public MessageTests()
    {
        executor = new FakeSqlExecutor(time);
        list = new BatchList(executor, time);
        sub = list.Subscribe();
    }

    Batch NewBatch(params string[] files)
    {
        foreach (var f in files) File.WriteAllText(Path.Combine(root, f), "select 1");
        var b = list.NewBatch();
        return list.UpdateSettings(b.Id, new BatchSettings { Folder = root })!;
    }

    static SqlMessage Info(string text) => new(SqlMessageKind.Info, text);

    /// <summary>Reads the changes until the predicate matches. Returns all changes that it read.</summary>
    async Task<List<Change>> ReadUntil(Func<Change, bool> ok)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        List<Change> seen = [];
        while (true)
        {
            var c = await sub.Changes.ReadAsync(cts.Token);
            seen.Add(c);
            if (ok(c)) return seen;
        }
    }

    static bool Ended(Change c, int attempts) => c.Data is Batch { Run: { Active: false } r } && r.Scripts.All(s => s.Attempts.Count == attempts);

    [Fact]
    public async Task Executor_messages_are_published_live_after_the_attempt_starts_and_kept_for_a_new_subscriber()
    {
        var b = NewBatch("a.sql");
        executor.Scripts["a.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(5), Messages = [Info("hello"), new(SqlMessageKind.RowCount, "3 rows affected")], Status = ScriptStatus.Failed, Error = "line 2 (part 1): boom" };

        list.Run(b.Id);
        time.Advance(TimeSpan.FromSeconds(5));
        var seen = await ReadUntil(c => Ended(c, 1));

        var running = seen.FindIndex(c => c.Data is Batch { Run: { } r } && r.Scripts[0].Status == ScriptStatus.Running);
        var messages = seen.Where(c => c.Kind == "message").Select(c => (ScriptMessage)c.Data).ToList();
        Assert.True(running >= 0 && running < seen.FindIndex(c => c.Kind == "message"), "the attempt shows Running before its first message");
        var start = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal([
            new ScriptMessage(b.Id, "a.sql", 1, start, SqlMessageKind.Info, "hello"),
            new ScriptMessage(b.Id, "a.sql", 1, start, SqlMessageKind.RowCount, "3 rows affected"),
            new ScriptMessage(b.Id, "a.sql", 1, start.AddSeconds(5), SqlMessageKind.Error, "line 2 (part 1): boom")], messages);

        using var late = list.Subscribe();
        var kept = Assert.Single(late.Messages);
        Assert.Equal((b.Id, "a.sql", 1, 0), (kept.Id, kept.Path, kept.Attempt, kept.Earlier));
        Assert.Equal(["hello", "3 rows affected", "line 2 (part 1): boom"], kept.Messages.Select(m => m.Text));
    }

    public void Dispose()
    {
        sub.Dispose();
        Directory.Delete(root, recursive: true);
    }
}
