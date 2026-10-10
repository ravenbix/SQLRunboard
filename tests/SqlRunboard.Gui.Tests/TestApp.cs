using System.Collections.Concurrent;
using System.Net;
using SqlRunboard.Core;
using SqlRunboard.Gui;

/// <summary>The real server with the fake executor, the fake clock and a temporary app data folder.</summary>
public sealed class TestApp : IAsyncDisposable
{
    public required GuiHost Host { get; init; }
    public required FakeSqlExecutor Executor { get; init; }
    public required FakeTime Time { get; init; }
    public required string AppData { get; init; }
    /// <summary>The folders that "Open app data folder" asked the OS to show.</summary>
    public required ConcurrentQueue<string> OpenedFolders { get; init; }

    public static async Task<TestApp> StartAsync()
    {
        var appData = Directory.CreateTempSubdirectory("sqlrunboard-test-").FullName;
        var time = new FakeTime();
        var executor = new FakeSqlExecutor(time);
        var opened = new ConcurrentQueue<string>();
        var host = await GuiHost.StartAsync(new GuiHostOptions(appData, executor, time, opened.Enqueue));
        return new TestApp { Host = host, Executor = executor, Time = time, AppData = appData, OpenedFolders = opened };
    }

    /// <summary>An HTTP client that has the auth cookie, as the page has after the start URL.</summary>
    public HttpClient Client()
    {
        var cookies = new CookieContainer();
        cookies.Add(Host.BaseUrl, new Cookie(GuiHost.CookieName, Host.Token));
        return new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = Host.BaseUrl };
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        Directory.Delete(AppData, recursive: true);
    }
}
