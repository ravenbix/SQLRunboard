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

// Single instance: a server already runs for this user, so open the browser to it and stop.
if (await GuiHost.FindRunningAsync(appData) is { } running)
{
    Open(running);
    return 0;
}

await using var host = await GuiHost.StartAsync(new GuiHostOptions(appData, new SqlServerExecutor(), TimeProvider.System));
Open(host.StartUrl);
await host.WaitForShutdownAsync(); // Ctrl+C stops the server
return 0;

void Open(Uri url)
{
    Console.WriteLine($"SQLRunboard: {url}");
    // UseShellExecute opens the default browser: start on Windows, open on macOS, xdg-open on Linux.
    if (openBrowser) Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
}
