using System.Diagnostics;
using System.Net;

public class ExecutableTests
{
    static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public void The_browser_executable_has_no_native_UI_dependency()
    {
        var deps = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sqlrunboard.deps.json"));

        Assert.DoesNotContain("Avalonia", deps, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WebView", deps, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Starts_the_server_in_the_given_app_data_folder_without_a_browser_and_prints_the_start_url()
    {
        var appData = Path.Combine(Path.GetTempPath(), "sqlrunboard-exe-" + Guid.NewGuid().ToString("N"));
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            ArgumentList = { Path.Combine(AppContext.BaseDirectory, "sqlrunboard.dll"), "--app-data", appData, "--no-browser" },
            RedirectStandardOutput = true,
        };
        using var exe = Process.Start(psi)!;
        try
        {
            var line = await exe.StandardOutput.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct) ?? "";
            var url = new Uri(line[line.IndexOf("http")..]);

            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            var res = await http.GetAsync(url, Ct);

            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
            Assert.Equal("127.0.0.1", url.Host);
            Assert.True(Directory.Exists(appData));
        }
        finally
        {
            exe.Kill(entireProcessTree: true);
            await exe.WaitForExitAsync(Ct);
            if (Directory.Exists(appData)) Directory.Delete(appData, recursive: true);
        }
    }

    [Fact]
    public async Task Ctrl_C_stops_the_server_and_removes_the_lock_file()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "No SIGINT for a child process on Windows. Quit and Ctrl+C share the ApplicationStopping handler (QuitTests).");
        var appData = Directory.CreateTempSubdirectory("sqlrunboard-exe-").FullName;
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            ArgumentList = { Path.Combine(AppContext.BaseDirectory, "sqlrunboard.dll"), "--app-data", appData, "--no-browser" },
            RedirectStandardOutput = true,
        };
        using var exe = Process.Start(psi)!;
        try
        {
            await exe.StandardOutput.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.True(File.Exists(Path.Combine(appData, "sqlrunboard.lock")));

            using (var kill = Process.Start("kill", ["-INT", exe.Id.ToString()])) await kill.WaitForExitAsync(Ct); // Ctrl+C
            await exe.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

            Assert.Equal(0, exe.ExitCode);
            Assert.False(File.Exists(Path.Combine(appData, "sqlrunboard.lock")));
        }
        finally
        {
            if (!exe.HasExited) exe.Kill(entireProcessTree: true);
            Directory.Delete(appData, recursive: true);
        }
    }

    [Fact]
    public async Task A_second_start_opens_the_running_server_and_stops()
    {
        await using var app = await TestApp.StartAsync();
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            ArgumentList = { Path.Combine(AppContext.BaseDirectory, "sqlrunboard.dll"), "--app-data", app.AppData, "--no-browser" },
            RedirectStandardOutput = true,
        };
        using var exe = Process.Start(psi)!;
        try
        {
            var output = await exe.StandardOutput.ReadToEndAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
            await exe.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

            Assert.Equal(0, exe.ExitCode);
            Assert.Equal($"SQLRunboard: {app.Host.StartUrl}", output.Trim());
        }
        finally
        {
            if (!exe.HasExited) exe.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task An_unknown_argument_prints_the_usage_and_exits_with_code_2()
    {
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            ArgumentList = { Path.Combine(AppContext.BaseDirectory, "sqlrunboard.dll"), "--bogus" },
            RedirectStandardError = true,
        };
        using var exe = Process.Start(psi)!;
        try
        {
            var error = await exe.StandardError.ReadToEndAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
            await exe.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

            Assert.Equal(2, exe.ExitCode);
            Assert.Contains("Usage: sqlrunboard", error);
        }
        finally
        {
            if (!exe.HasExited) exe.Kill(entireProcessTree: true);
        }
    }
}
