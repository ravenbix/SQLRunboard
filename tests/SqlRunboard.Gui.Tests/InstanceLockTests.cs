using System.Text.Json;
using SqlRunboard.Gui;

public class InstanceLockTests
{
    [Fact]
    public async Task The_server_writes_a_lock_file_with_the_url_and_the_token_that_only_the_user_can_read()
    {
        await using var app = await TestApp.StartAsync();

        var path = Path.Combine(app.AppData, GuiHost.LockFileName);
        var json = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        Assert.Equal(app.Host.BaseUrl, new Uri(json.GetProperty("url").GetString()!));
        Assert.Equal(app.Host.Token, json.GetProperty("token").GetString());
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task Finds_the_start_url_of_the_running_server()
    {
        await using var app = await TestApp.StartAsync();

        Assert.Equal(app.Host.StartUrl, await GuiHost.FindRunningAsync(app.AppData));
    }

    [Fact]
    public async Task Finds_nothing_without_a_lock_file()
    {
        var appData = Directory.CreateTempSubdirectory("sqlrunboard-test-").FullName;
        try { Assert.Null(await GuiHost.FindRunningAsync(appData)); }
        finally { Directory.Delete(appData, recursive: true); }
    }

    [Fact]
    public async Task A_stale_lock_file_does_not_block_a_start()
    {
        var app = await TestApp.StartAsync();
        var path = Path.Combine(app.AppData, GuiHost.LockFileName);
        var stale = File.ReadAllText(path);
        await app.Host.DisposeAsync(); // the server stops; a killed process leaves its lock file behind
        File.WriteAllText(path, stale);

        Assert.Null(await GuiHost.FindRunningAsync(app.AppData));
        await using var next = await GuiHost.StartAsync(new GuiHostOptions(app.AppData, app.Executor, app.Time));
        Assert.Equal(next.StartUrl, await GuiHost.FindRunningAsync(app.AppData));
        await next.DisposeAsync();
        Directory.Delete(app.AppData, recursive: true);
    }
}
