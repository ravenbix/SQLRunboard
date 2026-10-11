using System.Net;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using SqlRunboard.Gui;

public class AppSettingsTests
{
    static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    static async Task<JsonElement> SnapshotApp(HttpClient http)
    {
        await using var stream = await http.GetStreamAsync("/events", Ct);
        await using var events = SseParser.Create(stream).EnumerateAsync(Ct).GetAsyncEnumerator(Ct);
        Assert.True(await events.MoveNextAsync());
        return JsonDocument.Parse(events.Current.Data).RootElement.GetProperty("app");
    }

    [Fact]
    public async Task The_app_settings_start_empty()
    {
        await using var app = await TestApp.StartAsync();
        using var http = app.Client();

        Assert.Empty((await SnapshotApp(http)).EnumerateObject());
    }

    [Fact]
    public async Task A_setting_is_kept_in_the_app_data_folder_after_a_restart()
    {
        await using var app = await TestApp.StartAsync();
        using (var http = app.Client())
        {
            var post = await http.PostAsync("/api/app-settings", new StringContent("""{"view":"table"}""", Encoding.UTF8, "application/json"), Ct);
            Assert.Equal(HttpStatusCode.NoContent, post.StatusCode);
            await http.PostAsync("/api/app-settings", new StringContent("""{"theme":"Dark"}""", Encoding.UTF8, "application/json"), Ct);
        }
        Assert.True(File.Exists(Path.Combine(app.AppData, "settings.json")));

        await using var again = await GuiHost.StartAsync(new GuiHostOptions(app.AppData, app.Executor, app.Time));
        var cookies = new CookieContainer();
        cookies.Add(again.BaseUrl, new Cookie(GuiHost.CookieName, again.Token));
        using var http2 = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = again.BaseUrl };

        var settings = await SnapshotApp(http2);
        Assert.Equal("table", settings.GetProperty("view").GetString());
        Assert.Equal("Dark", settings.GetProperty("theme").GetString()); // a POST changes only the names it sends
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"table\"")]
    public async Task The_server_accepts_only_a_JSON_object(string json)
    {
        await using var app = await TestApp.StartAsync();
        using var http = app.Client();

        var post = await http.PostAsync("/api/app-settings", new StringContent(json, Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }
}
