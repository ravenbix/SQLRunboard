using Microsoft.Playwright;
using SqlRunboard.Core;

/// <summary>"Confirm before run": the dialog before Run, Rerun and "Rerun all failed".</summary>
public class ConfirmBeforeRunTests : AppPageTest
{
    readonly string scripts = Directory.CreateTempSubdirectory("sqlrunboard-scripts-").FullName;

    ILocator Panel => Page.Locator("#panel");
    ILocator Header => Page.Locator("#hdr");
    ILocator Dialog => Page.GetByRole(AriaRole.Dialog);
    ILocator RunButton => Header.GetByRole(AriaRole.Button, new() { Name = "Run", Exact = true });
    ILocator Cards(string column) => Panel.GetByRole(AriaRole.Region, new() { Name = column, Exact = true }).Locator(".card");

    async Task<Batch> OpenBatchAsync(params string[] files)
    {
        foreach (var f in files)
        {
            File.WriteAllText(Path.Combine(scripts, f), "select 1");
            App.Executor.Scripts[f] = new FakeScript { Duration = TimeSpan.FromSeconds(10) };
        }
        App.Executor.Logon = c => new SqlLogon("SQL01", "Sales", "EXAMPLE\\hank");
        var b = App.Host.Batches.NewBatch();
        b = App.Host.Batches.UpdateSettings(b.Id, new BatchSettings
        {
            Folder = scripts, ConfirmBeforeRun = true,
            Target = new() { Server = "sql01", Database = "Sales", Auth = Auth.Integrated },
        })!;
        await Page.GetByRole(AriaRole.Navigation, new() { Name = "Batches" }).Locator($"[role=option]:has-text('{b.Name}')").ClickAsync();
        return b;
    }

    async Task ExpectConfirmAsync(int scripts)
    {
        await Expect(Dialog.Locator("dd")).ToHaveTextAsync(["sql01", "Sales", "EXAMPLE\\hank", scripts.ToString()]);
    }

    Task ClickAsync(string button) => Dialog.GetByRole(AriaRole.Button, new() { Name = button, Exact = true }).ClickAsync();

    [Fact]
    public async Task Run_asks_first_and_Cancel_starts_nothing()
    {
        await OpenBatchAsync("a.sql", "b.sql");

        await RunButton.ClickAsync();
        await ExpectConfirmAsync(2);
        await ClickAsync("Cancel");
        await Expect(Dialog).ToHaveCountAsync(0);
        await Expect(RunButton).ToBeEnabledAsync();
        Assert.Empty(App.Executor.Calls);

        await RunButton.ClickAsync();
        await ExpectConfirmAsync(2);
        await ClickAsync("Run");
        await Expect(Cards("Running")).ToHaveCountAsync(2);
    }

    [Fact]
    public async Task Rerun_and_Rerun_all_failed_ask_first_with_their_script_count()
    {
        var b = await OpenBatchAsync("a.sql", "b.sql", "c.sql");
        foreach (var f in new[] { "a.sql", "b.sql" })
            App.Executor.Scripts[f] = new FakeScript { Duration = TimeSpan.FromSeconds(10), Status = ScriptStatus.Failed };
        App.Host.Batches.Run(b.Id);
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Run" }).ClickAsync();
        await Expect(Cards("Running")).ToHaveCountAsync(3);
        App.Time.Advance(TimeSpan.FromSeconds(10));
        await Expect(Cards("Failed / Cancelled")).ToHaveCountAsync(2);

        await Header.GetByRole(AriaRole.Button, new() { Name = "Rerun all failed" }).ClickAsync();
        await ExpectConfirmAsync(2);
        await ClickAsync("Cancel");
        await Expect(Dialog).ToHaveCountAsync(0);
        await Expect(Cards("Failed / Cancelled")).ToHaveCountAsync(2);
        Assert.Equal(3, App.Executor.Calls.Count);

        await Cards("Succeeded").ClickAsync(new() { Button = MouseButton.Right });
        await Page.GetByRole(AriaRole.Menuitem, new() { Name = "Rerun" }).ClickAsync();
        await ExpectConfirmAsync(1);
        await ClickAsync("Run");
        await Expect(Cards("Running")).ToHaveCountAsync(1);
        Assert.Equal(4, App.Executor.Calls.Count);
    }

    [Fact]
    public async Task A_rerun_during_the_run_asks_first_too()
    {
        var b = await OpenBatchAsync("a.sql", "b.sql");
        App.Executor.Scripts["a.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(5), Status = ScriptStatus.Failed };
        App.Executor.Scripts["b.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(60) };
        App.Host.Batches.Run(b.Id);
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Run" }).ClickAsync();
        await Expect(Cards("Running")).ToHaveCountAsync(2);
        App.Time.Advance(TimeSpan.FromSeconds(5));
        await Expect(Cards("Failed / Cancelled")).ToHaveCountAsync(1);

        await Header.GetByRole(AriaRole.Button, new() { Name = "Rerun all failed" }).ClickAsync();
        await ExpectConfirmAsync(1);
        await ClickAsync("Run");
        await Expect(Cards("Running")).ToHaveCountAsync(2);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(scripts, recursive: true);
    }
}
