using Microsoft.Playwright;
using SqlRunboard.Core;

/// <summary>The password dialog and Test connection, with the fake executor.</summary>
public class LogonTests : AppPageTest
{
    readonly string scripts = Directory.CreateTempSubdirectory("sqlrunboard-scripts-").FullName;
    static readonly BatchTarget SqlLogon = new() { Server = "sql01", Database = "Sales", Auth = Auth.SqlServer, User = "deploy" };

    ILocator Panel => Page.Locator("#panel");
    ILocator Header => Page.Locator("#hdr");
    ILocator Dialog => Page.GetByRole(AriaRole.Dialog);
    ILocator RunButton => Header.GetByRole(AriaRole.Button, new() { Name = "Run", Exact = true });

    async Task<Batch> OpenBatchAsync(BatchTarget target)
    {
        File.WriteAllText(Path.Combine(scripts, "a.sql"), "select 1");
        App.Executor.Scripts["a.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(10) };
        var b = App.Host.Batches.NewBatch();
        b = App.Host.Batches.UpdateSettings(b.Id, new BatchSettings { Folder = scripts, Target = target })!;
        await Page.GetByRole(AriaRole.Navigation, new() { Name = "Batches" }).Locator($"[role=option]:has-text('{b.Name}')").ClickAsync();
        return b;
    }

    async Task SignInAsync(string password)
    {
        await Dialog.GetByLabel("Password").FillAsync(password);
        await Dialog.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
    }

    async Task<string?[]> RunAndWaitAsync(int runs)
    {
        // The script runs until the clock moves, so a late run response cannot show it Running again after the end.
        await Expect(Panel.GetByRole(AriaRole.Region, new() { Name = "Running", Exact = true }).Locator(".card")).ToHaveCountAsync(1);
        App.Time.Advance(TimeSpan.FromSeconds(10));
        await Expect(RunButton).ToBeEnabledAsync();
        Assert.Equal(runs, App.Executor.Calls.Count);
        return [.. App.Executor.Calls.Select(c => c.Connection.Password)];
    }

    [Fact]
    public async Task The_first_run_asks_for_the_password_and_the_next_run_does_not()
    {
        await OpenBatchAsync(SqlLogon);

        await RunButton.ClickAsync();
        await Expect(Dialog).ToContainTextAsync("Sign in to sql01");
        await SignInAsync("secret");
        Assert.Equal(["secret"], await RunAndWaitAsync(1));

        await RunButton.ClickAsync();
        Assert.Equal(["secret", "secret"], await RunAndWaitAsync(2));
        await Expect(Dialog).ToHaveCountAsync(0);
        Assert.Equal(0, await Page.EvaluateAsync<int>("() => JSON.stringify(app).split('secret').length - 1")); // the page keeps no password
    }

    [Fact]
    public async Task A_rejected_password_asks_again_with_the_error()
    {
        await OpenBatchAsync(SqlLogon);
        App.Executor.Logon = c => c.Password == "wrong" ? new SqlLogon(null, null, null, "Login failed for user 'deploy'.", LogonFailed: true) : new SqlLogon("SQL01", "Sales", "deploy");

        await RunButton.ClickAsync();
        await SignInAsync("wrong");
        await Expect(Dialog.GetByRole(AriaRole.Alert)).ToHaveTextAsync("Login failed for user 'deploy'. Type the password again.");
        await SignInAsync("right");

        Assert.Equal(["right"], await RunAndWaitAsync(1));
    }

    [Fact]
    public async Task A_target_change_asks_for_the_password_again()
    {
        await OpenBatchAsync(SqlLogon);
        await RunButton.ClickAsync();
        await SignInAsync("secret");
        await RunAndWaitAsync(1);

        await Page.GetByRole(AriaRole.Tab, new() { Name = "Settings" }).ClickAsync();
        await Panel.GetByLabel("Database").FillAsync("Other");
        await Panel.GetByLabel("Database").PressAsync("Enter");
        await RunButton.ClickAsync();

        await Expect(Dialog).ToContainTextAsync("Other");
    }

    [Fact]
    public async Task A_script_that_fails_its_logon_asks_for_the_password_at_the_rerun()
    {
        await OpenBatchAsync(SqlLogon);
        App.Executor.Scripts["a.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(10), Status = ScriptStatus.Failed, Error = "Login failed for user 'deploy'.", LogonFailed = true };
        await RunButton.ClickAsync();
        await SignInAsync("secret");
        await RunAndWaitAsync(1);

        await Header.GetByRole(AriaRole.Button, new() { Name = "Rerun all failed" }).ClickAsync();
        await SignInAsync("again");

        await Expect(Panel.GetByRole(AriaRole.Region, new() { Name = "Running", Exact = true }).Locator(".card")).ToHaveCountAsync(1);
        Assert.Equal(["secret", "again"], App.Executor.Calls.Select(c => c.Connection.Password));
    }

    [Fact]
    public async Task Test_connection_shows_the_server_name_and_the_database()
    {
        await OpenBatchAsync(SqlLogon with { Auth = Auth.Integrated });
        App.Executor.Logon = c => new SqlLogon("SQL01\\PROD", "Sales", "EXAMPLE\\hank");

        await Page.GetByRole(AriaRole.Tab, new() { Name = "Settings" }).ClickAsync();
        await Panel.GetByRole(AriaRole.Button, new() { Name = "Test connection" }).ClickAsync();

        await Expect(Panel.GetByRole(AriaRole.Status)).ToHaveTextAsync("Connected. Server SQL01\\PROD, database Sales, login EXAMPLE\\hank.");
    }

    [Fact]
    public async Task A_logon_error_shows_a_banner_and_does_not_run()
    {
        await OpenBatchAsync(SqlLogon with { Auth = Auth.Integrated });
        App.Executor.Logon = c => new SqlLogon(null, null, null, "Cannot authenticate using Kerberos.");

        await RunButton.ClickAsync();

        await Expect(Page.GetByRole(AriaRole.Alert)).ToHaveTextAsync("Cannot authenticate using Kerberos.");
        Assert.Empty(App.Executor.Calls);
    }

    [Fact]
    public async Task The_Settings_tab_offers_only_Integrated_and_SQL_Server_logon()
    {
        await OpenBatchAsync(SqlLogon);
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Settings" }).ClickAsync();

        await Expect(Panel.GetByLabel("Logon").Locator("option")).ToHaveTextAsync(["Integrated (Windows or Kerberos)", "SQL Server"]);
    }
}
