using System.Diagnostics;
using System.Net;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlRunboard.Core;

namespace SqlRunboard.Gui;

/// <param name="AppDataFolder">Settings, lock file and logs of unsaved batches. Created if missing.</param>
/// <param name="OpenFolder">Shows a folder in the OS file manager. Null: the OS default (tests pass a recorder).</param>
public sealed record GuiHostOptions(string AppDataFolder, ISqlExecutor Executor, TimeProvider Time, Action<string>? OpenFolder = null);

public sealed record IncludeRequest(string Path, bool Include);
/// <param name="Files">A file filter such as <c>*.sqlrunboard.json</c>: the answer then also lists those files. Null: folders only.</param>
public sealed record FolderRequest(string? Path, string? Files = null);
public sealed record PathRequest(string Path);
/// <param name="Path">Null: Save to the current file. Otherwise Save as.</param>
public sealed record SaveRequest(string? Path, bool Overwrite);

/// <summary>The local web server: Kestrel on 127.0.0.1, a random port, a random token. See docs/SPEC.md "GUI host".</summary>
public sealed class GuiHost : IAsyncDisposable
{
    public const string CookieName = "sqlrunboard";
    /// <summary>In the app data folder. JSON <c>{"url","token"}</c>. Shared by sqlrunboard and sqlrunboard-desktop.</summary>
    public const string LockFileName = "sqlrunboard.lock";

    readonly WebApplication app;
    readonly string lockPath;

    GuiHost(WebApplication app, Uri baseUrl, string token, string lockPath) { this.app = app; BaseUrl = baseUrl; Token = token; this.lockPath = lockPath; }

    public Uri BaseUrl { get; }
    public string Token { get; }
    /// <summary>The URL to open. The first request changes the token into a cookie.</summary>
    public Uri StartUrl => new(BaseUrl, "/?token=" + Token);

    public static async Task<GuiHost> StartAsync(GuiHostOptions options)
    {
        Directory.CreateDirectory(options.AppDataFolder);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(k => k.Listen(IPAddress.Loopback, 0));
        builder.Configuration["AllowedHosts"] = "127.0.0.1;localhost"; // the built-in host filtering answers 400 to other hosts
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(options.Executor);
        builder.Services.AddSingleton(options.Time);
        builder.Services.AddSingleton<BatchList>();
        builder.Services.ConfigureHttpJsonOptions(j => j.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        var app = builder.Build();
        app.Use(RejectForeignRequests);
        app.Use(RequireToken(token));
        var files = new EmbeddedFileProvider(typeof(GuiHost).Assembly, "SqlRunboard.Gui.wwwroot");
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });

        // Server to page: SSE. Page to server: POST with a JSON body, one route per command.
        app.MapGet("/events", (BatchList list, CancellationToken ct) => TypedResults.ServerSentEvents(Events(list, ct)));
        app.MapPost("/api/batches", (BatchList list) => list.NewBatch());
        app.MapPost("/api/batches/{id:int}/settings", (int id, BatchSettings settings, BatchList list) =>
            !settings.IsValid ? Results.BadRequest()
            : list.UpdateSettings(id, settings) is { } b ? Results.Ok(b) : Results.NotFound());
        app.MapPost("/api/batches/{id:int}/include", (int id, IncludeRequest r, BatchList list) =>
            list.SetInclude(id, r.Path, r.Include) is { } b ? Results.Ok(b) : Results.NotFound());
        app.MapPost("/api/batches/open", (PathRequest r, BatchList list) => FileResult(() => list.Open(r.Path)));
        app.MapPost("/api/batches/{id:int}/save", (int id, SaveRequest r, BatchList list) => FileResult(() => list.Save(id, r.Path, r.Overwrite)));
        app.MapPost("/api/batches/{id:int}/reload", (int id, BatchList list) => FileResult(() => list.Reload(id)));
        app.MapPost("/api/batches/{id:int}/remove", (int id, BatchList list) => list.Remove(id) ? Results.Ok(new { }) : Results.NotFound());
        app.MapPost("/api/app-data/open", (GuiHostOptions o) =>
        {
            // UseShellExecute on a folder opens the file manager: Explorer, Finder or xdg-open.
            (o.OpenFolder ?? (f => Process.Start(new ProcessStartInfo(f) { UseShellExecute = true })))(o.AppDataFolder);
            return Results.Ok(new { });
        });
        // The browser has no folder or file picker that gives a path, so the page browses the folders here.
        app.MapPost("/api/folders", (FolderRequest r) =>
        {
            var path = string.IsNullOrWhiteSpace(r.Path) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : Path.GetFullPath(r.Path);
            if (File.Exists(path)) path = Path.GetDirectoryName(path)!; // a batch file: start in its folder
            if (!Directory.Exists(path)) return Results.NotFound();
            var options = new EnumerationOptions { IgnoreInaccessible = true, MatchType = MatchType.Simple };
            var folders = Directory.EnumerateDirectories(path, "*", options).Order(FileScan.NaturalOrder).Select(d => new { name = Path.GetFileName(d), path = d });
            var files = r.Files is null ? [] : Directory.EnumerateFiles(path, r.Files, options).Order(FileScan.NaturalOrder).Select(f => new { name = Path.GetFileName(f), path = f });
            return Results.Ok(new { path, parent = Path.GetDirectoryName(path), folders, files });
        });

        await app.StartAsync();
        var host = new GuiHost(app, new Uri(app.Urls.First()), token, Path.Combine(options.AppDataFolder, LockFileName));
        host.WriteLock();
        return host;
    }

    /// <summary>A batch file command: the batch, or a problem whose detail the page shows. 409: the file changed on disk.</summary>
    static IResult FileResult(Func<Batch?> command)
    {
        try { return command() is { } b ? Results.Ok(b) : Results.NotFound(); }
        catch (BatchFileChangedException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return Results.Problem(e.Message, statusCode: StatusCodes.Status404NotFound); }
        catch (Exception e) when (e is ArgumentException or InvalidDataException) { return Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Results.Problem(e.Message, statusCode: StatusCodes.Status500InternalServerError); }
    }

    record LockFile(Uri Url, string Token);

    /// <summary>The start URL of the server in the lock file, or null when no server answers there with that token (a stale or missing lock file).</summary>
    public static async Task<Uri?> FindRunningAsync(string appDataFolder)
    {
        try
        {
            var path = Path.Combine(appDataFolder, LockFileName);
            if (!File.Exists(path)) return null;
            if (JsonSerializer.Deserialize<LockFile>(File.ReadAllText(path), JsonSerializerOptions.Web) is not { Url: { } url, Token: { } token }) return null;
            var startUrl = new Uri(url, "/?token=" + Uri.EscapeDataString(token));
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(3) };
            using var res = await http.GetAsync(startUrl);
            return res.StatusCode == HttpStatusCode.Redirect ? startUrl : null; // only our server with this token redirects
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException or UriFormatException)
        {
            return null;
        }
    }

    // ponytail: check-then-write, so two starts in the same instant can both run. Add an OS file lock if that matters.
    void WriteLock()
    {
        File.Delete(lockPath); // a stale file can have other permissions; create it new with mode 600
        var fs = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) fs.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(lockPath, fs);
        JsonSerializer.Serialize(stream, new LockFile(BaseUrl, Token), JsonSerializerOptions.Web);
    }

    static async IAsyncEnumerable<SseItem<object>> Events(BatchList list, [EnumeratorCancellation] CancellationToken ct)
    {
        using var sub = list.Subscribe();
        yield return new SseItem<object>(new { batches = sub.Snapshot }, "snapshot");
        await foreach (var change in sub.Changes.ReadAllAsync(ct))
            yield return new SseItem<object>(change.Data, change.Kind);
    }

    public BatchList Batches => app.Services.GetRequiredService<BatchList>();

    /// <summary>
    /// Other web sites must not use the server: reject an <c>Origin</c> other than our own, and a <c>POST</c> that is not
    /// <c>application/json</c> (a form or <c>text/plain</c> POST is a "simple" cross-site request with no CORS preflight).
    /// </summary>
    static async Task RejectForeignRequests(HttpContext ctx, RequestDelegate next)
    {
        var origin = ctx.Request.Headers.Origin;
        if (origin.Count > 0 && origin != $"{ctx.Request.Scheme}://{ctx.Request.Host}")
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (HttpMethods.IsPost(ctx.Request.Method)
            && !string.Equals(ctx.Request.GetTypedHeaders().ContentType?.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }
        await next(ctx);
    }

    /// <summary>The token in the query string becomes a cookie, then a redirect drops it from the URL. All other requests need the cookie.</summary>
    static Func<HttpContext, RequestDelegate, Task> RequireToken(string token)
    {
        var expected = Encoding.UTF8.GetBytes(token);
        bool Valid(string? s) => s is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(s), expected);

        return async (ctx, next) =>
        {
            if (ctx.Request.Query.TryGetValue("token", out var q))
            {
                if (!Valid(q)) { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
                ctx.Response.Cookies.Append(CookieName, token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/" });
                ctx.Response.Redirect(ctx.Request.PathBase + ctx.Request.Path);
                return;
            }
            if (!Valid(ctx.Request.Cookies[CookieName])) { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
            await next(ctx);
        };
    }

    public Task WaitForShutdownAsync() => app.WaitForShutdownAsync();

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
