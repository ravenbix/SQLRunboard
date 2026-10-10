using SqlRunboard.Core;

public class FakeSqlExecutorTests
{
    static readonly SqlConnectionSettings Connection = new(new BatchTarget { Server = "sql01", Database = "Sales" }, Password: "secret");

    [Fact]
    public async Task Script_runs_for_its_duration_on_the_clock_then_gives_its_result()
    {
        var time = new FakeTime();
        var executor = new FakeSqlExecutor(time);
        executor.Scripts["005_fk.sql"] = new FakeScript
        {
            Duration = TimeSpan.FromSeconds(30),
            Status = ScriptStatus.Failed,
            Rows = 42,
            Error = "line 214 (part 3): FK conflict",
            Messages = [new SqlMessage(SqlMessageKind.Info, "Step 1 of 3 done")],
        };
        var messages = new List<SqlMessage>();

        var run = executor.ExecuteAsync(new SqlExecution("005_fk.sql", ["SELECT 1", "SELECT 2"], Connection), messages.Add, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(run.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        var result = await run;

        Assert.Equal(new SqlResult(ScriptStatus.Failed, 42, "line 214 (part 3): FK conflict"), result);
        Assert.Equal(
            [new SqlMessage(SqlMessageKind.Info, "Step 1 of 3 done"), new SqlMessage(SqlMessageKind.Error, "line 214 (part 3): FK conflict")],
            messages);
        var call = Assert.Single(executor.Calls);
        Assert.Equal(["SELECT 1", "SELECT 2"], call.Commands);
        Assert.Equal("sql01", call.Connection.Target.Server);
        Assert.Equal("secret", call.Connection.Password);
    }

    [Fact]
    public async Task Unknown_script_succeeds_at_once()
    {
        var executor = new FakeSqlExecutor(new FakeTime());

        var result = await executor.ExecuteAsync(new SqlExecution("x.sql", ["SELECT 1"], Connection), _ => { }, CancellationToken.None);

        Assert.Equal(new SqlResult(ScriptStatus.Succeeded, null, null), result);
    }

    [Fact]
    public async Task Cancel_stops_a_running_script()
    {
        var executor = new FakeSqlExecutor(new FakeTime());
        executor.Scripts["slow.sql"] = new FakeScript { Duration = TimeSpan.FromMinutes(5) };
        using var cts = new CancellationTokenSource();

        var run = executor.ExecuteAsync(new SqlExecution("slow.sql", ["SELECT 1"], Connection), _ => { }, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }
}
