using Microsoft.Playwright;
using SqlRunboard.Core;
using SqlRunboard.Gui;

/// <summary>A restart: a second server on the same app data folder brings back the side list and the last runs.</summary>
public class RestartTests : AppPageTest
{
    readonly string dir = Directory.CreateTempSubdirectory("sqlrunboard-restart-").FullName;

    Batch Saved(string name, BatchTarget target)
    {
        var folder = Directory.CreateDirectory(Path.Combine(dir, name)).FullName;
        File.WriteAllText(Path.Combine(folder, "1.sql"), "select 1");
        File.WriteAllText(Path.Combine(folder, "2.sql"), "select 1");
        var b = App.Host.Batches.NewBatch();
        App.Host.Batches.UpdateSettings(b.Id, new BatchSettings { Folder = folder, ParallelLimit = 1, Target = target });
        return App.Host.Batches.Save(b.Id, Path.Combine(folder, name), overwrite: false)!;
    }

    [Fact]
    public async Task The_side_list_and_the_last_runs_come_back_after_a_restart()
    {
        var target = new BatchTarget { Server = "sql01", Database = "Sales" };
        var gone = Saved("gone", target);
        var deploy = Saved("deploy", target);
        var changed = Saved("changed", target);
        App.Host.Batches.NewBatch();
        App.Host.Batches.MoveBatch(deploy.Id, MoveTo.Top);
        App.Executor.Scripts["2.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(30) };
        App.Host.Batches.Run(deploy.Id);
        App.Host.Batches.Run(changed.Id);
        await Expect(Page.Locator("[data-batch]")).ToHaveCountAsync(4);
        File.Delete(gone.File!);
        File.WriteAllText(changed.File!, File.ReadAllText(changed.File!).Replace("\"sql01\"", "\"sql02\"")); // changed while the app is stopped

        await using var again = await GuiHost.StartAsync(new GuiHostOptions(App.AppData, App.Executor, App.Time));
        var page = await Context.NewPageAsync();
        await OpenAsync(page, again.StartUrl);

        var side = page.GetByRole(AriaRole.Navigation, new() { Name = "Batches" }).GetByRole(AriaRole.Option);
        await Expect(side).ToHaveCountAsync(3); // the unsaved batch does not come back
        await Expect(side.Nth(0)).ToHaveAttributeAsync("aria-label", new System.Text.RegularExpressions.Regex("^deploy, Failed"));
        await Expect(side.Nth(1)).ToContainTextAsync("gone");
        await Expect(side.Nth(2)).ToContainTextAsync("changed");

        await side.Nth(0).ClickAsync();
        await page.Locator("#tabs").GetByRole(AriaRole.Tab, new() { Name = "Run" }).ClickAsync();
        await Expect(page.Locator("#panel [data-path='2.sql']")).ToHaveAttributeAsync("aria-label", new System.Text.RegularExpressions.Regex("^2.sql, Cancelled, attempt 1"));
        await Expect(page.Locator("#hdr").GetByRole(AriaRole.Button, new() { Name = "Rerun all failed" })).ToBeEnabledAsync();

        await side.Nth(2).ClickAsync();
        await page.Locator("#tabs").GetByRole(AriaRole.Tab, new() { Name = "Run" }).ClickAsync();
        await Expect(page.Locator("#panel")).ToContainTextAsync("The settings changed. Run the batch again.");

        await side.Nth(1).ClickAsync();
        await Expect(page.Locator("#hdr")).ToContainTextAsync("File not found");
        await page.Locator("#hdr").GetByRole(AriaRole.Button, new() { Name = "Remove from list" }).ClickAsync();
        await Expect(side).ToHaveCountAsync(2);
    }
}
