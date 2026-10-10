using System.Diagnostics;
using SqlRunboard.Core;
using SqlRunboard.Gui;

const string Usage = "Usage: sqlrunboard [--app-data <folder>] [--no-browser]";

var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sqlrunboard");
var openBrowser = true;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--app-data" when i + 1 < args.Length: appData = args[++i]; break;
        case "--no-browser": openBrowser = false; break;
        default: Console.Error.WriteLine(Usage); return 2;
    }
}

var time = TimeProvider.System;
// ponytail: the fake executor runs until the real SQL Server adapter (ticket #7) replaces it.
await using var host = await GuiHost.StartAsync(new GuiHostOptions(appData, new FakeSqlExecutor(time), time));
Console.WriteLine($"SQLRunboard: {host.StartUrl}");
// UseShellExecute opens the default browser: start on Windows, open on macOS, xdg-open on Linux.
if (openBrowser) Process.Start(new ProcessStartInfo(host.StartUrl.ToString()) { UseShellExecute = true });
await host.WaitForShutdownAsync(); // Ctrl+C stops the server
return 0;
