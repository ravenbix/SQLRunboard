using System.Net;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
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
public sealed record GuiHostOptions(string AppDataFolder, ISqlExecutor Executor, TimeProvider Time);

public sealed record IncludeRequest(string Path, bool Include);
public sealed record FolderRequest(string? Path);

/// <summary>The local web server: Kestrel on 127.0.0.1, a random port, a random token. See docs/SPEC.md "GUI host".</summary>
public sealed class GuiHost : IAsyncDisposable
{
    public const string CookieName = "sqlrunboard";

    readonly WebApplication app;

    GuiHost(WebApplication app, Uri baseUrl, string token) { this.app = app; BaseUrl = baseUrl; Token = token; }

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
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(options.Executor);
        builder.Services.AddSingleton(options.Time);
        builder.Services.AddSingleton<BatchList>();
        builder.Services.ConfigureHttpJsonOptions(j => j.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        var app = builder.Build();
        app.Use(RequireToken(token));
        var files = new EmbeddedFileProvider(typeof(GuiHost).Assembly, "SqlRunboard.Gui.wwwroot");
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });

        // Server to page: SSE. Page to server: POST with a JSON body, one route per command.
        app.MapGet("/events", (BatchList list, CancellationToken ct) => TypedResults.ServerSentEvents(Events(list, ct)));
        app.MapPost("/api/batches", (BatchList list) => list.NewBatch());
        app.MapPost("/api/batches/{id:int}/settings", (int id, BatchSettings settings, BatchList list) =>
            settings is not { ParallelLimit: >= 1 and <= 64, TimeoutSeconds: null or >= 1, Filters: not null, Target: not null } ? Results.BadRequest()
            : list.UpdateSettings(id, settings) is { } b ? Results.Ok(b) : Results.NotFound());
        app.MapPost("/api/batches/{id:int}/include", (int id, IncludeRequest r, BatchList list) =>
            list.SetInclude(id, r.Path, r.Include) is { } b ? Results.Ok(b) : Results.NotFound());
        // The browser has no folder picker that gives a path, so the page browses the folders here.
        app.MapPost("/api/folders", (FolderRequest r) =>
        {
            var path = string.IsNullOrWhiteSpace(r.Path) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : Path.GetFullPath(r.Path);
            if (!Directory.Exists(path)) return Results.NotFound();
            var folders = Directory.EnumerateDirectories(path, "*", new EnumerationOptions { IgnoreInaccessible = true })
                .Order(FileScan.NaturalOrder).Select(d => new { name = Path.GetFileName(d), path = d });
            return Results.Ok(new { path, parent = Path.GetDirectoryName(path), folders });
        });

        await app.StartAsync();
        return new GuiHost(app, new Uri(app.Urls.First()), token);
    }

    static async IAsyncEnumerable<SseItem<object>> Events(BatchList list, [EnumeratorCancellation] CancellationToken ct)
    {
        using var sub = list.Subscribe();
        yield return new SseItem<object>(new { batches = sub.Snapshot }, "snapshot");
        await foreach (var change in sub.Changes.ReadAllAsync(ct))
            yield return new SseItem<object>(change.Data, change.Kind);
    }

    public BatchList Batches => app.Services.GetRequiredService<BatchList>();

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
