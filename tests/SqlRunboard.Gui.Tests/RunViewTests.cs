using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using SqlRunboard.Core;
using SqlRunboard.Gui;

public class RunViewTests : AppPageTest
{
    readonly string scripts = Directory.CreateTempSubdirectory("sqlrunboard-scripts-").FullName;

    ILocator Panel => Page.Locator("#panel");
    ILocator View(string name) => Panel.GetByRole(AriaRole.Group, new() { Name = "View" }).GetByRole(AriaRole.Button, new() { Name = name });
    ILocator Side(string name) => Page.GetByRole(AriaRole.Navigation, new() { Name = "Batches" }).Locator($"[role=option]:has-text('{name}')");
    // The batch events can come after the click on the side list, so open the Run tab by hand.
    async Task OpenRun(string name) { await Side(name).ClickAsync(); await Page.GetByRole(AriaRole.Tab, new() { Name = "Run" }).ClickAsync(); }
    ILocator Rows => Panel.Locator("table tbody tr");

    Batch RunningBatch()
    {
        foreach (var f in new[] { "a.sql", "b.sql", "c.sql" }) File.WriteAllText(Path.Combine(scripts, f), "select 1");
        App.Executor.Scripts["a.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(10), Rows = 5 };
        App.Executor.Scripts["b.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(10) };
        var b = App.Host.Batches.NewBatch();
        App.Host.Batches.UpdateSettings(b.Id, new BatchSettings { Folder = scripts, ParallelLimit = 1 });
        return App.Host.Batches.Run(b.Id)!;
    }

    [Fact]
    public async Task The_table_shows_the_run_in_queue_order()
    {
        var b = RunningBatch();
        await OpenRun(b.Name);
        await View("Table").ClickAsync();

        await Expect(Panel.Locator("table thead th")).ToHaveTextAsync(["#", "Status", "Script", "Start", "Elapsed", "Attempts", "Rows"]);
        await Expect(Rows.Locator("td:nth-child(3)")).ToHaveTextAsync(["a.sql", "b.sql", "c.sql"]);
        await Expect(Rows.Locator("td:nth-child(1)")).ToHaveTextAsync(["", "1", "2"]); // the queue position only for Queued scripts
        await Expect(Rows.Locator("td:nth-child(6)")).ToHaveTextAsync(["1", "0", "0"]); // a script that did not start has 0 attempts

        App.Time.Advance(TimeSpan.FromSeconds(10));
        await Expect(Rows.Nth(0)).ToContainTextAsync("Succeeded");
        await Expect(Rows.Nth(0).Locator("td")).ToHaveTextAsync(new Regex[] { new("^$"), new("Succeeded"), new("^a.sql$"), new(@"^\d\d:\d\d:\d\d$"), new("^0:10$"), new("^1$"), new("^5$") });
    }

    [Fact]
    public async Task Board_and_table_use_the_same_selection()
    {
        var b = RunningBatch();
        await OpenRun(b.Name);
        await Panel.Locator(".card:has-text('b.sql')").ClickAsync();
        await View("Table").ClickAsync();

        await Expect(Panel.Locator("tr.issel td:nth-child(3)")).ToHaveTextAsync("b.sql");
        await Panel.Locator("tr:has-text('c.sql')").ClickAsync();
        await View("Board").ClickAsync();
        await Expect(Panel.Locator(".card.issel")).ToContainTextAsync("c.sql");
    }

    [Fact]
    public async Task The_right_click_menu_works_on_a_table_row()
    {
        var b = RunningBatch();
        await OpenRun(b.Name);
        await View("Table").ClickAsync();
        await Panel.Locator("tr:has-text('c.sql')").ClickAsync(new() { Button = MouseButton.Right });

        var menu = Page.GetByRole(AriaRole.Menu, new() { Name = "c.sql" });
        await Expect(menu).ToBeVisibleAsync();
        await Expect(menu.GetByRole(AriaRole.Menuitem, new() { Name = "Move to top" })).ToBeEnabledAsync(); // c.sql is Queued
        await Expect(Panel.Locator("tr.issel td:nth-child(3)")).ToHaveTextAsync("c.sql");
    }

    [Fact]
    public async Task The_Run_tab_opens_the_last_view_also_after_a_restart()
    {
        var b = RunningBatch();
        await OpenRun(b.Name);
        await View("Table").ClickAsync();
        await Expect(View("Table")).ToHaveAttributeAsync("aria-pressed", "true");

        var file = Path.Combine(App.AppData, AppSettings.FileName);
        for (var i = 0; i < 50 && !(File.Exists(file) && JsonNode.Parse(File.ReadAllText(file))?["view"]?.GetValue<string>() == "table"); i++) await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal("table", JsonNode.Parse(File.ReadAllText(file))?["view"]?.GetValue<string>());

        await using var again = await GuiHost.StartAsync(new GuiHostOptions(App.AppData, App.Executor, App.Time));
        var b2 = again.Batches.NewBatch();
        again.Batches.UpdateSettings(b2.Id, new BatchSettings { Folder = scripts });
        again.Batches.Run(b2.Id);
        await using var context = await Browser.NewContextAsync(); // a new browser profile: no local storage of the first page
        var page = await context.NewPageAsync();
        await OpenAsync(page, again.StartUrl);
        await page.Locator($"[role=option]:has-text('{b2.Name}')").ClickAsync();
        await page.GetByRole(AriaRole.Tab, new() { Name = "Run" }).ClickAsync();

        await Expect(page.Locator("#panel table")).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Table" })).ToHaveAttributeAsync("aria-pressed", "true");
    }

    [Fact]
    public async Task A_late_answer_to_Run_does_not_undo_the_newer_state_from_the_server()
    {
        File.WriteAllText(Path.Combine(scripts, "a.sql"), "select 1");
        App.Executor.Scripts["a.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(10) };
        var b = App.Host.Batches.NewBatch();
        App.Host.Batches.UpdateSettings(b.Id, new BatchSettings { Folder = scripts });
        var answer = new TaskCompletionSource();
        await Page.RouteAsync("**/api/batches/*/run", async route =>
        {
            var res = await route.FetchAsync(); // the server state when the run started: a.sql Running
            await answer.Task;
            await route.FulfillAsync(new() { Response = res });
        });
        await Side(b.Name).ClickAsync();

        await Page.Locator("#hdr").GetByRole(AriaRole.Button, new() { Name = "Run", Exact = true }).ClickAsync();
        await Expect(Side(b.Name)).ToContainTextAsync("0/1"); // from the event stream
        App.Time.Advance(TimeSpan.FromSeconds(10));
        await Expect(Side(b.Name)).ToContainTextAsync("1/1");
        answer.SetResult();

        await Expect(Page.GetByRole(AriaRole.Tab, new() { Name = "Run" })).ToHaveAttributeAsync("aria-selected", "true"); // the page has the answer
        await Expect(Side(b.Name)).ToContainTextAsync("1/1");
        await Expect(Panel.Locator(".card:has-text('a.sql')")).ToContainTextAsync("attempt 1");
        await Expect(Panel.GetByRole(AriaRole.Region, new() { Name = "Succeeded", Exact = true }).Locator(".card")).ToHaveCountAsync(1);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(scripts, recursive: true);
    }
}
