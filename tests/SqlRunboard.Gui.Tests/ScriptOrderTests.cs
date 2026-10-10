using Microsoft.Playwright;
using SqlRunboard.Core;

/// <summary>docs/SPEC.md "Script order" in the browser: move commands, Sort by file name, New and Missing, side list order.</summary>
public class ScriptOrderTests : AppPageTest
{
    readonly string scripts = Directory.CreateTempSubdirectory("sqlrunboard-order-").FullName;

    ILocator Panel => Page.Locator("#panel");
    ILocator Tab(string name) => Page.GetByRole(AriaRole.Tab, new() { Name = name });
    ILocator Side => Page.GetByRole(AriaRole.Navigation, new() { Name = "Batches" }).GetByRole(AriaRole.Option);
    ILocator FileNames => Panel.Locator("tbody tr td.tnum");
    ILocator Row(string path) => Panel.Locator($"tbody tr:has(td.tnum:text-is('{path}'))");
    ILocator Queued => Panel.GetByRole(AriaRole.Region, new() { Name = "Queued", Exact = true }).Locator(".card .r1 span:not(.st)");

    void Touch(params string[] files) { foreach (var f in files) File.WriteAllText(Path.Combine(scripts, f), "select 1"); }

    Batch NewBatch(int limit, params string[] files)
    {
        Touch(files);
        var b = App.Host.Batches.NewBatch();
        return App.Host.Batches.UpdateSettings(b.Id, new BatchSettings { Folder = scripts, ParallelLimit = limit })!;
    }

    IReadOnlyList<Batch> ServerBatches() { using var sub = App.Host.Batches.Subscribe(); return sub.Snapshot; }

    [Fact]
    public async Task The_Files_tab_moves_and_sorts_the_start_order_and_shows_New_and_Missing_until_the_save()
    {
        var b = NewBatch(4, "a.sql", "b.sql", "c.sql");
        var file = App.Host.Batches.Save(b.Id, Path.Combine(scripts, "x"), overwrite: false)!.File!;
        Touch("d.sql");
        File.Delete(Path.Combine(scripts, "a.sql"));
        App.Host.Batches.UpdateSettings(b.Id, b.Settings with { Folder = "." });
        await Side.First.ClickAsync();
        await Expect(Tab("Files")).ToHaveAttributeAsync("aria-selected", "true");

        await Expect(FileNames).ToHaveTextAsync(["a.sql", "b.sql", "c.sql", "d.sql"]);
        await Expect(Row("a.sql")).ToContainTextAsync("Missing on disk. The next save removes it.");
        await Expect(Row("d.sql")).ToContainTextAsync("New");
        await Expect(Row("b.sql")).Not.ToContainTextAsync("New");

        await Row("c.sql").ClickAsync();
        await Panel.GetByRole(AriaRole.Button, new() { Name = "Move to top" }).ClickAsync();
        await Expect(FileNames).ToHaveTextAsync(["c.sql", "a.sql", "b.sql", "d.sql"]);
        await Row("c.sql").PressAsync("Alt+End");
        await Expect(FileNames).ToHaveTextAsync(["a.sql", "b.sql", "d.sql", "c.sql"]);
        await Row("c.sql").PressAsync("Alt+ArrowUp");
        await Expect(FileNames).ToHaveTextAsync(["a.sql", "b.sql", "c.sql", "d.sql"]);
        await Row("a.sql").ClickAsync();
        await Row("a.sql").PressAsync("Alt+ArrowDown");
        await Expect(FileNames).ToHaveTextAsync(["b.sql", "a.sql", "c.sql", "d.sql"]);
        Assert.Equal(["b.sql", "a.sql", "c.sql", "d.sql"], ServerBatches().Single().Scripts.Select(s => s.Path));

        await Row("d.sql").ClickAsync();
        await Page.Keyboard.PressAsync("Alt+Home");
        await Expect(FileNames).ToHaveTextAsync(["d.sql", "b.sql", "a.sql", "c.sql"]);
        await Panel.GetByRole(AriaRole.Button, new() { Name = "Sort by file name" }).ClickAsync();
        await Expect(FileNames).ToHaveTextAsync(["a.sql", "b.sql", "c.sql", "d.sql"]);

        await Page.Keyboard.PressAsync("ControlOrMeta+s");
        await Expect(FileNames).ToHaveTextAsync(["b.sql", "c.sql", "d.sql"]);
        await Expect(Panel.Locator(".badge")).ToHaveCountAsync(0);
        Assert.DoesNotContain("a.sql", File.ReadAllText(file));
    }

    [Fact]
    public async Task During_a_run_the_card_menu_and_the_keys_move_Queued_scripts_for_that_run_only()
    {
        var b = NewBatch(1, "a.sql", "b.sql", "c.sql", "d.sql");
        App.Executor.Scripts["a.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(60) };
        App.Host.Batches.Run(b.Id);
        await Side.First.ClickAsync();
        await Expect(Queued).ToHaveTextAsync(["b.sql", "c.sql", "d.sql"]);

        await Queued.Filter(new() { HasText = "d.sql" }).ClickAsync(new() { Button = MouseButton.Right });
        await Page.GetByRole(AriaRole.Menuitem, new() { Name = "Move to top" }).ClickAsync();
        await Expect(Queued).ToHaveTextAsync(["d.sql", "b.sql", "c.sql"]);

        await Panel.GetByRole(AriaRole.Button, new() { Name = "Table" }).ClickAsync();
        var row = Panel.Locator("tbody tr:has-text('c.sql')");
        await row.ClickAsync();
        await row.PressAsync("Alt+Home");
        await Expect(Panel.Locator("tbody tr td.tnum")).ToHaveTextAsync(["a.sql", "c.sql", "d.sql", "b.sql"]); // queue order

        Assert.Equal(["a.sql", "b.sql", "c.sql", "d.sql"], ServerBatches().Single().Scripts.Select(s => s.Path)); // the start order stays
    }

    [Fact]
    public async Task The_side_list_moves_batches_with_the_same_keys()
    {
        App.Host.Batches.NewBatch(); App.Host.Batches.NewBatch(); App.Host.Batches.NewBatch();
        await Expect(Side).ToHaveCountAsync(3);

        await Side.Nth(2).ClickAsync();
        await Side.Nth(2).PressAsync("Alt+ArrowUp");
        await Expect(Side.Locator(".n")).ToHaveTextAsync(["Untitled 1 • (unsaved)", "Untitled 3 • (unsaved)", "Untitled 2 • (unsaved)"]);
        await Side.Nth(0).ClickAsync();
        await Side.Nth(0).PressAsync("Alt+End");
        await Expect(Side.Locator(".n")).ToHaveTextAsync(["Untitled 3 • (unsaved)", "Untitled 2 • (unsaved)", "Untitled 1 • (unsaved)"]);

        await OpenAsync(Page, App.Host.BaseUrl); // the server keeps the order
        await Expect(Side.Locator(".n")).ToHaveTextAsync(["Untitled 3 • (unsaved)", "Untitled 2 • (unsaved)", "Untitled 1 • (unsaved)"]);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(scripts, recursive: true);
    }
}
