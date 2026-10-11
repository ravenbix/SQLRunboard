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

    [Fact]
    public async Task The_chart_fits_the_run_to_the_width_and_a_rerun_makes_the_axis_longer()
    {
        App.Executor.Scripts["b.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(100) };
        App.Executor.Scripts["c.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(40) };
        var b = NewBatch(2, "a.sql", "b.sql", "c.sql"); // a.sql ends at once
        App.Host.Batches.Run(b.Id);
        await OpenTimeline(b.Name);
        var lastTick = Panel.Locator(".axis span").Last;

        await Expect(Bars("c.sql")).ToHaveCountAsync(1);
        App.Time.Advance(TimeSpan.FromSeconds(40)); // c ends
        await Expect(Bars("c.sql").Locator(".i")).ToHaveTextAsync("✓");
        await Expect(Bars("b.sql").Locator(".d")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("^0:4")); // the Running bar grows live
        App.Time.Advance(TimeSpan.FromSeconds(60)); // b ends: the run is complete
        await Expect(Bars("b.sql").Locator(".i")).ToHaveTextAsync("✓");

        await Expect(Panel.Locator(".axis span").First).ToHaveTextAsync("0:00");
        await Expect(lastTick).ToHaveTextAsync("1:40");
        var chart = (await Row("b.sql").BoundingBoxAsync())!;
        var bar = (await Bars("b.sql").BoundingBoxAsync())!;
        Assert.InRange(bar.X + bar.Width, chart.X + chart.Width - 1, chart.X + chart.Width + 1); // the full run fits the width
        Assert.InRange((await Bars("a.sql").BoundingBoxAsync())!.Width, 2, 2.5); // 0 s: the minimum width
        await Expect(Bars("b.sql")).ToHaveAttributeAsync("title", new System.Text.RegularExpressions.Regex("Start 14:00:00\nEnd 14:01:40\nDuration 1:40$"));

        await Row("c.sql").ClickAsync(new() { Button = MouseButton.Right });
        await Menu("c.sql").GetByRole(AriaRole.Menuitem, new() { Name = "Rerun" }).ClickAsync();
        await Expect(Bars("c.sql")).ToHaveCountAsync(2); // the rerun stays on the row of its script
        await Expect(Bars("c.sql").Last.Locator(".i")).ToHaveTextAsync("▶");
        App.Time.Advance(TimeSpan.FromSeconds(40));
        await Expect(Bars("c.sql").Last.Locator(".i")).ToHaveTextAsync("✓");
        await Expect(lastTick).ToHaveTextAsync("2:20");
    }

    [Fact]
    public async Task A_click_on_a_bar_opens_the_drawer_at_that_attempt_with_the_selection_of_the_board()
    {
        App.Executor.Scripts["a.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(10), Status = ScriptStatus.Failed, Error = "boom", Messages = [.. Enumerable.Range(1, 100).Select(i => new SqlMessage(SqlMessageKind.Info, $"m{i}"))] };
        var b = NewBatch(1, "a.sql", "b.sql");
        App.Host.Batches.Run(b.Id);
        await OpenTimeline(b.Name);
        App.Time.Advance(TimeSpan.FromSeconds(10));
        await Expect(Bars("a.sql").Locator(".i")).ToHaveTextAsync("✕");
        App.Host.Batches.Rerun(b.Id, "a.sql");
        await Expect(Bars("a.sql")).ToHaveCountAsync(2);
        App.Time.Advance(TimeSpan.FromSeconds(10));
        await Expect(Bars("a.sql").Last.Locator(".i")).ToHaveTextAsync("✕");

        var drawer = Page.Locator("#drawer");
        await Bars("a.sql").First.ClickAsync();
        await Expect(drawer.Locator(".dh .t")).ToHaveTextAsync("✕a.sql");
        await Expect(drawer.Locator("#att-1")).ToBeInViewportAsync(); // the drawer would show the end of attempt 2 without the scroll
        await Expect(drawer.Locator("#att-2")).Not.ToBeInViewportAsync();
        await Expect(Panel.Locator(".tlg > .c.issel .tnum")).ToHaveTextAsync("a.sql");

        await Row("b.sql").ClickAsync();
        await Expect(drawer.Locator(".dh .t")).ToHaveTextAsync("✓b.sql");
        await Panel.GetByRole(AriaRole.Group, new() { Name = "View" }).GetByRole(AriaRole.Button, new() { Name = "Board" }).ClickAsync();
        await Expect(Panel.Locator(".card.issel")).ToContainTextAsync("b.sql");
    }

    ILocator Toggle(string name) => Panel.GetByRole(AriaRole.Checkbox, new() { Name = name });

    [Fact]
    public async Task Columns_shows_a_table_with_a_Gantt_column_and_the_strip_and_axis_in_its_header()
    {
        App.Executor.Scripts["b.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(40) };
        var b = NewBatch(1, "a.sql", "b.sql", "c.sql"); // a.sql ends at once
        App.Host.Batches.Run(b.Id);
        await OpenTimeline(b.Name);
        await Expect(Bars("b.sql")).ToHaveCountAsync(1);

        // off (default): the script names and the chart
        await Expect(Toggle("Columns")).Not.ToBeCheckedAsync();
        await Expect(Panel.Locator(".tlg > .c[data-path='a.sql']")).ToHaveTextAsync(["✓a.sql"]);

        await Toggle("Columns").CheckAsync();
        await Expect(Panel.Locator(".tlg > .h")).ToHaveTextAsync(["#", "Status", "Script", "Start", "Elapsed", "Att."]);
        await Expect(Panel.Locator(".tlg > .c[data-path='a.sql']")).ToHaveTextAsync(["", "✓ Succeeded", "a.sql", "14:00:00", "0:00", "1"]);
        await Expect(Panel.Locator(".tlg > .c[data-path='c.sql']")).ToHaveTextAsync(["1", "○ Queued", "c.sql", "", "", "0"]); // not started: 0 attempts
        await Expect(Bars("b.sql")).ToHaveCountAsync(1); // the Gantt column

        // the strip and the axis are in the header of the chart column
        var head = Panel.Locator(".tlg > .striphead");
        await Expect(head.Locator("svg.strip")).ToHaveCountAsync(1);
        await Expect(head.Locator(".axis span").First).ToHaveTextAsync("0:00");
        // Both boxes in one read: the live run rebuilds the panel each second, so two reads can get a removed node.
        var box = await Panel.EvaluateAsync<double[]>("""
            p => { const h = p.querySelector('.tlg > .striphead').getBoundingClientRect(), c = p.querySelector(".tlg > .chart[data-path='a.sql']").getBoundingClientRect();
              return [h.x, h.width, h.bottom, c.x, c.width, c.y]; }
            """);
        Assert.InRange(box[0], box[3] - 1, box[3] + 1);
        Assert.InRange(box[1], box[4] - 1, box[4] + 1);
        Assert.True(box[2] <= box[5] + 1);

        await Toggle("Columns").UncheckAsync();
        await Expect(Panel.Locator(".tlg > .h")).ToHaveCountAsync(1);
    }

    static readonly System.Text.RegularExpressions.Regex Num = new(@"H([\d.e-]+)");

    async Task<double[]> StripXs() => [.. Num.Matches(await Panel.Locator("svg.strip path").Last.GetAttributeAsync("d") ?? "").Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))];

    static void Near(double[] expected, double[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++) Assert.InRange(actual[i], expected[i] - 0.001, expected[i] + 0.001);
    }

    [Fact]
    public async Task Collapse_idle_gaps_cuts_each_gap_over_1_minute_in_the_bars_and_the_strip()
    {
        App.Executor.Scripts["a.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(10) };
        var b = NewBatch(1, "a.sql");
        App.Host.Batches.Run(b.Id);
        await OpenTimeline(b.Name);
        var icons = Bars("a.sql").Locator(".i");
        await Expect(icons).ToHaveTextAsync(["▶"]);
        App.Time.Advance(TimeSpan.FromSeconds(10));  // 0:00-0:10
        await Expect(icons).ToHaveTextAsync(["✓"]);
        App.Time.Advance(TimeSpan.FromSeconds(120)); // idle 2:00: cut
        App.Host.Batches.Rerun(b.Id, "a.sql");
        await Expect(icons).ToHaveTextAsync(["✓", "▶"]);
        App.Time.Advance(TimeSpan.FromSeconds(10));  // 2:10-2:20
        await Expect(icons).ToHaveTextAsync(["✓", "✓"]);
        App.Time.Advance(TimeSpan.FromSeconds(60));  // idle 1:00: not cut
        App.Host.Batches.Rerun(b.Id, "a.sql");
        await Expect(icons).ToHaveTextAsync(["✓", "✓", "▶"]);
        App.Time.Advance(TimeSpan.FromSeconds(10));  // 3:20-3:30
        await Expect(icons).ToHaveTextAsync(["✓", "✓", "✓"]);

        // off (default): the full time to scale, 210 s
        await Expect(Toggle("Collapse idle gaps")).Not.ToBeCheckedAsync();
        await Expect(Panel.Locator(".axis span").Last).ToHaveTextAsync("3:30");
        await Expect(Panel.Locator(".brkl")).ToHaveCountAsync(0);
        Near([0, 10 / 210.0, 130 / 210.0, 140 / 210.0, 200 / 210.0, 1, 1], await StripXs());

        // on: the 2:00 gap shrinks to a break of 5 % of the width; kept time 90 s, D = 90 + 90 * 0.05 / 0.95
        await Toggle("Collapse idle gaps").CheckAsync();
        await Expect(Panel.Locator(".brkl")).ToHaveTextAsync(["⋯ 2:00 idle"]);
        await Expect(Row("a.sql").Locator(".brk")).ToHaveCountAsync(1);
        var d = 90 + 4.5 / 0.95;
        double[] cut = [0, 10 / d, (10 + 4.5 / 0.95) / d, (20 + 4.5 / 0.95) / d, (80 + 4.5 / 0.95) / d, 1, 1];
        Near(cut, await StripXs()); // the strip uses the same breaks
        var lefts = await Bars("a.sql").EvaluateAllAsync<double[]>("els => els.map(e => parseFloat(e.style.left) / 100)");
        Near([cut[0], cut[2], cut[4]], lefts);

        await Toggle("Collapse idle gaps").UncheckAsync();
        await Expect(Panel.Locator(".brkl")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task The_app_settings_keep_both_toggles()
    {
        var b = NewBatch(1, "a.sql");
        App.Host.Batches.Run(b.Id);
        await OpenTimeline(b.Name);
        await Page.RunAndWaitForResponseAsync(() => Toggle("Columns").CheckAsync(), r => r.Url.EndsWith("/api/app-settings"));
        await Page.RunAndWaitForResponseAsync(() => Toggle("Collapse idle gaps").CheckAsync(), r => r.Url.EndsWith("/api/app-settings"));

        await using var context = await Browser.NewContextAsync(); // a new browser profile
        var page = await context.NewPageAsync();
        await OpenAsync(page, App.Host.StartUrl);
        await page.Locator($"[role=option]:has-text('{b.Name}')").ClickAsync();
        await page.GetByRole(AriaRole.Tab, new() { Name = "Run" }).ClickAsync();
        var panel = page.Locator("#panel");
        await panel.GetByRole(AriaRole.Group, new() { Name = "View" }).GetByRole(AriaRole.Button, new() { Name = "Timeline" }).ClickAsync();
        await Expect(panel.GetByRole(AriaRole.Checkbox, new() { Name = "Columns" })).ToBeCheckedAsync();
        await Expect(panel.GetByRole(AriaRole.Checkbox, new() { Name = "Collapse idle gaps" })).ToBeCheckedAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(scripts, recursive: true);
    }
}
