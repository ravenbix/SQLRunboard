using System.Net;
using System.Net.Http.Json;
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
