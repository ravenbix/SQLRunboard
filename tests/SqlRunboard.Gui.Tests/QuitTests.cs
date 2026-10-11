using System.Net;
using System.Net.Http.Json;
using Microsoft.Playwright;
using SqlRunboard.Core;
using SqlRunboard.Gui;

/// <summary>Quit stops the app: it cancels all scripts, writes the logs, removes the lock file and stops the server.</summary>
public class QuitTests
{
    static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Quit_cancels_all_scripts_removes_the_lock_file_and_stops_the_server()
    {
        await using var app = await TestApp.StartAsync();
        var scripts = Directory.CreateTempSubdirectory("sqlrunboard-scripts-").FullName;
        File.WriteAllText(Path.Combine(scripts, "a.sql"), "select 1");
        app.Executor.Scripts["a.sql"] = new FakeScript { Duration = TimeSpan.FromMinutes(1) };
        var b = app.Host.Batches.NewBatch();
        app.Host.Batches.UpdateSettings(b.Id, new BatchSettings { Folder = scripts });
        app.Host.Batches.Run(b.Id);

        var res = await app.Client().PostAsJsonAsync("/api/quit", new { }, Ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var csv = Path.Combine(app.AppData, "logs", $"{b.Name}_20261009-140000", "summary.csv");
        Assert.Contains("a.sql,1,Cancelled", File.ReadAllText(csv));
        Assert.False(File.Exists(Path.Combine(app.AppData, GuiHost.LockFileName)));
        await app.Host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Directory.Delete(scripts, recursive: true);
    }
}

/// <summary>The Quit button in the page: it asks to save each unsaved batch, and asks for confirmation during a run.</summary>
public class QuitPageTests : AppPageTest
{
    static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    readonly string root = Directory.CreateTempSubdirectory("sqlrunboard-quit-").FullName;

    ILocator Dialog => Page.GetByRole(AriaRole.Dialog);
    Task ClickAsync(string name) => Dialog.GetByRole(AriaRole.Button, new() { Name = name, Exact = true }).ClickAsync();
    Task QuitAsync() => Page.GetByRole(AriaRole.Toolbar, new() { Name = "App" }).GetByRole(AriaRole.Button, new() { Name = "Quit" }).ClickAsync();
    string LockFile => Path.Combine(App.AppData, GuiHost.LockFileName);

    /// <summary>A saved batch with one script, so it is not dirty.</summary>
    Batch SavedBatch(string name, string script)
    {
        File.WriteAllText(Path.Combine(root, script), "select 1");
        var b = App.Host.Batches.NewBatch();
        App.Host.Batches.UpdateSettings(b.Id, new BatchSettings { Folder = root, Filters = script });
        return App.Host.Batches.Save(b.Id, Path.Combine(root, name), overwrite: false)!;
    }

    [Fact]
    public async Task Quit_asks_to_save_each_unsaved_batch_then_stops_the_app()
    {
        var saved = SavedBatch("deploy", "a.sql");
        App.Host.Batches.SetInclude(saved.Id, "a.sql", false); // dirty
        App.Host.Batches.NewBatch(); // Untitled 2: never saved
        await OpenAsync(Page, App.Host.BaseUrl);

        await QuitAsync();
        await Expect(Dialog).ToContainTextAsync("Save deploy?");
        await ClickAsync("Save");
        await Expect(Dialog).ToContainTextAsync("Save Untitled 2?");
        await ClickAsync("Don't save");

        await Expect(Dialog).ToContainTextAsync("SQLRunboard stopped");
        Assert.Contains("\"include\": false", File.ReadAllText(saved.File!));
        Assert.False(File.Exists(LockFile));
        await App.Host.WaitForShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task Save_on_a_never_saved_batch_opens_Save_as_and_then_quits()
    {
        App.Host.Batches.NewBatch();
        await OpenAsync(Page, App.Host.BaseUrl);

        await QuitAsync();
        await ClickAsync("Save");
        await Dialog.GetByLabel("File name").FillAsync(Path.Combine(root, "nightly"));
        await ClickAsync("Save");

        await Expect(Dialog).ToContainTextAsync("SQLRunboard stopped");
        Assert.True(File.Exists(Path.Combine(root, "nightly.sqlrunboard.json")));
    }

    [Fact]
    public async Task Cancel_on_a_save_question_keeps_the_app_running()
    {
        App.Host.Batches.NewBatch();
        await OpenAsync(Page, App.Host.BaseUrl);

        await QuitAsync();
        await ClickAsync("Cancel");

        await Expect(Dialog).ToHaveCountAsync(0);
        Assert.True(File.Exists(LockFile));
    }

    [Fact]
    public async Task During_a_run_Quit_asks_for_confirmation_and_then_cancels_all_scripts()
    {
        var b = SavedBatch("load", "a.sql");
        App.Executor.Scripts["a.sql"] = new FakeScript { Duration = TimeSpan.FromMinutes(1) };
        App.Host.Batches.Run(b.Id);
        await OpenAsync(Page, App.Host.BaseUrl);

        await QuitAsync();
        await Expect(Dialog).ToContainTextAsync("Close SQLRunboard?");
        await ClickAsync("Keep open");
        await Expect(Dialog).ToHaveCountAsync(0);
        Assert.True(File.Exists(LockFile));

        await QuitAsync();
        await ClickAsync("Cancel all and close");

        await Expect(Dialog).ToContainTextAsync("SQLRunboard stopped");
        var csv = Directory.GetFiles(Path.Combine(root, "logs"), "summary.csv", SearchOption.AllDirectories).Single();
        Assert.Contains("a.sql,1,Cancelled", File.ReadAllText(csv));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(root, recursive: true);
    }
}
