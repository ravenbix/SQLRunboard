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
}
