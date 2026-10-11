using System.Threading.Channels;

namespace SqlRunboard.Core;

/// <summary>A change for the page. Kind is the SSE event type; Data is the JSON payload.</summary>
public sealed record Change(string Kind, object Data);

/// <summary>
/// The side list: all batches, in the order you added them. The server keeps this state; the page shows it.
/// Each subscriber gets the full state, then every change after it.
/// It also runs the batches: each batch has its own queue and parallel limit, and many batches can run at once.
/// Each run writes a log folder next to the batch file, or for an unsaved batch in appDataFolder (no log if it is null).
/// </summary>
public sealed class BatchList(ISqlExecutor executor, TimeProvider time, string? appDataFolder = null)
{
    readonly Lock gate = new();
    readonly List<Batch> batches = [];
    readonly Dictionary<int, ActiveRun> runs = [];
    readonly Dictionary<int, ActiveRun> shown = []; // the run that each batch shows: its messages stay when the run ends
    readonly List<Channel<Change>> subscribers = [];
    readonly Dictionary<int, string> passwords = []; // in memory only: never in a Batch, a file or an event
    readonly Dictionary<int, string> loaded = []; // batch id -> the file text that the app last read or wrote
    int lastId, lastUntitled;

    /// <summary>The server keeps the last 10,000 messages of each attempt. run.log has all of them.</summary>
    public const int MaxMessages = 10_000;

    public Batch NewBatch()
    {
        lock (gate)
        {
            var batch = new Batch(++lastId, $"Untitled {++lastUntitled}", File: null, Dirty: true, new BatchSettings(), Scripts: []);
            batches.Add(batch);
            Publish(new Change("batch", batch));
            return batch;
        }
    }

    /// <summary>Changes the settings and scans the folder again. Null if no batch has this id.</summary>
    public Batch? UpdateSettings(int id, BatchSettings settings)
    {
        lock (gate)
        {
            // ponytail: the scan runs under the lock; move it out if a slow network folder blocks the page.
            return Replace(id, b => b with { Settings = settings, Scripts = Merge(b.Scripts, FileScan.Scan(FolderPath(b.File, settings.Folder), settings.Filters, settings.Recurse)), Run = EndRun(b, settings) });
        }
    }

    /// <summary>
    /// Test connection, and the logon check before a run. A password replaces the kept one; null uses the kept one.
    /// A SQL Server logon with no password gives LogonFailed with no Error and no connection: ask for it.
    /// The server keeps a password that works and forgets one that the server rejects. Null if no batch has this id.
    /// </summary>
    public async Task<SqlLogon?> LogonAsync(int id, string? password, CancellationToken ct)
    {
        BatchTarget target;
        lock (gate)
        {
            if (Find(id) is not { } b) return null;
            target = b.Settings.Target;
            password ??= passwords.GetValueOrDefault(id);
        }
        if (target.Auth == Auth.SqlServer && password is null) return new SqlLogon(null, null, null, LogonFailed: true);
        var logon = await executor.LogonAsync(new SqlConnectionSettings(target, password), ct);
        lock (gate)
        {
            if (Find(id)?.Settings.Target != target) return logon; // the target changed meanwhile: keep nothing
            if (logon.LogonFailed) passwords.Remove(id);
            else if (logon.Error is null && password is not null) passwords[id] = password;
        }
        return logon;
    }

    /// <summary>
    /// Call under the lock. A change to the target or the file scan ends the current run: reruns do nothing until the next Run.
    /// The other settings apply to the next reruns. A run with Queued or Running scripts does not end.
    /// </summary>
    RunState? EndRun(Batch b, BatchSettings settings)
    {
        var (a, run) = (b.Settings, b.Run);
        var newTarget = a.Target with { PasswordVariable = null } != settings.Target with { PasswordVariable = null };
        if (newTarget) passwords.Remove(b.Id); // a new target asks for the password again
        var ends = newTarget || (a.Folder, a.Filters, a.Recurse) != (settings.Folder, settings.Filters, settings.Recurse);
        return ends && run is { Ended: false, Active: false } && runs.Remove(b.Id) ? run with { Ended = true } : run;
    }

    /// <summary>Sets or clears the check box of one file. Null if no batch has this id.</summary>
    public Batch? SetInclude(int id, string path, bool include)
    {
        lock (gate)
            return Replace(id, b => b with { Scripts = [.. b.Scripts.Select(s => s.Path == path ? s with { Include = include } : s)] });
    }

    /// <summary>
    /// Moves one script. During a run it moves a Queued script in the queue of that run only. Otherwise it changes the start order.
    /// Null if no batch has this id.
    /// </summary>
    public Batch? Move(int id, string path, MoveTo to)
    {
        lock (gate)
        {
            if (Find(id) is not { } b) return null;
            if (b.Run?.Active == true)
            {
                if (runs.GetValueOrDefault(id) is { } run && MoveIn(run.Queue, p => p == path, to)) Pump(id, run);
                return Find(id);
            }
            var scripts = b.Scripts.ToList();
            return MoveIn(scripts, s => s.Path == path, to) ? Replace(id, x => x with { Scripts = scripts }) : b;
        }
    }

    /// <summary>Moves a batch in the side list. SSE event "order" with the ids in the new order. False if no batch has this id.</summary>
    public bool MoveBatch(int id, MoveTo to)
    {
        lock (gate)
        {
            if (Find(id) is null) return false;
            if (MoveIn(batches, b => b.Id == id, to)) Publish(new Change("order", new { ids = batches.Select(b => b.Id).ToArray() }));
            return true;
        }
    }

    /// <summary>"Sort by file name": the start order becomes the natural sort. Null if no batch has this id.</summary>
    public Batch? SortByFileName(int id)
    {
        lock (gate)
            return Replace(id, b => b with { Scripts = [.. b.Scripts.OrderBy(s => s.Path, FileScan.NaturalOrder).ThenBy(s => s.Path, StringComparer.Ordinal)] });
    }

    /// <summary>Moves the first match. False if there is no match or it is already at that end.</summary>
    static bool MoveIn<T>(List<T> list, Predicate<T> match, MoveTo to)
    {
        var i = list.FindIndex(match);
        if (i < 0) return false;
        var item = list[i];
        list.RemoveAt(i);
        var j = to switch { MoveTo.Top => 0, MoveTo.Bottom => list.Count, MoveTo.Up => Math.Max(0, i - 1), _ => Math.Min(list.Count, i + 1) };
        list.Insert(j, item);
        return i != j;
    }

    /// <summary>
    /// Writes the batch file. Path null: Save, to the current file. Otherwise Save as, to that full path; the app adds .sqlrunboard.json if it is not there.
    /// Throws BatchFileChangedException if the file on disk is not the version that the app loaded, unless overwrite is true. Null if no batch has this id.
    /// </summary>
    public Batch? Save(int id, string? path, bool overwrite)
    {
        if (path is not null && !Path.IsPathFullyQualified(path)) throw new ArgumentException($"Type a full path: {path}");
        lock (gate)
        {
            var i = batches.FindIndex(b => b.Id == id);
            if (i < 0) return null;
            var b = batches[i];
            var file = path is null ? b.File ?? throw new ArgumentException("The batch has no file. Use Save as.")
                : BatchFile.HasExtension(path) ? Path.GetFullPath(path) : Path.GetFullPath(path + BatchFile.Extension);
            // ponytail: Save as over another existing file overwrites it without a question; ask first if users lose files that way.
            if (!overwrite && file == b.File && File.Exists(file) && File.ReadAllText(file) != loaded.GetValueOrDefault(id)) throw new BatchFileChangedException(file);
            var settings = b.Settings with { Folder = Rebase(b.File, file, b.Settings.Folder) };
            // Save removes the Missing entries and clears the New marks.
            List<ScriptEntry> scripts = [.. b.Scripts.Where(s => s.State != ScriptState.Missing).Select(s => s with { State = null })];
            var text = BatchFile.Format(settings, scripts);
            File.WriteAllText(file, text);
            loaded[id] = text;
            var batch = batches[i] = b with { Name = BatchFile.NameOf(file), File = file, Dirty = false, Settings = settings, Scripts = scripts };
            Publish(new Change("batch", batch));
            return batch;
        }
    }

    /// <summary>
    /// Adds a batch file to the list, or gives the entry that already has this file. The path must be full and end with .sqlrunboard.json.
    /// Throws FileNotFoundException, or InvalidDataException for an incorrect file.
    /// </summary>
    public Batch Open(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !BatchFile.HasExtension(path)) throw new ArgumentException($"Select a full path to a {BatchFile.Extension} file: {path}");
        var file = Path.GetFullPath(path);
        lock (gate)
        {
            // ponytail: an ordinal path compare; the same file with another letter case on Windows or macOS gets a second entry.
            if (batches.Find(b => b.File == file) is { } open) return open;
            var batch = Load(new Batch(++lastId, BatchFile.NameOf(file), file, Dirty: false, new BatchSettings(), Scripts: []));
            batches.Add(batch);
            Publish(new Change("batch", batch));
            return batch;
        }
    }

    /// <summary>"Load again": replaces the batch with its file on disk. Null if no batch has this id.</summary>
    public Batch? Reload(int id)
    {
        lock (gate)
        {
            var i = batches.FindIndex(b => b.Id == id);
            if (i < 0) return null;
            if (batches[i].File is null) throw new ArgumentException("The batch has no file.");
            var batch = batches[i] = Load(batches[i]);
            Publish(new Change("batch", batch));
            return batch;
        }
    }

    // Call under the lock. Reads b.File and scans its folder.
    Batch Load(Batch b)
    {
        var text = File.ReadAllText(b.File!);
        var (settings, scripts) = BatchFile.Parse(text);
        loaded[b.Id] = text;
        return b with { Dirty = false, Settings = settings, Scripts = Merge(scripts, FileScan.Scan(FolderPath(b.File, settings.Folder), settings.Filters, settings.Recurse)), Run = EndRun(b, settings) };
    }

    /// <summary>"Remove from list". The file stays on disk. The run of the batch is cancelled. False if no batch has this id.</summary>
    public bool Remove(int id)
    {
        CancelAll(id); // outside the lock: see Cancel
        lock (gate)
        {
            if (batches.RemoveAll(b => b.Id == id) == 0) return false;
            loaded.Remove(id);
            passwords.Remove(id);
            runs.Remove(id);
            shown.Remove(id);
            Publish(new Change("remove", new { id }));
            return true;
        }
    }

    /// <summary>
    /// The folder for a batch file at newFile. A relative folder (or any folder of an unsaved batch) becomes relative to newFile, with /.
    /// It stays absolute if no relative path is possible (another Windows drive). An absolute folder of a saved batch is your choice and stays.
    /// </summary>
    static string? Rebase(string? oldFile, string newFile, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || (oldFile is not null && Path.IsPathRooted(folder))) return folder;
        var rel = Path.GetRelativePath(Path.GetDirectoryName(newFile)!, Path.GetFullPath(FolderPath(oldFile, folder)!));
        return Path.IsPathRooted(rel) ? rel : rel.Replace('\\', '/');
    }

    // Call under the lock.
    Batch? Replace(int id, Func<Batch, Batch> change)
    {
        var i = batches.FindIndex(b => b.Id == id);
        if (i < 0) return null;
        if (batches[i].Run?.Active == true) return batches[i]; // the run locks the settings and the check boxes
        var batch = batches[i] = change(batches[i]) with { Dirty = true };
        Publish(new Change("batch", batch));
        return batch;
    }

    /// <summary>
    /// Scans the folder again and queues all included scripts in the start order. Returns the batch with its new run,
    /// the batch unchanged if its run still has Queued or Running scripts, or null if no batch has this id.
    /// </summary>
    public Batch? Run(int id)
    {
        lock (gate)
        {
            var i = batches.FindIndex(b => b.Id == id);
            if (i < 0) return null;
            var b = batches[i];
            if (b.Run?.Active == true) return b;
            var folder = FolderPath(b.File, b.Settings.Folder);
            var scripts = Merge(b.Scripts, FileScan.Scan(folder, b.Settings.Filters, b.Settings.Recurse));
            batches[i] = b with { Scripts = scripts, Dirty = b.Dirty || !scripts.SequenceEqual(b.Scripts) };
            var logParent = b.File is null ? appDataFolder : Path.GetDirectoryName(b.File);
            // ponytail: a log folder that cannot be made (read-only folder) stops the Run with the IO error; run without logs if users need that.
            var log = logParent is null ? null : RunLog.Create(logParent, b.Name, time, text => Warn(id, text));
            var run = runs[id] = shown[id] = new ActiveRun(time.GetUtcNow(), folder ?? "", b.Settings, log);
            foreach (var s in scripts.Where(s => s.Include && s.State != ScriptState.Missing))
            {
                run.Scripts[s.Path] = new ScriptRun(s.Path, ScriptStatus.Queued, []);
                run.Queue.Add(s.Path);
            }
            Pump(id, run);
            return batches[i];
        }
    }

    /// <summary>
    /// Cancels one script of the current run. A Queued script changes to Cancelled with 0 attempts. A Running script
    /// changes to Cancelled when its executor stops. Returns the batch, or null if no batch has this id.
    /// </summary>
    public Batch? Cancel(int id, string path) => Cancel(id, _ => [path]);

    /// <summary>Cancels all Running and Queued scripts of the current run. Null if no batch has this id.</summary>
    public Batch? CancelAll(int id) => Cancel(id, run => [.. run.Queue, .. run.Tokens.Keys]);

    Batch? Cancel(int id, Func<ActiveRun, IReadOnlyList<string>> paths)
    {
        List<CancellationTokenSource> stop = [];
        Batch? batch;
        lock (gate)
        {
            if (runs.GetValueOrDefault(id) is { } run)
            {
                foreach (var path in paths(run))
                    if (run.Queue.Remove(path)) run.Scripts[path] = run.Scripts[path] with { Status = ScriptStatus.Cancelled };
                    else if (run.Tokens.TryGetValue(path, out var cts) && run.Cancelled.Add(path)) stop.Add(cts);
                Pump(id, run);
            }
            batch = Find(id);
        }
        // Outside the lock: a real executor can block while it tells the server to stop.
        foreach (var cts in stop)
            try { cts.Cancel(); } catch (ObjectDisposedException) { } // the script ended first
        return batch;
    }

    /// <summary>
    /// Reruns one completed script of the current run. It goes to the bottom of the queue and reads its file again.
    /// Returns the batch, or null if no batch has this id.
    /// </summary>
    public Batch? Rerun(int id, string path) => Rerun(id, _ => [path]);

    /// <summary>Reruns all Failed and Cancelled scripts of the current run, in the start order. Null if no batch has this id.</summary>
    public Batch? RerunFailed(int id) =>
        Rerun(id, run => [.. run.Scripts.Values.Where(s => s.Status is ScriptStatus.Failed or ScriptStatus.Cancelled).Select(s => s.Path)]);

    // Reruns ignore the check boxes. They add attempts to the current run.
    Batch? Rerun(int id, Func<ActiveRun, IReadOnlyList<string>> paths)
    {
        lock (gate)
        {
            if (runs.GetValueOrDefault(id) is { } run && Find(id) is { } batch)
            {
                run.Settings = batch.Settings; // reruns use the current parallel limit, stop setting and timeout
                foreach (var path in paths(run))
                    if (run.Scripts.TryGetValue(path, out var s) && s.Status is ScriptStatus.Succeeded or ScriptStatus.Failed or ScriptStatus.Cancelled)
                    {
                        run.Scripts[path] = s with { Status = ScriptStatus.Queued, Error = null };
                        run.Queue.Add(path);
                    }
                Pump(id, run);
            }
            return Find(id);
        }
    }

    /// <summary>The mutable state of a run. Change it only under the lock.</summary>
    sealed class ActiveRun(DateTimeOffset start, string folder, BatchSettings settings, RunLog? log)
    {
        public RunLog? Log => log;
        public DateTimeOffset Start => start;
        public string Folder => folder;
        public BatchSettings Settings { get; set; } = settings; // a rerun sets the current settings; they cannot change while scripts run
        public OrderedDictionary<string, ScriptRun> Scripts { get; } = new(StringComparer.Ordinal); // the start order
        public List<string> Queue { get; } = [];
        public int Running { get; set; }
        public HashSet<string> Cancelled { get; } = new(StringComparer.Ordinal); // Running scripts that you cancelled: not a timeout
        public Dictionary<string, CancellationTokenSource> Tokens { get; } = new(StringComparer.Ordinal); // one per Running script
        public Dictionary<(string Path, int Attempt), (Queue<RunMessage> Kept, int Earlier)> Messages { get; } = [];

        public void Add(string path, Attempt a) { var s = Scripts[path]; Scripts[path] = s with { Status = a.Status, Attempts = [.. s.Attempts, a] }; }
        /// <summary>"Stop the queue at the first failure": no new scripts start. The Running scripts continue.</summary>
        public void Failed()
        {
            if (!Settings.StopOnFirstFailure) return;
            foreach (var path in Queue) Scripts[path] = Scripts[path] with { Status = ScriptStatus.Cancelled, Error = "Not started: the queue stopped" };
            Queue.Clear();
        }
        public void SetLast(string path, Attempt a) { var s = Scripts[path]; Scripts[path] = s with { Status = a.Status, Attempts = [.. s.Attempts.SkipLast(1), a] }; }
    }

    // Call under the lock. Starts Queued scripts up to the parallel limit, then publishes the run.
    // The executors start after the publish, so the page sees an attempt before its first message.
    void Pump(int id, ActiveRun run)
    {
        List<Action> after = [];
        while (run.Running < run.Settings.ParallelLimit && run.Queue.Count > 0)
        {
            var path = run.Queue[0];
            run.Queue.RemoveAt(0);
            var start = time.GetUtcNow();
            IReadOnlyList<ScriptPart> parts;
            // Each script reads its file when it starts, so you can fix a Queued script.
            // ponytail: the read runs under the lock; move it out if slow network folders block the page.
            try { parts = SqlScript.Read(Path.Combine(run.Folder, path)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                var error = e is FileNotFoundException or DirectoryNotFoundException ? "File not found" : e.Message;
                run.Add(path, new Attempt(start, start, ScriptStatus.Failed, null, error));
                run.Log?.Append($"{Attempt(run, path)}: Failed: {error}");
                var n = run.Scripts[path].Attempts.Count;
                after.Add(() => AddMessage(id, run, path, n, new(SqlMessageKind.Error, error)));
                run.Failed();
                continue;
            }
            run.Running++;
            run.Add(path, new Attempt(start, null, ScriptStatus.Running, null, null));
            var (attempt, number) = (Attempt(run, path), run.Scripts[path].Attempts.Count);
            run.Log?.Append($"{attempt}: Started");
            var execution = new SqlExecution(path, parts, new SqlConnectionSettings(run.Settings.Target, passwords.GetValueOrDefault(id)));
            // The continuation runs on the thread pool, never inline here under the lock.
            var cts = run.Tokens[path] = run.Settings.TimeoutSeconds is int s ? new CancellationTokenSource(TimeSpan.FromSeconds(s), time) : new CancellationTokenSource();
            void OnMessage(SqlMessage m)
            {
                run.Log?.Append($"{attempt}: {(m.Kind == SqlMessageKind.Error ? "Error: " : "")}{m.Text}"); // outside the lock: a file write
                lock (gate) AddMessage(id, run, path, number, m);
            }
            after.Add(() => executor.ExecuteAsync(execution, OnMessage, cts.Token).ContinueWith(t => Finish(id, run, path, start, t), TaskScheduler.Default));
        }
        run.Log?.Update(run.Scripts.Values, run.Settings.Target); // also for a run that is no longer current: its late ends go to its log
        var i = batches.FindIndex(b => b.Id == id);
        if (i >= 0 && runs.GetValueOrDefault(id) == run)
            Publish(new Change("batch", batches[i] = batches[i] with { Run = new RunState(run.Start, time.GetUtcNow(), [.. run.Scripts.Values], [.. run.Queue]) }));
        foreach (var a in after) a();
    }

    // Call under the lock. Keeps the last MaxMessages of the attempt, and sends the message to the page if the batch shows this run.
    void AddMessage(int id, ActiveRun run, string path, int attempt, SqlMessage m)
    {
        var (kept, earlier) = run.Messages.GetValueOrDefault((path, attempt), ([], 0));
        var message = new RunMessage(time.GetUtcNow(), m.Kind, m.Text);
        kept.Enqueue(message);
        if (kept.Count > MaxMessages) { kept.Dequeue(); earlier++; }
        run.Messages[(path, attempt)] = (kept, earlier);
        if (shown.GetValueOrDefault(id) == run) Publish(new Change("message", new ScriptMessage(id, path, attempt, message.Time, m.Kind, m.Text)));
    }

    void Finish(int id, ActiveRun run, string path, DateTimeOffset start, Task<SqlResult> t)
    {
        lock (gate)
        {
            var end = time.GetUtcNow();
            if (run.Tokens.Remove(path, out var cts)) cts.Dispose();
            var cancelled = run.Cancelled.Remove(path);
            run.SetLast(path, t.IsCompletedSuccessfully ? new Attempt(start, end, t.Result.Status, t.Result.Rows, t.Result.Error)
                : t.IsCanceled && cancelled ? new Attempt(start, end, ScriptStatus.Cancelled, null, null)
                : t.IsCanceled ? new Attempt(start, end, ScriptStatus.Failed, null, $"Timeout after {run.Settings.TimeoutSeconds} s")
                : new Attempt(start, end, ScriptStatus.Failed, null, t.Exception!.GetBaseException().Message));
            var a = run.Scripts[path].Attempts[^1];
            if (!t.IsCompletedSuccessfully && a.Error is { } why) AddMessage(id, run, path, run.Scripts[path].Attempts.Count, new(SqlMessageKind.Error, why)); // the executor did not send it
            run.Log?.Append($"{Attempt(run, path)}: {a.Status}" + (a.Rows is { } rows ? $", {rows} rows" : "") + (a.Error is { } err ? $": {err}" : ""));
            run.Running--;
            if (t.IsCompletedSuccessfully && t.Result.LogonFailed) passwords.Remove(id); // the page asks again at the next run
            if (run.Scripts[path].Status == ScriptStatus.Failed) run.Failed();
            Pump(id, run);
        }
    }

    /// <summary>"1.sql #2": the script and the number of its last attempt, for run.log.</summary>
    static string Attempt(ActiveRun run, string path) => $"{path} #{run.Scripts[path].Attempts.Count}";

    /// <summary>A log write failed. The page shows the text; the run continues.</summary>
    void Warn(int id, string text)
    {
        lock (gate) Publish(new Change("warning", new { id, text }));
    }

    Batch? Find(int id) => batches.Find(b => b.Id == id);

    /// <summary>A relative folder is relative to the batch file. An unsaved batch has an absolute folder.</summary>
    static string? FolderPath(string? batchFile, string? folder) =>
        string.IsNullOrWhiteSpace(folder) || batchFile is null ? folder : Path.Combine(Path.GetDirectoryName(batchFile)!, folder);

    /// <summary>
    /// Known files keep their place and check box; new files go at the bottom in file-name order, marked New.
    /// Files not on disk stay, marked Missing, until the next save.
    /// </summary>
    static IReadOnlyList<ScriptEntry> Merge(IReadOnlyList<ScriptEntry> known, IReadOnlyList<string> found)
    {
        var onDisk = found.ToHashSet(StringComparer.Ordinal);
        var old = known.Select(s => s.Path).ToHashSet(StringComparer.Ordinal);
        return [.. known.Select(s => !onDisk.Contains(s.Path) ? s with { State = ScriptState.Missing } : s.State == ScriptState.Missing ? s with { State = null } : s),
            .. found.Where(p => !old.Contains(p)).Select(p => new ScriptEntry(p, Include: true, ScriptState.New))];
    }

    /// <summary>The snapshot and the subscription are taken under one lock, so no change is lost between them.</summary>
    public Subscription Subscribe()
    {
        var channel = Channel.CreateUnbounded<Change>();
        lock (gate)
        {
            subscribers.Add(channel);
            List<AttemptMessages> messages = [.. shown.SelectMany(r => r.Value.Messages.Select(m => new AttemptMessages(r.Key, m.Key.Path, m.Key.Attempt, m.Value.Earlier, [.. m.Value.Kept])))];
            return new Subscription([.. batches], messages, channel.Reader, () => { lock (gate) subscribers.Remove(channel); });
        }
    }

    // Call under the lock, so each subscriber sees the changes in order.
    void Publish(Change change)
    {
        foreach (var s in subscribers) s.Writer.TryWrite(change);
    }

    public sealed class Subscription(IReadOnlyList<Batch> snapshot, IReadOnlyList<AttemptMessages> messages, ChannelReader<Change> changes, Action unsubscribe) : IDisposable
    {
        public IReadOnlyList<Batch> Snapshot => snapshot;
        /// <summary>The kept messages of the runs that the batches show.</summary>
        public IReadOnlyList<AttemptMessages> Messages => messages;
        public ChannelReader<Change> Changes => changes;
        public void Dispose() => unsubscribe();
    }
}
