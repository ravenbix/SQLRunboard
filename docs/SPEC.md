# SQLRunboard

Run many T-SQL scripts in parallel against one target, and watch them live.

Status: idea. Decisions from the design sessions on 2026-10-09 and 2026-10-10.

## Purpose

Use SQLRunboard for these jobs:

- Deployment: apply change scripts to a database.
- Data operations: run long loads, rebuilds and maintenance scripts.
- Testing: run many query scripts and compare durations.

## Non-goals

- The app does not keep a deployment journal. It does not record which scripts were applied before, as DbUp or Flyway do. If you run a batch again, all included scripts run again. Make the scripts idempotent, or clear their check boxes.

## Platform

- .NET 10 (LTS) on Windows, macOS and Linux.
- SQL Server client: `Microsoft.Data.SqlClient`.
- One shared core: file scan, `GO` split, queue, execution and events.
- Deliverables:
  - GUI: a web page, served by the app on the local computer, in the default browser. Build it first. See "GUI host".
  - Desktop shell: the same page in a native window. Build it second. See "Desktop shell".
  - TUI: Spectre.Console. It is not in the initial release. See "TUI".
- Executables:
  - `sqlrunboard`: without arguments, it starts the GUI. After the initial release, `sqlrunboard run <file>` starts the TUI.
  - `sqlrunboard-desktop`: the desktop shell.
- App data folder: `Environment.SpecialFolder.LocalApplicationData` plus `sqlrunboard`, in lowercase on all platforms. That is `%LOCALAPPDATA%` on Windows, `~/Library/Application Support` on macOS and `~/.local/share` on Linux. The folder contains the app settings, the lock file and the logs of unsaved batches.

## Distribution

- Publish a self-contained single-file archive for each platform on GitHub Releases: win-x64, osx-arm64, linux-x64 and linux-arm64.
- Each archive contains `sqlrunboard` and `sqlrunboard-desktop`.
- The initial release has no code signing. macOS quarantines downloaded binaries, and Windows SmartScreen shows a warning.

## Batch

A batch is a saved definition in a JSON file. You can commit the file to git, next to the scripts. The file contains no secrets.

| Setting | Default | Notes |
|---|---|---|
| Folder | none | Relative to the batch file, or absolute. See "Batch files". |
| File filters | `*.sql` | Many filters, separated by `;`. |
| Search subfolders | off | |
| Parallel limit | 4 | Range 1 to 64. A limit of 1 gives an ordered run. |
| Stop the queue at the first failure | off | Use it for deployment. |
| Confirm before run | off | Marks a protected target. |
| Command timeout | none | Applies to each script. |
| Target | none | Server, database, logon type, user, password variable, encryption, additional connection-string text. |
| Scripts | none | The start order and the skipped files. See "Script order". |

- `user`: the logon for SQL Server, and the login hint for Entra interactive.
- `passwordVariable`: the name of the environment variable that contains the password, for the TUI.
- Each path in `scripts` is relative to `folder`. It always uses `/`. The app compares paths with a case-sensitive comparison. The same batch file works on Windows, macOS and Linux.

Batch files:

- The file name ends with `.sqlrunboard.json`. The batch name is the file name without `.sqlrunboard.json`.
- An unsaved batch has the name "Untitled N".
- An unsaved batch has no file, so its folder is an absolute path. At the first save, the app changes the folder to a path relative to the batch file. If no relative path is possible, for example on a different Windows drive, the folder stays absolute.

Example:

```json
{
  "version": 1,
  "folder": "scripts",
  "filters": "*.sql",
  "recurse": false,
  "parallelLimit": 4,
  "stopOnFirstFailure": false,
  "confirmBeforeRun": true,
  "timeoutSeconds": null,
  "target": {
    "server": "sql01.example.local",
    "database": "Sales",
    "auth": "Integrated",
    "user": null,
    "passwordVariable": null,
    "encrypt": "Mandatory",
    "trustServerCertificate": false,
    "extraConnectionString": ""
  },
  "scripts": [
    { "path": "001_create_tables.sql", "include": true },
    { "path": "003_backfill.sql", "include": false },
    { "path": "002_add_columns.sql", "include": true }
  ]
}
```

## Target and logon

All scripts in a batch run against one server and one database.

Logon types:

- Integrated: Windows logon on Windows. Kerberos on macOS and Linux. Kerberos needs a valid ticket (`kinit`).
- SQL Server: user name and password.
- Microsoft Entra ID, interactive with MFA.
- Microsoft Entra ID, default: Azure CLI logon, managed identity or environment variables. Use it for CI and service principals.

Encryption settings:

- Encrypt: Mandatory, Optional or Strict.
- Trust server certificate: a check box.
- Additional connection-string text for all other settings.

Passwords:

- GUI: the page asks for the password at the first run or rerun after you open the batch, and sends it to the server. The server keeps the password in memory until you close the batch. The page does not keep it. The page asks again after a logon failure, or after you change the target.
- TUI: see "TUI".

The "Test connection" button shows `@@SERVERNAME` and the database name.

## Scripts

- The app finds files with the folder, the filters and the subfolder setting.
- Before the run, the GUI shows the files in a check-box list. Clear a check box to remove a file from the run. The batch file keeps the cleared check boxes.
- "Run" scans the folder again. A new file goes at the bottom, with its check box set.
- Each script reads its file when it starts, not when the run starts. If the file is missing, the script fails with "File not found".
- The app splits each file on `GO` and `GO n`, and sends each part as a separate command. `GO n` runs the part n times. The app does not support SQLCMD mode.
- `GO` split rules, as in SSMS:
  - `GO` must be alone on its line. Upper and lower case are both valid.
  - Spaces and a `--` comment can follow `GO` or `GO n`.
  - The app ignores `GO` inside `/* */` comments and inside string literals that continue across lines.
- The scripts are independent. The queue starts them in the start order. See "Script order".
- File encoding: the app reads the byte order mark (BOM) for UTF-8 or UTF-16. Without a BOM, it reads the file as UTF-8.

## Script order

- The start order is the order of the `scripts` list in the batch file. The TUI obeys the same order.
- The order is a start order only. It does not make dependencies between scripts.
- File-name order is a natural sort on the relative path: `2_x.sql` comes before `10_x.sql`.
- A new file matches the filters but is not in the list. The app adds it at the bottom, in file-name order. The Files tab marks it "New" until you save.
- A missing file is in the list but not on disk. The GUI shows a warning in the Files tab and removes the entry at the next save. The TUI writes a warning and continues.
- "Sort by file name" in the Files tab resets the order.

Move commands: move up, move down, move to top, move to bottom.

- Before a run: move files in the Files tab. This changes the start order in the batch file.
- During a run: move Queued scripts. The move applies to that run only. It does not change the batch file.
- You move one script at a time.
- Give the commands from the drawer buttons, the right-click menu on a card or a table row, or the keyboard: Alt+↑, Alt+↓, Alt+Home, Alt+End.

## Execution

- Each script gets a new connection. The app closes the connection when the script ends. `USE`, `SET` and temp tables stay inside one script.
- The app adds `Pooling=false` to each connection. Each script has its own application name, so a pool gives no benefit. A pooled connection can also keep the session state of a cancelled script.
- The application name in the connection is `SQLRunboard` plus the script name. A DBA can see each script in `sys.dm_exec_sessions`.
- The app does not add transactions. The scripts control their own transactions.
- The app discards result sets. It shows the rows-affected count.
- An error message shows the line number in the file, not in the `GO` part. Example: `line 214 (part 3)`.
- The server keeps the last 10,000 messages of each attempt in memory and on the page. If an attempt has more messages, the drawer shows "N earlier messages are in run.log". `run.log` keeps all messages.

## Runs and reruns

- A run starts when you click "Run". Each run has its own log folder, and all attempt counts start at 0.
- "Run" queues all included scripts.
- "Run" is disabled while the current run has Queued or Running scripts.
- Rerun and "Rerun all failed" add attempts to the current run. They write to the same log folder.
- "Rerun all failed" reruns all Failed and Cancelled scripts of the current run.
- Reruns ignore the check boxes. A failed script reruns even if you cleared its check box after the run.
- A rerun after all scripts are complete opens the current run again. The bubble changes to yellow, the timeline axis gets longer, and the app writes `summary.csv` again.
- After a restart, the app reads the last run from the newest `summary.csv`. The rerun commands work on that run, and add attempts to its log folder.
- "Confirm before run" also applies to Rerun and "Rerun all failed".

The current run ends when one of these settings changes:

- The target: server, database, logon type, user, encryption or additional connection-string text.
- The file scan: folder, filters or search subfolders.
- After a restart: `server` or `database` in `summary.csv` is different from the target in the batch file. For example, a `git pull` changed the target while the app was stopped.

After the run ends, the rerun commands stay disabled until the next "Run". The Run tab shows "The settings changed. Run the batch again."

Changes to the parallel limit, "Stop the queue at the first failure", the command timeout and "Confirm before run" do not end the run. They apply to the next reruns.

## Failures and reruns

- A failed script stops at its first failed `GO` part.
- A script that reaches the command timeout fails with "Timeout after N s". Cancelled is only for a cancel from the user or a stopped queue.
- The other scripts continue. Exception: if "Stop the queue at the first failure" is on, no new scripts start. The Running scripts continue. The Queued scripts change to Cancelled with the message "Not started: the queue stopped". The setting also applies to reruns.
- A script that did not start has 0 attempts. `summary.csv` has a row for it with attempt 0, status Cancelled and no start time.
- You can rerun any completed script in the current run.
- A rerun starts at the start of the script.
- A rerun reads the file from disk again. You can fix a script and run it again.
- A rerun goes to the bottom of the queue and obeys the parallel limit.
- "Rerun all failed" queues the scripts in the start order of the batch, not in the order in which they failed.
- The log keeps all attempts. The table shows the last attempt and the attempt count.

## Controls

- Cancel one script.
- Cancel all scripts.
- Move a Queued script. See "Script order".
- Optional command timeout.

## Display

For each script:

- Status: Queued, Running, Succeeded, Failed or Cancelled.
- Start time and elapsed time.
- Attempt count.
- Rows affected.
- Messages: `PRINT`, `RAISERROR`, rows affected and errors with line numbers.

For each batch: overall progress (for example, 12 of 40 done, 2 failed).

## GUI host

Decision on 2026-10-10. Research: `research-web-frontend.md`.

- The app is one .NET process. It hosts ASP.NET Core (Kestrel) on `127.0.0.1` with a random port, and opens the page in the default browser.
- The page is plain HTML, CSS and JS, with no libraries. Start from `prototype-gui.html`. The app serves the files from embedded resources.
- Server to page: Server-Sent Events (`TypedResults.ServerSentEvents`). Page to server: `POST` requests with a JSON body.
- Only the server runs SQL and reads or writes files. The page never gets a connection string. The server keeps all batch state, also unsaved changes. If you close the tab, the runs continue. Open the page again to see them.
- Folder: type or paste the path, or select it in a folder list that the server supplies. The browser has no folder picker that gives a path.
- No Blazor, no htmx.

Single instance:

- Only one GUI server runs for each user. `sqlrunboard` and `sqlrunboard-desktop` use the same lock file in the app data folder.
- The lock file contains the URL and the token. Only the current user can read it. On Linux and macOS, use mode 600. On Windows, use the default ACL of the user profile.
- If a server already runs, `sqlrunboard` opens the browser to it and stops. `sqlrunboard-desktop` opens a window to it.

Security. The server runs SQL with the logon of the user, so these controls are mandatory:

- Listen on `127.0.0.1` only.
- Make a random token at each start. Put it in the URL that the app opens.
- On the first request, change the token into an `HttpOnly`, `SameSite=Strict` cookie, and redirect to a URL without the token. The browser history then does not keep the token. `EventSource` sends the cookie, so the cookie also authenticates the SSE stream.
- Reject each request without a valid token or cookie.
- Set `AllowedHosts` to `127.0.0.1` and `localhost`.
- Reject a request that has an `Origin` header for a different origin.
- Accept `POST` requests only with `Content-Type: application/json`.

## Desktop shell

- A separate executable, `sqlrunboard-desktop`. It uses the same core, the same server and the same page. The browser executable keeps no native UI libraries.
- Window: Avalonia 12 with `NativeWebView` (package `Avalonia.Controls.WebView`, MIT). Engines: WebView2 on Windows, WKWebView on macOS, WebKitGTK or WPE WebKit on Linux.
- The window opens the page from the local server with the token. All security controls stay in force.
- The shell adds these functions. The page uses them only when it runs in the shell:
  - A native folder picker that gives the real path.
  - Native file dialogs for "Open" and "Save as" (Avalonia `StorageProvider`). They give the real path on Windows, macOS and Linux.
  - The close event of the window. It does the same as the "Quit" button: it asks you to save each unsaved batch, and during a run it asks for confirmation.
- The page sends requests for native functions to the shell through the JavaScript bridge of `NativeWebView`. In the browser, the page uses the typed path and the in-page dialogs.
- Linux: the computer must have WebKitGTK or WPE WebKit. The browser executable has no such dependency.

## GUI

Layout:

- Side list: all batches. Each batch shows its status bubble, its name and a progress line, for example "12/40, 2 failed". The list keeps the order in which you added the batches. Move a batch with the same commands as the queue.
- Header: the batch name, the target, the parallel limit, the Run, "Cancel all" and "Rerun all failed" buttons, and a progress bar.
- Tabs: Run, Files and Settings. When you select a batch, the Files tab opens if the batch has not run yet. Otherwise the Run tab opens.
- Drawer: the messages for the selected script. It opens inline at the right side and makes the main area narrower. It does not cover the board.

Run tab. A toggle selects the board, the table or the timeline. All three views use the same selection and the same drawer. The Run tab opens the last view that you selected. The app keeps it in its own settings, not in the batch file.

- Board: four columns, Queued, Running, Succeeded, and Failed / Cancelled. A Cancelled card shows the word "Cancelled".
- Card order: Queued in queue order, Running in start order, Succeeded and Failed / Cancelled with the newest at the top.
- A card shows the script name, the elapsed time, the attempt count and the rows affected. A running card has no progress bar.
- Table: queue position (#, for Queued scripts), status, script, start, elapsed, attempts and rows. The table opens in queue order.
- Timeline: a Gantt chart of the run. See "Timeline".

Timeline:

- Data: the current run, updated live. After a restart, the last run from the newest `summary.csv`.
- Rows: one row for each script. Each attempt is a separate bar on the row of its script.
- Row order: the start of the first attempt. Queued scripts go at the bottom, in queue order. A rerun stays on the row of its script.
- Queued scripts: rows with no bar. The move commands work on them.
- Before the first run: the Queued rows and the text "No run yet".
- Axis: elapsed time from the start of the run. The local start time of the run shows above the axis.
- Scale: the chart always fits the full run to the width. The minimum bar width is 2 px.
- Concurrency strip: a step chart above the rows shows the number of running scripts. A dashed line shows the parallel limit.
- Bars: yellow for Running (grows live), green for Succeeded, red for Failed. A Cancelled bar is red with a hatch. Each bar ends with its status icon: ✓, ✕ or ▶. The duration shows on the bar if it fits.
- Tooltip: the local start time, the local end time and the duration.
- Click: selects the script and opens the drawer. A click on an attempt bar also scrolls the drawer to that attempt. A right-click opens the card menu.
- "Columns" toggle: off shows the script names and the chart. On shows a table with a Gantt column: queue position, status, script, start, elapsed, attempts and the chart. The concurrency strip and the axis go in the column header. The default is off.
- "Collapse idle gaps" toggle: an idle gap is a period with no running script. Off shows the full time to scale. On cuts each idle gap longer than 1 minute. A break mark, for example "⋯ 58:12 idle", replaces the gap. The concurrency strip uses the same breaks. The default is off.
- The app keeps the state of both toggles in its own settings.

Files tab: the check-box list in the start order, with the move commands and "Sort by file name".

Settings tab: the batch settings.

Drawer: one message stream for all attempts. A separator line starts each attempt. The header has the Cancel, Rerun and move buttons.

While a batch runs, the app locks the settings and the file check boxes. You can still move Queued scripts.

Status bubbles. Batches and script rows use the same colors. Each color has an icon.

| Color | Icon | Meaning |
|---|---|---|
| Grey | ○ | Not run yet, or queued. |
| Yellow | ▶ | Running. |
| Green | ✓ | The last run finished, and all scripts succeeded. |
| Red | ✕ | The last run finished, and one or more scripts failed or were cancelled. |

Batch commands, in the top bar:

- New batch (Ctrl+N): adds an unsaved batch "Untitled N", and opens its Settings tab.
- Open (Ctrl+O): adds a batch file to the side list. It accepts only `*.sqlrunboard.json` files.
- Save (Ctrl+S): writes the batch file. For an unsaved batch, it does the same as "Save as".
- Save as (Ctrl+Shift+S): writes the batch to a new file. If you do not type the `.sqlrunboard.json` extension, the app adds it.
- Remove from list.
- Open app data folder.

In the browser, "Open" and "Save as" work as the Folder field does. Type or paste a full path, or select it in a folder list that the server supplies. For "Open", the list shows only `*.sqlrunboard.json` files. In the desktop shell, both commands use the native file dialogs.

Behavior:

- The side list stays after a restart. The app saves the paths of the batch files in its settings.
- If a batch file is missing at a restart, the batch stays in the side list with the banner "File not found", because the drive can come back. Remove it with "Remove from list".
- Unsaved batches do not stay after a restart. "Quit" asks you to save them first.
- If you save a batch after a run, the log folder of that run stays in the app data folder. The next run writes its logs next to the batch file.
- If you add a batch file that is already in the side list, the app selects the existing entry. It does not add a second entry.
- When you remove a batch from the list, the file stays on disk.
- At save, the app compares the file on disk with the version that it loaded. If they are different, a dialog asks you to overwrite the file or to load it again. The app does not watch the file between saves.
- After a restart, each batch shows the status of its last run. The app reads it from the newest `summary.csv`.
- Many batches can run at the same time. Each batch obeys its own parallel limit. No limit applies to the app as a whole.
- If "Confirm before run" is on, a dialog shows the server, the database, the login and the number of scripts before the run or rerun.
- The "Quit" button stops the app. It asks you to save each unsaved batch. During a run, it asks for confirmation. Then the app cancels all scripts, writes the logs and stops.
- Ctrl+C in the console window does the same, but without questions. Unsaved changes are lost.
- All dialogs are in the page, not in the browser.

## TUI

Not in the initial release. Build it after the GUI and the desktop shell. These decisions apply when you build it:

- The TUI is not interactive. It runs from command-line options or a batch file.
- It shows a live table with the same columns as the GUI table.
- `--server` and `--database` override the target in the batch file.
- `--yes` skips "Confirm before run". Use it in CI.
- Password: the TUI reads the password from the environment variable in `passwordVariable`, or from a command-line option that gives the name of the variable. If no variable is set and the session is interactive, the TUI prompts in the terminal.
- Entra interactive works only in an interactive session. Otherwise the TUI exits with code 2, and tells you to use Entra default.
- The TUI tests the connection once before it starts the queue.
- The TUI does not use the single-instance lock. CI can run many TUI processes.
- Ctrl+C cancels all scripts.
- Exit codes:
  - 0: all scripts succeeded.
  - 1: one or more scripts failed or were cancelled.
  - 2: the run did not start. Examples: an incorrect batch file, a missing password, or a failed connection test.

Example:

```
sqlrunboard run deploy.sqlrunboard.json --server sql02 --yes
```

## Logs

- For each run, the app writes a folder `logs/<batch>_<yyyyMMdd-HHmmss>/` next to the batch file.
- If the folder name already exists, the app adds the suffix `-2`, then `-3`, and so on.
- For an unsaved batch, the app writes the folder in the app data folder.
- `run.log`: all messages from all attempts, and a summary at the end.
- `summary.csv`: script, attempt, status, start, end, duration, rows, error, server, database.

`summary.csv`:

- The file has one row for each attempt, and one row for each script that has not started yet. Queued scripts are included.
- The app writes the file again at each status change. It writes a temporary file, then renames it. A crash cannot leave a partial file.
- After a restart, a Queued or Running row shows as Cancelled with "The app stopped". "Rerun all failed" then continues an interrupted run.
- If the rename fails, the run continues. For example, Excel on Windows locks an open CSV file. The app writes a warning to `run.log` and to the drawer, and tries again at the next status change.
- Times are local time with the UTC offset, for example `2026-10-09T14:03:12+02:00`.
- Add `logs/` to `.gitignore` in repositories that contain batch files.

## Tests

Decision on 2026-10-10.

UI tests:

- Test the GUI with Playwright for .NET (`Microsoft.Playwright`).
- The tests start the real server and open the page with the token.
- The tests use a fake SQL executor in the core. The fake sets the status, the duration, the messages and the failures for each script.
- Run the tests on Chromium, Firefox and WebKit.
- Run the tests in CI on Windows, macOS and Linux.
- Playwright does not test the desktop shell. Test the native folder picker and the window close event manually.

Integration tests:

- A separate suite tests the core against a real SQL Server.
- Each developer needs a local SQL Server. The repository supplies a `compose.yaml` with the `mcr.microsoft.com/mssql/server:2022-latest` image. The image is amd64 only. On Apple Silicon, it needs Rosetta emulation in Docker Desktop.
- The tests read the connection string from `SQLRUNBOARD_TEST_SQL`. If the variable is not set, the tests fail with a clear message. They do not skip.
- CI runs the integration tests on Linux only, with a SQL Server service container.

## For later

- The TUI. See "TUI".
- A devcontainer, when a second contributor needs it.
- Code signing for Windows and macOS.
- A `dotnet tool` package.
- SQLCMD mode (`:setvar`, `$(var)`, `:r`, `:on error`).
- Ordered phases, or dependencies between scripts.
- One target for each script, or one batch against many targets.
- Named environments (dev, test, prod) in the batch file.
- Result sets saved to files.
- Passwords saved in the operating-system credential store.
- Pause the queue.
- Change the parallel limit during a run.
- Drag a batch out into its own window.
- Drag and drop to reorder the queue.
- Move many selected scripts as a block.
- Timeline: a run selector for past runs.
- Timeline: zoom and horizontal scroll.
- Timeline: a segment for each `GO` part. The log must then record the start and end of each part.
- Timeline in the TUI.
