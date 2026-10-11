using SqlRunboard.Core;

/// <summary>The password of a batch: the server keeps it in memory after a logon, and forgets it after a logon failure or a target change.</summary>
public sealed class LogonTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("sqlrunboard-logon-").FullName;
    readonly FakeTime time = new();
    readonly FakeSqlExecutor executor;
    readonly BatchList list;
    readonly CancellationToken ct = TestContext.Current.CancellationToken;

    public LogonTests()
    {
        executor = new FakeSqlExecutor(time);
        list = new BatchList(executor, time);
    }

    static readonly BatchTarget SqlLogon = new() { Server = "sql01", Database = "Sales", Auth = Auth.SqlServer, User = "deploy" };

    int NewBatch(BatchTarget target)
    {
        File.WriteAllText(Path.Combine(root, "a.sql"), "select 1");
        var id = list.NewBatch().Id;
        list.UpdateSettings(id, new BatchSettings { Folder = root, Target = target });
        return id;
    }

    [Fact]
    public async Task A_SQL_Server_logon_asks_for_the_password_first()
    {
        var id = NewBatch(SqlLogon);

        var logon = await list.LogonAsync(id, null, ct);

        Assert.Equal(new SqlLogon(null, null, null, null, LogonFailed: true), logon);
        Assert.Empty(executor.Logons);
    }

    [Fact]
    public async Task The_server_keeps_the_password_for_the_next_logons_and_runs()
    {
        var id = NewBatch(SqlLogon);

        var first = await list.LogonAsync(id, "secret", ct);
        var second = await list.LogonAsync(id, null, ct);
        list.Run(id);

        Assert.Equal(new SqlLogon("sql01", "Sales", "deploy"), first);
        Assert.Equal(first, second);
        Assert.Equal(["secret", "secret"], executor.Logons.Select(c => c.Password));
        Assert.Equal("secret", Assert.Single(executor.Calls).Connection.Password);
    }

    [Fact]
    public async Task A_target_change_forgets_the_password_and_other_settings_do_not()
    {
        var id = NewBatch(SqlLogon);
        await list.LogonAsync(id, "secret", ct);

        list.UpdateSettings(id, new BatchSettings { Folder = root, Target = SqlLogon, ParallelLimit = 2 });
        Assert.False((await list.LogonAsync(id, null, ct))!.LogonFailed);

        list.UpdateSettings(id, new BatchSettings { Folder = root, Target = SqlLogon with { Database = "Other" } });
        Assert.True((await list.LogonAsync(id, null, ct))!.LogonFailed);
    }

    [Fact]
    public async Task A_rejected_password_is_forgotten()
    {
        var id = NewBatch(SqlLogon);
        executor.Logon = c => new SqlLogon(null, null, null, "Login failed for user 'deploy'.", LogonFailed: true);

        var logon = await list.LogonAsync(id, "wrong", ct);
        executor.Logon = c => new SqlLogon("sql01", "Sales", "deploy");

        Assert.Equal("Login failed for user 'deploy'.", logon!.Error);
        Assert.Equal(new SqlLogon(null, null, null, null, LogonFailed: true), await list.LogonAsync(id, null, ct));
    }

    [Fact]
    public async Task A_script_that_fails_its_logon_forgets_the_password()
    {
        var id = NewBatch(SqlLogon);
        executor.Scripts["a.sql"] = new FakeScript { Status = ScriptStatus.Failed, Error = "Login failed for user 'deploy'.", LogonFailed = true };
        await list.LogonAsync(id, "secret", ct);
        using var sub = list.Subscribe();

        list.Run(id);
        while ((await sub.Changes.ReadAsync(ct)).Data is not Batch { Run.Active: false }) { }

        Assert.True((await list.LogonAsync(id, null, ct))!.LogonFailed);
    }

    [Fact]
    public async Task An_integrated_logon_needs_no_password()
    {
        var id = NewBatch(SqlLogon with { Auth = Auth.Integrated, User = null });

        var logon = await list.LogonAsync(id, null, ct);

        Assert.Equal(new SqlLogon("sql01", "Sales", "fake"), logon);
    }

    [Fact]
    public async Task An_unknown_batch_has_no_logon() => Assert.Null(await list.LogonAsync(99, "x", ct));

    public void Dispose() => Directory.Delete(root, recursive: true);
}
