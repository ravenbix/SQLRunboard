using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using SqlRunboard.Core;
using SqlRunboard.Gui;
using System.Text.Json;

const string Usage = "Usage: sqlrunboard-desktop [--app-data <folder>]";

var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sqlrunboard");
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--app-data" && i + 1 < args.Length) appData = args[++i];
    else { Console.Error.WriteLine(Usage); return 2; }
}

// Same lock file as the browser executable: if a server already runs, open a window to it.
// Block here, not with await: Avalonia must stay on the main thread (macOS).
GuiHost? host = null;
var startUrl = GuiHost.FindRunningAsync(appData).GetAwaiter().GetResult();
if (startUrl is null)
{
    host = GuiHost.StartAsync(new GuiHostOptions(appData, new SqlServerExecutor(), TimeProvider.System)).GetAwaiter().GetResult();
    startUrl = host.StartUrl;
}

var lifetime = new ClassicDesktopStyleApplicationLifetime { Args = args, ShutdownMode = ShutdownMode.OnMainWindowClose };
AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithLifetime(lifetime);

// The page loads from 127.0.0.1 with the token, as in the browser, so all server checks stay in force.
var webView = new NativeWebView { Source = startUrl };
var window = new Window
{
    Title = "SQLRunboard",
    Width = 1280,
    Height = 800,
    // No theme package: a bare template that shows the content.
    Template = new FuncControlTemplate<Window>((w, _) => new ContentPresenter { [!ContentPresenter.ContentProperty] = w[!ContentControl.ContentProperty] }),
    Content = webView,
};
lifetime.MainWindow = window;

// The close event does what Quit in the page does. The page asks to save and to confirm; when the app has stopped,
// it sends "stopped" (or the server stop below closes the window). Without a page or a server, the window closes at once.
var stopped = false;
void CloseNow() { stopped = true; lifetime.Shutdown(); }
window.Closing += async (_, e) =>
{
    if (stopped) return;
    e.Cancel = true;
    // Reuse path: if the other process's server is gone, the page cannot quit, so do not ask.
    var noServer = host is null ? "!('connected' in document.documentElement.dataset)" : "false";
    try { if ((await webView.InvokeScript($"typeof quit !== 'function' || {noServer} ? 'none' : (quit(), 'asked')"))?.Contains("asked") == true) return; }
    catch (Exception) { } // no page loaded
    CloseNow();
};

// The page asks for the native dialogs: { id, cmd, path, name }. The answer is shellReply(id, real path or null).
var batchFiles = new FilePickerFileType("SQLRunboard batch") { Patterns = ["*.sqlrunboard.json"] };
webView.WebMessageReceived += async (_, e) =>
{
    using var json = JsonDocument.Parse(e.Body ?? "{}");
    var m = json.RootElement;
    var cmd = m.GetProperty("cmd").GetString();
    if (cmd == "stopped") { CloseNow(); return; }
    string? Text(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    string? result = null;
    try
    {
        var sp = window.StorageProvider;
        var path = Text("path");
        var dir = path is null || !Path.IsPathRooted(path) ? null : Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        var start = dir is null ? null : await sp.TryGetFolderFromPathAsync(dir);
        result = cmd switch
        {
            "folder" => (await sp.OpenFolderPickerAsync(new() { SuggestedStartLocation = start })).FirstOrDefault()?.TryGetLocalPath(),
            "open" => (await sp.OpenFilePickerAsync(new() { SuggestedStartLocation = start, FileTypeFilter = [batchFiles] })).FirstOrDefault()?.TryGetLocalPath(),
            "saveAs" => (await sp.SaveFilePickerAsync(new() { SuggestedStartLocation = start, SuggestedFileName = Text("name"), FileTypeChoices = [batchFiles] }))?.TryGetLocalPath(),
            _ => null,
        };
    }
    finally { await webView.InvokeScript($"shellReply({m.GetProperty("id").GetInt32()}, {JsonSerializer.Serialize(result)})"); }
};

// Quit in the page, Ctrl+C and SIGTERM stop the server: then the window closes too.
host?.WaitForShutdownAsync().ContinueWith(_ => Dispatcher.UIThread.Post(CloseNow), TaskScheduler.Default);
var code = lifetime.Start(args);
if (host is not null) host.DisposeAsync().AsTask().GetAwaiter().GetResult();
return code;
