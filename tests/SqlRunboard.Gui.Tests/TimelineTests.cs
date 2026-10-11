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

    [Fact]
    public async Task A_live_run_shows_a_bar_for_each_attempt_in_the_order_of_the_first_start()
    {
        App.Executor.Scripts["a.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(10), Rows = 5 };
        App.Executor.Scripts["b.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(60) };
        App.Executor.Scripts["c.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(10), Status = ScriptStatus.Failed, Error = "boom" };
        App.Executor.Scripts["d.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(60) };
        App.Executor.Scripts["e.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(60) };
        App.Executor.Scripts["f.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(60) };
        var b = NewBatch(2, "a.sql", "b.sql", "c.sql", "d.sql", "e.sql", "f.sql");
        App.Host.Batches.Run(b.Id);
        await OpenTimeline(b.Name);

        await Expect(Panel).ToContainTextAsync("Run started 14:00:00");
        await Expect(Bars("a.sql")).ToHaveCountAsync(1);
        App.Time.Advance(TimeSpan.FromSeconds(10)); // a ends, c starts
        await Expect(Bars("c.sql")).ToHaveCountAsync(1);
        App.Time.Advance(TimeSpan.FromSeconds(10)); // c fails, d starts
        await Expect(Bars("d.sql")).ToHaveCountAsync(1);

        await Expect(Names).ToHaveTextAsync(["a.sql", "b.sql", "c.sql", "d.sql", "e.sql", "f.sql"]);
        await Expect(Panel.Locator(".bar")).ToHaveCountAsync(4); // e and f are Queued: rows with no bar
        await Expect(Bars("a.sql").Locator(".i")).ToHaveTextAsync("✓");
        await Expect(Bars("a.sql").Locator(".d")).ToHaveTextAsync("0:10");
        await Expect(Bars("a.sql")).ToHaveAttributeAsync("style", new System.Text.RegularExpressions.Regex("--green"));
        await Expect(Bars("b.sql").Locator(".i")).ToHaveTextAsync("▶");
        await Expect(Bars("b.sql")).ToHaveAttributeAsync("style", new System.Text.RegularExpressions.Regex("--yellow"));
        await Expect(Bars("c.sql").Locator(".i")).ToHaveTextAsync("✕");
        await Expect(Bars("c.sql")).ToHaveAttributeAsync("style", new System.Text.RegularExpressions.Regex("--red"));

        // the concurrency strip: 2 running scripts all the time, and a dashed line at the parallel limit 2
        var strip = Panel.Locator("svg.strip");
        await Expect(strip.Locator("line[stroke-dasharray]")).ToHaveAttributeAsync("y1", "1"); // top = limit + 1 = 3
        await Expect(strip.Locator("path").Last).ToHaveAttributeAsync("d", "M0 3H0V1H1"); // 2 from the start to now: a end and c start at the same time

        // the move commands work on the Queued rows
        await Row("f.sql").ClickAsync(new() { Button = MouseButton.Right });
        await Menu("f.sql").GetByRole(AriaRole.Menuitem, new() { Name = "Move to top" }).ClickAsync();
        await Expect(Names).ToHaveTextAsync(["a.sql", "b.sql", "c.sql", "d.sql", "f.sql", "e.sql"]);

        // a cancelled bar is red with a hatch
        App.Host.Batches.CancelAll(b.Id);
        await Expect(Bars("b.sql")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bhatch\b"));
        await Expect(Bars("b.sql")).ToHaveAttributeAsync("style", new System.Text.RegularExpressions.Regex("--red"));
        await Expect(Bars("b.sql").Locator(".i")).ToHaveTextAsync("✕");
        await Expect(Bars("c.sql")).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bhatch\b"));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(scripts, recursive: true);
    }
}
