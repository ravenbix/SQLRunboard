using Microsoft.Playwright;
using SqlRunboard.Core;

public class TimelineTests : AppPageTest
{
    readonly string scripts = Directory.CreateTempSubdirectory("sqlrunboard-scripts-").FullName;

    // A fixed zone for the local times: the fake clock starts 2026-10-09T12:00Z = 14:00 in Berlin (summer time).
    public override BrowserNewContextOptions ContextOptions() => new() { TimezoneId = "Europe/Berlin" };

    ILocator Panel => Page.Locator("#panel");
    ILocator Side(string name) => Page.GetByRole(AriaRole.Navigation, new() { Name = "Batches" }).Locator($"[role=option]:has-text('{name}')");
    ILocator Names => Panel.Locator(".tlg > .c[data-k] .tnum");
    ILocator Row(string path) => Panel.Locator($".tlg > .chart[data-path='{path}']");
    ILocator Bars(string path) => Row(path).Locator(".bar");
    ILocator Menu(string path) => Page.GetByRole(AriaRole.Menu, new() { Name = path });

    // The batch events can come after the click on the side list, so open the Run tab by hand.
    async Task OpenTimeline(string name)
    {
        await Side(name).ClickAsync();
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Run" }).ClickAsync();
        await Panel.GetByRole(AriaRole.Group, new() { Name = "View" }).GetByRole(AriaRole.Button, new() { Name = "Timeline" }).ClickAsync();
    }

    Batch NewBatch(int limit, params string[] files)
    {
        foreach (var f in files) File.WriteAllText(Path.Combine(scripts, f), "select 1");
        var b = App.Host.Batches.NewBatch();
        return App.Host.Batches.UpdateSettings(b.Id, new BatchSettings { Folder = scripts, ParallelLimit = limit })!;
    }

    [Fact]
    public async Task Before_the_first_run_the_timeline_shows_the_Queued_rows_and_No_run_yet()
    {
        var b = NewBatch(1, "a.sql", "b.sql", "c.sql");
        await OpenTimeline(b.Name);

        await Expect(Panel).ToContainTextAsync("No run yet");
        await Expect(Names).ToHaveTextAsync(["a.sql", "b.sql", "c.sql"]);
        await Expect(Panel.Locator(".bar")).ToHaveCountAsync(0);

        await Row("c.sql").ClickAsync(new() { Button = MouseButton.Right });
        await Menu("c.sql").GetByRole(AriaRole.Menuitem, new() { Name = "Move to top" }).ClickAsync();
        await Expect(Names).ToHaveTextAsync(["c.sql", "a.sql", "b.sql"]);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(scripts, recursive: true);
    }
}
