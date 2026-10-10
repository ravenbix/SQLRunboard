using Microsoft.Playwright;
using SqlRunboard.Core;

public class RunLogTests : AppPageTest
{
    readonly string scripts = Directory.CreateTempSubdirectory("sqlrunboard-scripts-").FullName;

    [Fact]
    public async Task A_run_of_an_unsaved_batch_logs_in_app_data_and_a_failed_summary_write_shows_a_warning()
    {
        File.WriteAllText(Path.Combine(scripts, "a.sql"), "select 1");
        File.WriteAllText(Path.Combine(scripts, "b.sql"), "select 1");
        App.Executor.Scripts["a.sql"] = new FakeScript { Duration = TimeSpan.FromSeconds(5) };
        var b = App.Host.Batches.NewBatch();
        App.Host.Batches.UpdateSettings(b.Id, new BatchSettings { Folder = scripts, ParallelLimit = 1 });
        var csv = Path.Combine(App.AppData, "logs", $"{b.Name}_20261009-140000", "summary.csv");

        await Page.GetByRole(AriaRole.Navigation, new() { Name = "Batches" }).Locator($"[role=option]:has-text('{b.Name}')").ClickAsync();
        await Page.Locator("#hdr").GetByRole(AriaRole.Button, new() { Name = "Run", Exact = true }).ClickAsync();
        await Expect(Page.Locator("#panel").GetByRole(AriaRole.Region, new() { Name = "Running", Exact = true })).ToContainTextAsync("a.sql");
        Assert.Contains("a.sql,1,Running", File.ReadAllText(csv));

        File.Delete(csv);
        Directory.CreateDirectory(csv); // the rename fails, as for a file that Excel locks
        App.Time.Advance(TimeSpan.FromSeconds(5));

        await Expect(Page.Locator("#toast")).ToContainTextAsync($"{b.Name}: Warning: cannot write summary.csv");
    }
}
