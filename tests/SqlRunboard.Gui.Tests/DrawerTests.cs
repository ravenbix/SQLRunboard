using Microsoft.Playwright;
using SqlRunboard.Core;

public class DrawerTests : AppPageTest
{
    readonly string scripts = Directory.CreateTempSubdirectory("sqlrunboard-scripts-").FullName;

    ILocator Panel => Page.Locator("#panel");
    ILocator Drawer => Page.Locator("#drawer");
    ILocator Msgs => Drawer.Locator(".msgs");
    ILocator Card(string path) => Panel.Locator($".card:has-text('{path}')");
    ILocator DrawerButton(string name) => Drawer.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
    ILocator Side(string name) => Page.GetByRole(AriaRole.Navigation, new() { Name = "Batches" }).Locator($"[role=option]:has-text('{name}')");
    // The batch events can come after the click on the side list, so open the Run tab by hand.
    async Task OpenRun(string name) { await Side(name).ClickAsync(); await Page.GetByRole(AriaRole.Tab, new() { Name = "Run" }).ClickAsync(); }

    Batch RunBatch(int limit, params string[] files)
    {
        foreach (var f in files) File.WriteAllText(Path.Combine(scripts, f), "select 1");
        var b = App.Host.Batches.NewBatch();
        App.Host.Batches.UpdateSettings(b.Id, new BatchSettings { Folder = scripts, ParallelLimit = limit });
        return App.Host.Batches.Run(b.Id)!;
    }

    [Fact]
    public async Task Selecting_a_script_opens_the_drawer_beside_the_board_with_one_live_stream_for_all_attempts()
    {
        App.Executor.Scripts["a.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(10), Messages = [new(SqlMessageKind.Info, "hello")], Status = ScriptStatus.Failed, Error = "line 2 (part 1): boom" };
        App.Executor.Scripts["b.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(60) };
        var b = RunBatch(2, "a.sql", "b.sql");
        await OpenRun(b.Name);
        await Card("a.sql").ClickAsync();

        await Expect(Msgs).ToContainTextAsync("hello");
        var panel = (await Panel.BoundingBoxAsync())!;
        var drawer = (await Drawer.BoundingBoxAsync())!;
        Assert.True(panel.X + panel.Width <= drawer.X + 1, "the drawer is beside the board, not over it");

        App.Time.Advance(TimeSpan.FromSeconds(10));
        await Expect(Msgs.Locator(".Error")).ToContainTextAsync("line 2 (part 1): boom");

        await DrawerButton("Rerun").ClickAsync(); // b.sql still runs, so the page posts the rerun at once
        await Expect(Msgs.Locator(".sep")).ToHaveCountAsync(2);
        await Expect(Msgs.Locator(".sep").Nth(1)).ToContainTextAsync("Attempt 2");
        await Expect(Msgs.Locator(".Info")).ToHaveTextAsync([new System.Text.RegularExpressions.Regex("hello$"), new System.Text.RegularExpressions.Regex("hello$")]);
        await DrawerButton("Cancel").ClickAsync();
        await Expect(Card("a.sql").Locator(".r2")).ToContainTextAsync("Cancelled");
    }

    [Fact]
    public async Task The_move_buttons_of_the_drawer_move_a_Queued_script()
    {
        App.Executor.Scripts["a.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(60) };
        var b = RunBatch(1, "a.sql", "b.sql", "c.sql");
        await OpenRun(b.Name);
        await Card("c.sql").ClickAsync();

        await DrawerButton("Move to top").ClickAsync();
        await Expect(Panel.GetByRole(AriaRole.Region, new() { Name = "Queued" }).Locator(".card .r1 span:not(.st)")).ToHaveTextAsync(["c.sql", "b.sql"]);
    }

    [Fact]
    public async Task The_table_uses_the_same_selection_and_drawer_as_the_board()
    {
        App.Executor.Scripts["a.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(60) };
        var b = RunBatch(1, "a.sql", "b.sql");
        await OpenRun(b.Name);
        await Card("b.sql").ClickAsync();
        await Expect(Drawer.Locator(".dh .t")).ToHaveTextAsync("○b.sql");

        await Panel.GetByRole(AriaRole.Group, new() { Name = "View" }).GetByRole(AriaRole.Button, new() { Name = "Table" }).ClickAsync();
        await Expect(Drawer.Locator(".dh .t")).ToHaveTextAsync("○b.sql");
        await Panel.Locator("tr:has-text('a.sql')").ClickAsync();
        await Expect(Drawer.Locator(".dh .t")).ToHaveTextAsync("▶a.sql");
    }

    [Fact]
    public async Task An_attempt_with_more_than_10000_messages_names_the_earlier_ones_in_run_log()
    {
        App.Executor.Scripts["a.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(5), Messages = [.. Enumerable.Range(1, BatchList.MaxMessages + 5).Select(i => new SqlMessage(SqlMessageKind.Info, $"m{i}"))] };
        var b = RunBatch(1, "a.sql");
        await OpenRun(b.Name);
        await Card("a.sql").ClickAsync();
        await Expect(Msgs).ToContainTextAsync("5 earlier messages are in run.log");
        await Expect(Msgs.Locator(".Info").First).ToContainTextAsync("m6");

        await Page.ReloadAsync();
        await OpenRun(b.Name);
        await Card("a.sql").ClickAsync();
        await Expect(Msgs).ToContainTextAsync("5 earlier messages are in run.log");
        await Expect(Msgs.Locator(".Info")).ToHaveCountAsync(BatchList.MaxMessages);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(scripts, recursive: true);
    }
}
