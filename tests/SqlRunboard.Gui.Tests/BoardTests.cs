using Microsoft.Playwright;
using SqlRunboard.Core;

public class BoardTests : AppPageTest
{
    readonly string scripts = Directory.CreateTempSubdirectory("sqlrunboard-scripts-").FullName;

    ILocator Panel => Page.Locator("#panel");
    ILocator Header => Page.Locator("#hdr");
    ILocator Tab(string name) => Page.GetByRole(AriaRole.Tab, new() { Name = name });
    ILocator Side(string name) => Page.GetByRole(AriaRole.Navigation, new() { Name = "Batches" }).Locator($"[role=option]:has-text('{name}')");
    ILocator Column(string name) => Panel.GetByRole(AriaRole.Region, new() { Name = name, Exact = true });
    ILocator Cards(string column) => Column(column).Locator(".card .r1 span:not(.st)");

    Batch NewBatch(int limit, params string[] files)
    {
        foreach (var f in files) File.WriteAllText(Path.Combine(scripts, f), "select 1");
        var b = App.Host.Batches.NewBatch();
        return App.Host.Batches.UpdateSettings(b.Id, new BatchSettings { Folder = scripts, ParallelLimit = limit })!;
    }

    void Script(string path, int seconds, ScriptStatus status = ScriptStatus.Succeeded, long? rows = null) =>
        App.Executor.Scripts[path] = new FakeScript { Duration = TimeSpan.FromSeconds(seconds), Status = status, Rows = rows };

    [Fact]
    public async Task Run_shows_the_scripts_on_the_board_until_the_run_ends()
    {
        var b = NewBatch(2, "a.sql", "b.sql", "c.sql", "d.sql");
        Script("a.sql", 10, rows: 5); Script("b.sql", 20); Script("c.sql", 5, ScriptStatus.Failed); Script("d.sql", 1);

        await Side(b.Name).ClickAsync();
        await Expect(Tab("Files")).ToHaveAttributeAsync("aria-selected", "true");
        await Header.GetByRole(AriaRole.Button, new() { Name = "Run", Exact = true }).ClickAsync();

        await Expect(Tab("Run")).ToHaveAttributeAsync("aria-selected", "true");
        await Expect(Cards("Running")).ToHaveTextAsync(["a.sql", "b.sql"]);
        await Expect(Cards("Queued")).ToHaveTextAsync(["c.sql", "d.sql"]);
        await Expect(Header).ToContainTextAsync(b.Name);
        await Expect(Header).ToContainTextAsync("Parallel 2");
        await Expect(Header.GetByRole(AriaRole.Button, new() { Name = "Run", Exact = true })).ToBeDisabledAsync();
        await Expect(Header.Locator(".prog")).ToBeAttachedAsync();
        await Expect(Side(b.Name).Locator(".st")).ToHaveTextAsync("▶");

        App.Time.Advance(TimeSpan.FromSeconds(10));
        await Expect(Cards("Succeeded")).ToHaveTextAsync(["a.sql"]);
        await Expect(Column("Succeeded").Locator(".card .r2 span")).ToHaveTextAsync(["0:10", "attempt 1", "5 rows"]);
        await Expect(Side(b.Name).Locator(".p")).ToHaveTextAsync("1/4");

        App.Time.Advance(TimeSpan.FromSeconds(5));
        await Expect(Cards("Failed / Cancelled")).ToHaveTextAsync(["c.sql"]);
        App.Time.Advance(TimeSpan.FromSeconds(1));
        await Expect(Cards("Succeeded")).ToHaveTextAsync(["d.sql", "a.sql"]);
        App.Time.Advance(TimeSpan.FromSeconds(4));

        await Expect(Cards("Succeeded")).ToHaveTextAsync(["b.sql", "d.sql", "a.sql"]); // the newest at the top
        await Expect(Side(b.Name).Locator(".p")).ToHaveTextAsync("4/4, 1 failed");
        await Expect(Side(b.Name).Locator(".st")).ToHaveTextAsync("✕");
        await Expect(Header.GetByRole(AriaRole.Button, new() { Name = "Run", Exact = true })).ToBeEnabledAsync();
    }

    [Fact]
    public async Task Selecting_a_batch_opens_Files_before_its_first_run_and_Run_after_it()
    {
        var ran = NewBatch(4, "a.sql");
        var fresh = NewBatch(4);
        App.Host.Batches.Run(ran.Id);

        await Side(fresh.Name).ClickAsync();
        await Expect(Tab("Files")).ToHaveAttributeAsync("aria-selected", "true");
        await Side(ran.Name).ClickAsync();
        await Expect(Tab("Run")).ToHaveAttributeAsync("aria-selected", "true");
        await Expect(Side(ran.Name).Locator(".st")).ToHaveTextAsync("✓");
    }

    [Fact]
    public async Task The_run_locks_the_settings_and_the_check_boxes()
    {
        var b = NewBatch(4, "a.sql");
        Script("a.sql", 60);
        App.Host.Batches.Run(b.Id);

        await Side(b.Name).ClickAsync();
        await Tab("Settings").ClickAsync();
        await Expect(Panel.GetByLabel("Parallel limit")).ToBeDisabledAsync();
        await Tab("Files").ClickAsync();
        await Expect(Panel.GetByLabel("Include a.sql")).ToBeDisabledAsync();

        App.Time.Advance(TimeSpan.FromSeconds(60));
        await Expect(Panel.GetByLabel("Include a.sql")).ToBeEnabledAsync();
    }

    [Fact]
    public async Task Cancel_a_script_from_its_card_menu_and_the_rest_with_Cancel_all()
    {
        var b = NewBatch(1, "a.sql", "b.sql", "c.sql");
        Script("a.sql", 60);
        App.Host.Batches.Run(b.Id);
        await Side(b.Name).ClickAsync();

        await Column("Queued").Locator(".card:has-text('b.sql')").ClickAsync(new() { Button = MouseButton.Right });
        await Page.GetByRole(AriaRole.Menuitem, new() { Name = "Cancel" }).ClickAsync();
        await Expect(Cards("Failed / Cancelled")).ToHaveTextAsync(["b.sql"]);
        await Expect(Column("Failed / Cancelled").Locator(".card .r2")).ToContainTextAsync("Cancelled");

        await Header.GetByRole(AriaRole.Button, new() { Name = "Cancel all" }).ClickAsync();
        await Expect(Cards("Failed / Cancelled")).ToHaveCountAsync(3);
        await Expect(Cards("Running")).ToHaveCountAsync(0);
        await Expect(Header.GetByRole(AriaRole.Button, new() { Name = "Run", Exact = true })).ToBeEnabledAsync();
    }

    [Fact]
    public async Task The_drawer_of_a_script_that_did_not_start_says_why()
    {
        var b = NewBatch(1, "a.sql", "b.sql");
        App.Host.Batches.UpdateSettings(b.Id, b.Settings with { StopOnFirstFailure = true });
        Script("a.sql", 5, ScriptStatus.Failed);
        App.Host.Batches.Run(b.Id);
        await Side(b.Name).ClickAsync();

        App.Time.Advance(TimeSpan.FromSeconds(5));
        await Expect(Cards("Failed / Cancelled")).ToHaveTextAsync(["a.sql", "b.sql"]); // a script with 0 attempts has no end time: it goes at the bottom
        await Column("Failed / Cancelled").Locator(".card:has-text('b.sql')").ClickAsync();
        await Expect(Page.Locator("#drawer .msgs")).ToContainTextAsync("Not started: the queue stopped");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(scripts, recursive: true);
    }
}
