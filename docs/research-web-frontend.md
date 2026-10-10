# Research: a web front end for the SQLRunboard GUI

Status: research. Date: 2026-10-10. Question: build the GUI as a .NET 10 back end with an HTML front end instead of Avalonia?

## Summary

- A web front end fits the spec. The shared core stays in .NET. SQL, credentials and file access stay in the .NET process. The browser gets only status events and sends only commands.
- The prototype is already plain HTML and JS with no dependencies. A plain HTML+JS front end can reuse it almost as it is. Blazor needs a rewrite in Razor.
- Blazor WebAssembly does not fit. Its code runs in the browser and cannot reach the database or the file system directly.
- The weak points of a browser UI are native dialogs: the folder picker, the "save before close" dialog and the close-during-run dialog. A desktop shell around the HTML solves them.
- The .NET desktop shells for HTML are weak on Linux. Photino has had no release since 2025-01-23 and does not target .NET 10. MAUI and WebView2 have no Linux. Electron.NET is active and supports .NET 10, but it ships Chromium and Node.
- A localhost web server that runs SQL with the user's Windows or Kerberos logon is a security risk. It needs a launch token, Host and Origin checks, and JSON-only endpoints.

## Recommendation

1. Keep one .NET 10 process. Host ASP.NET Core (Kestrel) on `127.0.0.1` with a random port. Serve the HTML, CSS and JS from embedded resources.
2. Use plain HTML+JS. Start from `prototype-gui.html`. Replace its fake core with calls to the server.
3. Send live events with Server-Sent Events (`TypedResults.ServerSentEvents`, new in ASP.NET Core 10). Send commands (run, cancel, move, rerun) as `POST` requests with a JSON body. Use SignalR only if you want one channel for both directions.
4. Protect the server: a random token per launch, a Host header check, an Origin check, and JSON-only `POST` endpoints. See "Security".
5. Phase 1: open the UI in the default browser. Type or paste folder paths. Do the file-system browsing on the server.
6. Phase 2, if native dialogs become necessary: put the same page in a desktop shell. Avalonia with `NativeWebView` (MIT, release 12.1.0 on 2026-08-15) keeps Avalonia in the stack and has Linux support. Electron.NET is the alternative. Do not start on Photino until it ships a .NET 10 release.

Reasons:

- The web UI already exists as a prototype. An Avalonia UI must be written again in XAML.
- The same server and events can feed the TUI design and a later remote use.
- The browser path has no native dependencies. It runs on Windows, macOS and Linux with only the .NET runtime.
- The security and dialog costs are known and small. The shell option stays open because the page does not change.

Trade-off to accept: a native Avalonia GUI has no HTTP surface and has native dialogs from day one. If the user does not want a local HTTP server that can run SQL as the logged-on user, keep Avalonia.

## 1. Local web server model

### Hosting

- Kestrel can bind to loopback only. "When `localhost` is specified, Kestrel attempts to bind to both IPv4 and IPv6 loopback interfaces." ([Kestrel endpoints](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0))
- Port `0` gives a dynamic free port. Dynamic binding is not available with `ListenLocalhost`. Use `Listen(IPAddress.Loopback, 0)` and read the port from `IServerAddressesFeature`. ([Kestrel endpoints](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0))
- Kestrel does not validate the `Host` header. Host filtering is off by default. Set `AllowedHosts` to turn it on. ([Host filtering](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/host-filtering?view=aspnetcore-10.0))
- To open the default browser, use `Process.Start` with `UseShellExecute = true`. The default is `false` on .NET (Core). ([UseShellExecute](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.useshellexecute?view=net-10.0)) On Linux the runtime looks for `xdg-open`, `gnome-open` or `kfmclient`. ([Process.Linux.cs](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.Linux.cs)) On macOS it uses `open`. ([Process.OSX.cs](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.OSX.cs))
- Plain HTTP on loopback is a secure context for browser APIs. The Secure Contexts draft treats `127.0.0.0/8` and `::1` as "potentially trustworthy". `localhost` is trustworthy only if the browser follows the localhost name rules. ([Secure Contexts, Editor's Draft 2023-11-10](https://w3c.github.io/webappsec-secure-contexts/)) Use `http://127.0.0.1:<port>` and no TLS certificate.

### Live updates

| Option | Direction | Built in | Notes |
|---|---|---|---|
| Server-Sent Events | Server to browser | Yes. `TypedResults.ServerSentEvents` in ASP.NET Core 10 ([what's new in 10](https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-10.0?view=aspnetcore-10.0)). Browser `EventSource` needs no library. | The browser reconnects by itself. The server can set `retry`. The browser sends `Last-Event-ID` on reconnect ([WHATWG HTML, SSE](https://html.spec.whatwg.org/multipage/server-sent-events.html)). Commands go as separate `POST` requests. |
| WebSockets | Both | Yes, `UseWebSockets` | You write your own message protocol. CORS does not protect WebSockets. You must check `Origin` (`AllowedOrigins`) ([WebSockets](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets?view=aspnetcore-10.0)). |
| SignalR | Both | Yes, server. The browser needs the SignalR JS client. | Microsoft recommends SignalR over raw WebSockets for most apps: transport fallback, an RPC model, no large performance cost ([WebSockets](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets?view=aspnetcore-10.0)). CORS does not apply to its WebSocket transport either ([SignalR security](https://learn.microsoft.com/en-us/aspnet/core/signalr/security?view=aspnetcore-10.0)). |

Fit for SQLRunboard:

- The data flow is mostly one way: status changes, messages, timer ticks. Commands are rare and small. SSE plus `POST` matches this flow and needs no JS library.
- The HTML spec warns about the per-server connection limit when many pages each hold an `EventSource` ([WHATWG HTML, SSE](https://html.spec.whatwg.org/multipage/server-sent-events.html)). Use one `EventSource` per tab for all batches. Do not open one per batch.
- The spec permits "many batches run at the same time". Put a batch id in each event.

### Security

The server runs SQL with the user's Integrated logon (Windows or Kerberos) or Entra token. Any request that reaches the API acts as the user. A web page on another site must not be able to start a run.

Threats and controls:

- Cross-site requests (CSRF). A page from any site can send a "simple" request to `127.0.0.1`. The browser blocks only the read of the response. A request is "simple" if its `Content-Type` is `application/x-www-form-urlencoded`, `multipart/form-data` or `text/plain` ([WHATWG Fetch](https://fetch.spec.whatwg.org/)). Control: accept only `application/json` bodies and require a custom header with the token. Then the browser must send a CORS preflight, and the server does not allow it.
- WebSocket hijack. Browsers do not preflight WebSocket requests and do not obey `Access-Control` headers for them. Browsers do send `Origin`. ([WebSockets](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets?view=aspnetcore-10.0)) Control: check `Origin` against `http://127.0.0.1:<port>`.
- DNS rebinding. An attacker name resolves first to the attacker, then to `127.0.0.1`. The page is then same-origin with the attacker name, and the `Host` header shows the attacker name. Control: turn on host filtering with `AllowedHosts` = `127.0.0.1;localhost` ([Host filtering](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/host-filtering?view=aspnetcore-10.0)).
- Browser help. Local Network Access gates requests from public sites to loopback behind a `"loopback-network"` permission and names DNS rebinding as a reason to check each connection. It is a Draft Community Group Report (2026-08-07), and the draft says Chromium enforces only public-to-local and public-to-loopback requests now ([Local Network Access](https://wicg.github.io/local-network-access/)). I did not verify support in Firefox or Safari. Do not depend on it.
- Token in the URL. Generate a random token at each launch. Open `http://127.0.0.1:<port>/?t=<token>`. The page moves it to memory or a cookie and removes it from the address bar. Note: the URL with its query string goes to the ASP.NET Core request log at `Info` level by default ([SignalR security](https://learn.microsoft.com/en-us/aspnet/core/signalr/security?view=aspnetcore-10.0)). Set the `Microsoft.AspNetCore.Hosting` logger to `Warning`. `EventSource` cannot set headers, so the SSE endpoint must take the token from a cookie or the query string.
- `Origin` is not authentication. It can be faked by non-browser clients ([WebSockets](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets?view=aspnetcore-10.0)). The token is the authentication. `Origin` and `Host` checks stop browser-based attacks.
- Other local users. Loopback is reachable by all processes on the machine. The token stops other users who cannot read the token. Do not write the token to a file that others can read.
- Errors. Do not send exception details that contain connection strings to the browser. SignalR hides hub exception details by default ([SignalR security](https://learn.microsoft.com/en-us/aspnet/core/signalr/security?view=aspnetcore-10.0)). Do the same for the JSON API.

### Single-file and self-contained publish

- Single-file publish works for framework-dependent and self-contained apps. A single-file app is OS and architecture specific. Publish one file for each RID, for example `win-x64`, `linux-x64`, `osx-arm64`. ([Single-file overview](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview))
- Only managed DLLs go into the bundle by default. Set `IncludeNativeLibrariesForSelfExtract` to embed native libraries. Those files are extracted to disk at start, under `$HOME/.net` (Linux, macOS) or `%TEMP%/.net` (Windows). ([Single-file overview](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview))
- `Assembly.Location` returns an empty string in a single-file app. Use `AppContext.BaseDirectory`. Microsoft suggests embedded resources to avoid loose files. ([Single-file overview](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview))
- Embed the web files: add `Microsoft.Extensions.FileProviders.Embedded`, set `GenerateEmbeddedFilesManifest` to `true`, mark the files as `EmbeddedResource`, and serve them with `ManifestEmbeddedFileProvider` ([File providers](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/file-providers?view=aspnetcore-10.0)). Pass the provider to the static files middleware.
- I did not find a Microsoft statement on how a `wwwroot` folder behaves in a single-file bundle. Embedded resources avoid the question.
- Microsoft.Data.SqlClient has native parts on Windows (SNI). Test the single-file build on each OS. I did not verify the exact native files for the current SqlClient version.

## 2. Desktop shells that host HTML

State on 2026-10-10. Release data comes from the GitHub releases API and NuGet.

| Shell | Windows | macOS | Linux | Engine | Last release | .NET 10 |
|---|---|---|---|---|---|---|
| Default browser + Kestrel | Yes | Yes | Yes | The user's browser | n/a (part of .NET 10) | Yes |
| Photino.NET 4.0.16 | Yes | Yes | Yes | WebView2 (Windows), WKWebView (macOS), WebKitGTK (Linux) | 2025-01-23 | No. Package targets `net8.0`, `net9.0` |
| Photino.Blazor 4.0.13 | Yes | Yes | Yes | Same as Photino.NET | 2025-01-23 | No. `net8.0`, `net9.0` |
| Blazor Hybrid in .NET MAUI | Yes (WinUI 3) | Yes (Mac Catalyst) | No | Platform web view | MAUI 10.0.110, 2026-09-22 | Yes |
| Blazor Hybrid in WPF / WinForms | Yes | No | No | WebView2 | Ships with .NET | Yes |
| WebView2 | Yes | No | No | Microsoft Edge (Chromium) | Evergreen runtime | Yes |
| Electron.NET (ElectronNET.Core) 0.6.0 | Yes | Yes | Yes | Bundled Chromium + Node.js | 0.6.0 on 2026-09-07; 0.6.0-pre.165 on 2026-10-01 | Yes. `net6.0`, `net8.0`, `net10.0` |
| webview_csharp 0.13.0 | Yes | Yes | Yes | WebView2, Cocoa WebKit, WebKitGTK | 2025-11-24 | Not checked |
| Avalonia NativeWebView 12.1.0 | Yes | Yes | Yes | WebView2, WKWebView, WPE WebKit or WebKitGTK | 2026-08-15 | Avalonia 12 line; target framework not checked |

### Photino.NET and Photino.Blazor

- Photino uses the OS web view. The README says "WebKit-based" for all OSes ([photino.NET README](https://github.com/tryphotino/photino.NET)). The Photino.Native build steps require the WebView2 runtime on Windows, `webkit2gtk` on Linux and the Cocoa WebKit framework on macOS ([photino.Native README](https://github.com/tryphotino/photino.Native)). So Windows uses WebView2, not WebKit.
- Latest releases: Photino.NET v4.0.16, Photino.Blazor v4.0.13, Photino.Native v4.0.22, all on 2025-01-23 ([photino.NET releases](https://github.com/tryphotino/photino.NET/releases), [photino.Blazor releases](https://github.com/tryphotino/photino.Blazor/releases)).
- The NuGet packages target `net8.0` and `net9.0` only ([Photino.NET on NuGet](https://www.nuget.org/packages/Photino.NET/4.0.16)). Support for .NET 8 and .NET 9 ends on 2026-11-10 ([.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)). A pull request to add `net10.0` is open since July 2026 ([photino.NET #280](https://github.com/tryphotino/photino.NET/issues/280)). A `net9.0` package can load in a .NET 10 app, but Photino does not state support for that.
- The last code commit is from 2025-01-23. On 2026-03-26 the maintainers announced a move to AI-assisted review and triage "in response to evolving time constraints" ([photino.NET README](https://github.com/tryphotino/photino.NET)). An open issue asks "Is this project dead?" ([photino.NET #279](https://github.com/tryphotino/photino.NET/issues/279)). Users in that issue point to forks: PhotinoX ([ivanvoyager/PhotinoX](https://github.com/ivanvoyager/PhotinoX), 45 stars) and InfiniFrame ([InfiniLore/InfiniFrame](https://github.com/InfiniLore/InfiniFrame), v0.62.1 on 2026-09-05). Both are small. I did not evaluate them.
- Photino has native dialogs: `ShowOpenFile`, `ShowOpenFolder`, `ShowSaveFile`, `ShowMessage` ([PhotinoWindow.NET.cs](https://github.com/tryphotino/photino.NET/blob/master/Photino.NET/PhotinoWindow.NET.cs)). This solves the folder picker.
- Linux: Ubuntu 24.04 has no `libwebkit2gtk-4.0`. Version 4.1 is the default there ([photino.Native #133](https://github.com/tryphotino/photino.Native/issues/133), closed 2024-10-10). I did not verify which WebKitGTK version Photino 4.0.x links.

### Blazor Hybrid (.NET MAUI, WPF, Windows Forms)

- In Blazor Hybrid, Razor components run natively in .NET and render to an embedded web view. WebAssembly is not used ([Blazor Hybrid](https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/)).
- MAUI supports Android, iOS, macOS (Mac Catalyst) and Windows (WinUI 3). Tizen support comes from Samsung. Linux is not in the list ([MAUI supported platforms](https://learn.microsoft.com/en-us/dotnet/maui/supported-platforms)). This fails the spec.
- WPF and Windows Forms hosts are Windows only ([Blazor Hybrid](https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/)).
- MAUI release 10.0.110 on 2026-09-22 ([dotnet/maui releases](https://github.com/dotnet/maui/releases)).

### WebView2

- WebView2 uses Microsoft Edge as the engine. It supports Windows 10, Windows 11 and Windows Server 2016 and later, plus Xbox and HoloLens 2. It supports .NET 5 and later. No macOS or Linux ([WebView2 introduction](https://learn.microsoft.com/en-us/microsoft-edge/webview2/)). It fails the spec on its own. Photino and Avalonia use it on Windows.

### Electron.NET

- `ElectronNET.Core` is the current line. It supports .NET 6, 8 and 10, from console apps to ASP.NET Core and Blazor ([Electron.NET README](https://github.com/ElectronNET/Electron.NET)). The `ElectronNET.Core` 0.6.0 package has `net10.0` assets (NuGet metadata).
- Release 0.6.0 on 2026-09-07. Pre-release 0.6.0-pre.165 on 2026-10-01 ([Electron.NET releases](https://github.com/ElectronNET/Electron.NET/releases)). The project is active.
- It needs Node.js 22 or later ([Electron.NET README](https://github.com/ElectronNET/Electron.NET)). Electron is based on Node.js and Chromium ([electron README](https://github.com/electron/electron)), so each app ships a full Chromium. Electron v44.7.0 came out on 2026-10-07.
- Electron gives native dialogs, menus and window close events. The cost is size, a Node toolchain in the build, and a second runtime to patch.

### Other options

- webview_csharp: a binding to the `webview` C library. It uses Edge Chromium on Windows, GTK WebKit on Linux and Cocoa WebKit on macOS. Linux needs `libwebkit2gtk-4.1` ([webview_csharp README](https://github.com/webview/webview_csharp)). Last release v0.13.0 on 2025-11-24. The README says the Windows web view runs in a UWP context that blocks loopback for development unless you run an admin command. I did not test this.
- Avalonia `NativeWebView`: a control that hosts the OS web view inside an Avalonia window. Engines: WebView2 on Windows, WKWebView on macOS, WPE WebKit or WebKitGTK on Linux ([NativeWebView docs](https://docs.avaloniaui.net/accelerate/components/webview/nativewebview)). The repository has an MIT license and release 12.1.0 on 2026-08-15 ([Avalonia.Controls.WebView](https://github.com/AvaloniaUI/Avalonia.Controls.WebView)). The license is MIT, confirmed on 2026-10-10: the `LICENSE` file is MIT, copyright 2026 AvaloniaUI OÜ, and the NuGet package `Avalonia.Controls.WebView` 12.1.0 has the license expression `MIT` ([NuGet](https://www.nuget.org/packages/Avalonia.Controls.WebView/12.1.0)). The WebView was a paid Accelerate control before Avalonia 12. Avalonia made it open source in Avalonia 12 ([blog, 2026-03-11](https://avaloniaui.net/blog/the-avalonia-webview-is-going-open-source), [Avalonia 12](https://avaloniaui.net/blog/avalonia-12)). The docs still sit under "Accelerate", but they state no license key or price. With this control, Avalonia gives the window, the native dialogs and the close events, and the page gives the UI.

## 3. Blazor Server, Blazor WebAssembly or plain HTML+JS

| | Blazor Server | Blazor WebAssembly | Plain HTML+JS |
|---|---|---|---|
| Where UI code runs | In .NET on the server, over a SignalR circuit ([hosting models](https://learn.microsoft.com/en-us/aspnet/core/blazor/hosting-models?view=aspnetcore-10.0)) | In the browser on a WebAssembly .NET runtime | In the browser |
| Direct database access | Yes | No. Only through a server API ([hosting models](https://learn.microsoft.com/en-us/aspnet/core/blazor/hosting-models?view=aspnetcore-10.0)) | No. Only through a server API |
| Code visible to the user | No | Yes. "Code sent to the client can't be protected from inspection and tampering" | Yes (only UI code) |
| Reuse of the prototype | Rewrite in Razor | Rewrite in Razor | Direct reuse |
| Server API to design | No. Components call the core | Yes | Yes |
| Extra | One circuit per tab. Every UI action is a round trip ([hosting models](https://learn.microsoft.com/en-us/aspnet/core/blazor/hosting-models?view=aspnetcore-10.0)). On loopback the latency is small. | Large download. Only a subset of .NET APIs. | No framework. The JS must stay simple. |

- Blazor WebAssembly fails the spec. SQL and secrets must stay in the .NET process.
- Blazor Server works. It keeps all logic in C#. It also works inside Photino.Blazor, Electron.NET and MAUI. But it throws away the prototype, and it needs the same localhost security work because it is a web server with a SignalR endpoint.
- Plain HTML+JS reuses the 70 KB prototype. The prototype already models the full GUI with a fake core and uses `localStorage` in place of the app settings file. The work is to replace the fake core with `fetch` calls and an `EventSource`.

## 4. Fit with the spec

- SQL execution. `Microsoft.Data.SqlClient` runs in the .NET process. The browser never gets a connection string. This matches the spec in all server-side models.
- Secrets.
  - SQL Server logon: the spec says the GUI asks for the password at each run and keeps it in memory. In a web UI the password goes once from the browser to the server in a `POST`. The server keeps it in memory. The server never sends it back. Use loopback only, so the password does not cross a network.
  - Integrated logon: the server process runs as the user. Windows logon or the Kerberos ticket (`kinit`) works as in a native app. The browser does not take part.
  - Entra interactive with MFA: the library opens the system browser for logon. I did not verify how `Microsoft.Data.SqlClient` interactive auth behaves when the app itself runs in a browser tab. Test it.
  - Entra default (Azure CLI, managed identity, environment variables): fully server-side.
- Batch files and folders. The browser cannot list a folder or read a file by path. The server must do the file scan, the `GO` split, the batch JSON load and save, the logs and `summary.csv`. This is the core already.
- File picker problem.
  - `<input type="file">` gives the page the file contents and the name. It does not give the path. The `value` returns `C:\fakepath\` plus the file name ([WHATWG HTML](https://html.spec.whatwg.org/multipage/input.html)). The server needs the path, because the batch folder is relative to the batch file.
  - `showDirectoryPicker()` returns a handle, not an OS path. It is a WICG draft, "not a W3C Standard nor is it on the W3C Standards Track" ([File System Access](https://wicg.github.io/file-system-access/)). It does not solve the problem.
  - Options in browser mode: (a) the user types or pastes the path; (b) the server lists folders and the page shows a simple folder browser; (c) the server opens a native dialog. Option (c) needs a native library in a console app. I did not evaluate such libraries. In a shell (Photino, Electron, Avalonia), use its native dialog.
- Close behavior. The spec asks to save each unsaved batch on close and to confirm a close during a run. A browser tab can only ask the browser for its generic unload prompt. The browser ignores the page text, and it can skip the prompt ([WHATWG HTML, unloading](https://html.spec.whatwg.org/multipage/browsing-the-web.html)). Also, closing the tab does not stop the server process. Decide what a closed tab means: keep runs alive and let the user open the tab again, or stop the server after a time with no client. A desktop shell gives real close events.
- App settings. The spec keeps the side list and view toggles in the app settings file. In the web model the server owns that file. The page reads and writes it through the API. Do not use `localStorage` for it.
- TUI. The TUI uses the core directly. The web model does not change it.

## 5. Recommendation details

Use the local web server with plain HTML+JS, opened in the default browser, as the first GUI. See "Recommendation" at the top.

Do not use:

- Blazor WebAssembly: code runs in the browser. No direct SQL. Code is visible.
- MAUI Blazor Hybrid: no Linux.
- WebView2 alone, WPF or WinForms: Windows only.
- Photino now: no release since 2025-01-23, no .NET 10 target, maintenance unclear. Look again if a .NET 10 release ships.

Keep open:

- Avalonia with `NativeWebView` around the same page, for native dialogs and close events.
- Electron.NET, if the Linux web views give rendering problems. Accept the size and the Node build step.

Open questions to test before you decide:

- Entra interactive MFA from a process whose UI is a browser tab.
- The single-file build with `Microsoft.Data.SqlClient` native parts on each OS.
- What happens to running batches when the user closes the tab.

## Sources

Microsoft Learn and .NET:

- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core): .NET 10 LTS, released 2025-11-11, patch 10.0.12 on 2026-09-08, support ends 2028-11-14. .NET 8 and 9 end 2026-11-10.
- [Kestrel endpoints (ASP.NET Core 10)](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0)
- [Host filtering with Kestrel](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/host-filtering?view=aspnetcore-10.0)
- [WebSockets support in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets?view=aspnetcore-10.0)
- [Security considerations in ASP.NET Core SignalR](https://learn.microsoft.com/en-us/aspnet/core/signalr/security?view=aspnetcore-10.0)
- [What's new in ASP.NET Core 10](https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-10.0?view=aspnetcore-10.0)
- [Blazor hosting models](https://learn.microsoft.com/en-us/aspnet/core/blazor/hosting-models?view=aspnetcore-10.0)
- [Blazor Hybrid](https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/)
- [.NET MAUI supported platforms](https://learn.microsoft.com/en-us/dotnet/maui/supported-platforms)
- [Microsoft Edge WebView2 introduction](https://learn.microsoft.com/en-us/microsoft-edge/webview2/)
- [Single-file deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)
- [File providers in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/file-providers?view=aspnetcore-10.0)
- [ProcessStartInfo.UseShellExecute](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.useshellexecute?view=net-10.0)
- [dotnet/runtime Process.Linux.cs](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.Linux.cs), [Process.OSX.cs](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.OSX.cs)

Projects:

- [tryphotino/photino.NET](https://github.com/tryphotino/photino.NET), [releases](https://github.com/tryphotino/photino.NET/releases), [#279](https://github.com/tryphotino/photino.NET/issues/279), [#280](https://github.com/tryphotino/photino.NET/issues/280), [PhotinoWindow.NET.cs](https://github.com/tryphotino/photino.NET/blob/master/Photino.NET/PhotinoWindow.NET.cs)
- [tryphotino/photino.Blazor releases](https://github.com/tryphotino/photino.Blazor/releases)
- [tryphotino/photino.Native](https://github.com/tryphotino/photino.Native), [#133](https://github.com/tryphotino/photino.Native/issues/133)
- [Photino.NET 4.0.16 on NuGet](https://www.nuget.org/packages/Photino.NET/4.0.16)
- [ivanvoyager/PhotinoX](https://github.com/ivanvoyager/PhotinoX), [InfiniLore/InfiniFrame](https://github.com/InfiniLore/InfiniFrame)
- [ElectronNET/Electron.NET](https://github.com/ElectronNET/Electron.NET), [releases](https://github.com/ElectronNET/Electron.NET/releases)
- [electron/electron](https://github.com/electron/electron)
- [dotnet/maui releases](https://github.com/dotnet/maui/releases)
- [webview/webview_csharp](https://github.com/webview/webview_csharp)
- [AvaloniaUI/Avalonia.Controls.WebView](https://github.com/AvaloniaUI/Avalonia.Controls.WebView), [NativeWebView docs](https://docs.avaloniaui.net/accelerate/components/webview/nativewebview)

Specifications:

- [WHATWG HTML: Server-sent events](https://html.spec.whatwg.org/multipage/server-sent-events.html)
- [WHATWG HTML: input type=file](https://html.spec.whatwg.org/multipage/input.html)
- [WHATWG HTML: unloading documents](https://html.spec.whatwg.org/multipage/browsing-the-web.html)
- [WHATWG Fetch](https://fetch.spec.whatwg.org/)
- [W3C Secure Contexts (Editor's Draft, 2023-11-10)](https://w3c.github.io/webappsec-secure-contexts/)
- [WICG Local Network Access (Draft CG Report, 2026-08-07)](https://wicg.github.io/local-network-access/)
- [WICG File System Access (Draft CG Report, 2025-10-10)](https://wicg.github.io/file-system-access/)
