using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using SqlRunboard.Core;
using SqlRunboard.Gui;

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
lifetime.MainWindow = new Window
{
    Title = "SQLRunboard",
    Width = 1280,
    Height = 800,
    // No theme package: a bare template that shows the content.
    Template = new FuncControlTemplate<Window>((w, _) => new ContentPresenter { [!ContentPresenter.ContentProperty] = w[!ContentControl.ContentProperty] }),
    Content = new NativeWebView { Source = startUrl },
};
// Quit in the page, Ctrl+C and SIGTERM stop the server: then the window closes too.
host?.WaitForShutdownAsync().ContinueWith(_ => Dispatcher.UIThread.Post(() => lifetime.Shutdown()), TaskScheduler.Default);
var code = lifetime.Start(args);
if (host is not null) host.DisposeAsync().AsTask().GetAwaiter().GetResult();
return code;
