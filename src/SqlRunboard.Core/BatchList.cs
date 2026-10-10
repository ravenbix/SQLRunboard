using System.Threading.Channels;

namespace SqlRunboard.Core;

/// <summary>A change for the page. Kind is the SSE event type; Data is the JSON payload.</summary>
public sealed record Change(string Kind, object Data);

/// <summary>
/// The side list: all batches, in the order you added them. The server keeps this state; the page shows it.
/// Each subscriber gets the full state, then every change after it.
/// It also runs the batches: each batch has its own queue and parallel limit, and many batches can run at once.
/// </summary>
public sealed class BatchList(ISqlExecutor executor, TimeProvider time)
{
    readonly Lock gate = new();
    readonly List<Batch> batches = [];
    readonly Dictionary<int, ActiveRun> runs = [];
    readonly List<Channel<Change>> subscribers = [];
    int lastId, lastUntitled;

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
            return Replace(id, b => b with { Settings = settings, Scripts = Merge(b.Scripts, FileScan.Scan(FolderPath(b.File, settings.Folder), settings.Filters, settings.Recurse)) });
        }
    }

    /// <summary>Sets or clears the check box of one file. Null if no batch has this id.</summary>
    public Batch? SetInclude(int id, string path, bool include)
    {
        lock (gate)
            return Replace(id, b => b with { Scripts = [.. b.Scripts.Select(s => s.Path == path ? s with { Include = include } : s)] });
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
            var run = runs[id] = new ActiveRun(time.GetUtcNow(), folder ?? "", b.Settings);
            foreach (var s in scripts.Where(s => s.Include))
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

    /// <summary>The mutable state of a run. Change it only under the lock.</summary>
    sealed class ActiveRun(DateTimeOffset start, string folder, BatchSettings settings)
    {
        public DateTimeOffset Start => start;
        public string Folder => folder;
        public BatchSettings Settings => settings;
        public OrderedDictionary<string, ScriptRun> Scripts { get; } = new(StringComparer.Ordinal); // the start order
        public List<string> Queue { get; } = [];
        public int Running { get; set; }
        public HashSet<string> Cancelled { get; } = new(StringComparer.Ordinal); // Running scripts that you cancelled: not a timeout
        public Dictionary<string, CancellationTokenSource> Tokens { get; } = new(StringComparer.Ordinal); // one per Running script

        public void Add(string path, Attempt a) { var s = Scripts[path]; Scripts[path] = s with { Status = a.Status, Attempts = [.. s.Attempts, a] }; }
        /// <summary>"Stop the queue at the first failure": no new scripts start. The Running scripts continue.</summary>
        public void Failed()
        {
            if (!settings.StopOnFirstFailure) return;
            foreach (var path in Queue) Scripts[path] = Scripts[path] with { Status = ScriptStatus.Cancelled, Error = "Not started: the queue stopped" };
            Queue.Clear();
        }
        public void SetLast(string path, Attempt a) { var s = Scripts[path]; Scripts[path] = s with { Status = a.Status, Attempts = [.. s.Attempts.SkipLast(1), a] }; }
    }

    // Call under the lock. Starts Queued scripts up to the parallel limit, then publishes the run.
    void Pump(int id, ActiveRun run)
    {
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
                run.Add(path, new Attempt(start, start, ScriptStatus.Failed, null, e is FileNotFoundException or DirectoryNotFoundException ? "File not found" : e.Message));
                run.Failed();
                continue;
            }
            run.Running++;
            run.Add(path, new Attempt(start, null, ScriptStatus.Running, null, null));
            var execution = new SqlExecution(path, parts, new SqlConnectionSettings(run.Settings.Target, Password: null));
            // The continuation runs on the thread pool, never inline here under the lock.
            var cts = run.Tokens[path] = run.Settings.TimeoutSeconds is int s ? new CancellationTokenSource(TimeSpan.FromSeconds(s), time) : new CancellationTokenSource();
            _ = executor.ExecuteAsync(execution, _ => { }, cts.Token)
                .ContinueWith(t => Finish(id, run, path, start, t), TaskScheduler.Default);
        }
        var i = batches.FindIndex(b => b.Id == id);
        if (i < 0 || runs.GetValueOrDefault(id) != run) return;
        var state = new RunState(run.Start, time.GetUtcNow(), [.. run.Scripts.Values], [.. run.Queue]);
        Publish(new Change("batch", batches[i] = batches[i] with { Run = state }));
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
            run.Running--;
            if (run.Scripts[path].Status == ScriptStatus.Failed) run.Failed();
            Pump(id, run);
        }
    }

    Batch? Find(int id) => batches.Find(b => b.Id == id);

    /// <summary>A relative folder is relative to the batch file. An unsaved batch has an absolute folder.</summary>
    static string? FolderPath(string? batchFile, string? folder) =>
        string.IsNullOrWhiteSpace(folder) || batchFile is null ? folder : Path.Combine(Path.GetDirectoryName(batchFile)!, folder);

    /// <summary>Known files keep their place and check box; new files go at the bottom in file-name order. Files not on disk drop out.</summary>
    static IReadOnlyList<ScriptEntry> Merge(IReadOnlyList<ScriptEntry> known, IReadOnlyList<string> found)
    {
        var onDisk = found.ToHashSet(StringComparer.Ordinal);
        var old = known.Select(s => s.Path).ToHashSet(StringComparer.Ordinal);
        return [.. known.Where(s => onDisk.Contains(s.Path)), .. found.Where(p => !old.Contains(p)).Select(p => new ScriptEntry(p, Include: true))];
    }

    /// <summary>The snapshot and the subscription are taken under one lock, so no change is lost between them.</summary>
    public Subscription Subscribe()
    {
        var channel = Channel.CreateUnbounded<Change>();
        lock (gate)
        {
            subscribers.Add(channel);
            return new Subscription([.. batches], channel.Reader, () => { lock (gate) subscribers.Remove(channel); });
        }
    }

    // Call under the lock, so each subscriber sees the changes in order.
    void Publish(Change change)
    {
        foreach (var s in subscribers) s.Writer.TryWrite(change);
    }

    public sealed class Subscription(IReadOnlyList<Batch> snapshot, ChannelReader<Change> changes, Action unsubscribe) : IDisposable
    {
        public IReadOnlyList<Batch> Snapshot => snapshot;
        public ChannelReader<Change> Changes => changes;
        public void Dispose() => unsubscribe();
    }
}
