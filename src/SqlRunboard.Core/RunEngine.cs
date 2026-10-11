namespace SqlRunboard.Core;

/// <summary>
/// The run engine of one run of a batch: its queue and parallel limit, the executors, the attempts, their messages and the log.
/// It has no lock of its own. It shares the lock of its <see cref="BatchList"/>, so each subscriber sees all changes in one order:
/// BatchList calls it under that lock, and the executor callbacks take that lock.
/// </summary>
sealed class RunEngine(BatchList list, int id, DateTimeOffset start, string folder, BatchSettings settings, RunLog? log)
{
    readonly OrderedDictionary<string, ScriptRun> scripts = new(StringComparer.Ordinal); // the start order
    readonly List<string> queue = [];
    readonly HashSet<string> cancelled = new(StringComparer.Ordinal); // Running scripts that you cancelled: not a timeout
    readonly Dictionary<string, CancellationTokenSource> tokens = new(StringComparer.Ordinal); // one per Running script
    readonly Dictionary<(string Path, int Attempt), (Queue<RunMessage> Kept, int Earlier)> messages = [];
    BatchSettings settings = settings; // a rerun sets the current settings; they cannot change while scripts run
    int running;
    List<Change>? held; // while Pump starts executors: their first messages, to publish after the run state

    /// <summary>The run as the page sees it, at the server clock now.</summary>
    public RunState State => new(start, list.Time.GetUtcNow(), [.. scripts.Values], [.. queue]);

    /// <summary>The kept messages of each attempt, for a new subscriber.</summary>
    public IEnumerable<AttemptMessages> Kept => messages.Select(m => new AttemptMessages(id, m.Key.Path, m.Key.Attempt, m.Value.Earlier, [.. m.Value.Kept]));

    /// <summary>Run: queues these scripts in this order and starts them.</summary>
    public void Start(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            scripts[path] = new ScriptRun(path, ScriptStatus.Queued, []);
            queue.Add(path);
        }
        Pump();
    }

    /// <summary>After a restart: the scripts of the run in summary.csv. Nothing starts.</summary>
    public void Restore(IEnumerable<ScriptRun> runs) { foreach (var s in runs) scripts[s.Path] = s; }

    /// <summary>All Queued and Running scripts.</summary>
    public IReadOnlyList<string> Active => [.. queue, .. tokens.Keys];

    /// <summary>All Failed and Cancelled scripts, in the start order.</summary>
    public IReadOnlyList<string> FailedOrCancelled => [.. scripts.Values.Where(s => s.Status is ScriptStatus.Failed or ScriptStatus.Cancelled).Select(s => s.Path)];

    /// <summary>
    /// A Queued script changes to Cancelled with 0 attempts. A Running script changes to Cancelled when its executor stops:
    /// cancel the returned tokens outside the lock, because a real executor can block while it tells the server to stop.
    /// </summary>
    public List<CancellationTokenSource> Cancel(IReadOnlyList<string> paths)
    {
        List<CancellationTokenSource> stop = [];
        foreach (var path in paths)
            if (queue.Remove(path)) scripts[path] = scripts[path] with { Status = ScriptStatus.Cancelled };
            else if (tokens.TryGetValue(path, out var cts) && cancelled.Add(path)) stop.Add(cts);
        Pump();
        return stop;
    }

    /// <summary>Reruns ignore the check boxes. They add attempts to this run, with the current settings but the target of the run.</summary>
    public void Rerun(IReadOnlyList<string> paths, BatchSettings current)
    {
        settings = current with { Target = settings.Target }; // reruns use the current parallel limit, stop setting and timeout
        foreach (var path in paths)
            if (scripts.TryGetValue(path, out var s) && s.Status is ScriptStatus.Succeeded or ScriptStatus.Failed or ScriptStatus.Cancelled)
            {
                scripts[path] = s with { Status = ScriptStatus.Queued, Error = null };
                queue.Add(path);
            }
        Pump();
    }

    /// <summary>Moves a Queued script in the queue of this run.</summary>
    public void Move(string path, MoveTo to)
    {
        if (BatchList.MoveIn(queue, p => p == path, to)) Pump();
    }

    void Add(string path, Attempt a) { var s = scripts[path]; scripts[path] = s with { Status = a.Status, Attempts = [.. s.Attempts, a] }; }
    void SetLast(string path, Attempt a) { var s = scripts[path]; scripts[path] = s with { Status = a.Status, Attempts = [.. s.Attempts.SkipLast(1), a] }; }

    /// <summary>"Stop the queue at the first failure": no new scripts start. The Running scripts continue.</summary>
    void Failed()
    {
        if (!settings.StopOnFirstFailure) return;
        foreach (var path in queue) scripts[path] = scripts[path] with { Status = ScriptStatus.Cancelled, Error = "Not started: the queue stopped" };
        queue.Clear();
    }

    // Call under the lock. Starts Queued scripts up to the parallel limit, then publishes the run.
    // The executors start before the publish, so a Running script that a subscriber sees has started.
    // Their messages wait until after the publish, so the page sees an attempt before its first message.
    void Pump()
    {
        var time = list.Time;
        List<Action> starts = [];
        while (running < settings.ParallelLimit && queue.Count > 0)
        {
            var path = queue[0];
            queue.RemoveAt(0);
            var begin = time.GetUtcNow();
            IReadOnlyList<ScriptPart> parts;
            // Each script reads its file when it starts, so you can fix a Queued script.
            // ponytail: the read runs under the lock; move it out if slow network folders block the page.
            try { parts = SqlScript.Read(Path.Combine(folder, path)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                var error = e is FileNotFoundException or DirectoryNotFoundException ? "File not found" : e.Message;
                Add(path, new Attempt(begin, begin, ScriptStatus.Failed, null, error));
                log?.Append($"{Attempt(path)}: Failed: {error}");
                var n = scripts[path].Attempts.Count;
                starts.Add(() => AddMessage(path, n, new(SqlMessageKind.Error, error)));
                Failed();
                continue;
            }
            running++;
            Add(path, new Attempt(begin, null, ScriptStatus.Running, null, null));
            var (attempt, number) = (Attempt(path), scripts[path].Attempts.Count);
            log?.Append($"{attempt}: Started");
            var execution = new SqlExecution(path, parts, new SqlConnectionSettings(settings.Target, list.Password(id)));
            // The continuation runs on the thread pool, never inline here under the lock.
            var cts = tokens[path] = settings.TimeoutSeconds is int s ? new CancellationTokenSource(TimeSpan.FromSeconds(s), time) : new CancellationTokenSource();
            void OnMessage(SqlMessage m)
            {
                log?.Append($"{attempt}: {(m.Kind == SqlMessageKind.Error ? "Error: " : "")}{m.Text}"); // outside the lock: a file write
                lock (list.Gate) AddMessage(path, number, m);
            }
            starts.Add(() =>
            {
                Task<SqlResult> task;
                try { task = list.Executor.ExecuteAsync(execution, OnMessage, cts.Token); }
                catch (Exception e) { task = Task.FromException<SqlResult>(e); } // a throw before the first await fails the attempt too
                Task? done = null;
                // The continuation takes the lock, so it cannot remove 'done' before this method adds it.
                done = task.ContinueWith(t =>
                {
                    try { Finish(path, begin, t); }
                    finally { lock (list.Gate) list.Finishing.Remove(done!); }
                }, TaskScheduler.Default);
                list.Finishing.Add(done);
            });
        }
        var outer = held;
        held = [];
        foreach (var a in starts) a();
        var later = held;
        held = outer;
        log?.Update(scripts.Values, settings.Target); // also for a run that is no longer current: its late ends go to its log
        list.Show(id, this);
        foreach (var m in later) list.Publish(m);
    }

    // Call under the lock. Keeps the last MaxMessages of the attempt, and sends the message to the page if the batch shows this run.
    void AddMessage(string path, int attempt, SqlMessage m)
    {
        var (kept, earlier) = messages.GetValueOrDefault((path, attempt), ([], 0));
        var message = new RunMessage(list.Time.GetUtcNow(), m.Kind, m.Text);
        kept.Enqueue(message);
        if (kept.Count > BatchList.MaxMessages) { kept.Dequeue(); earlier++; }
        messages[(path, attempt)] = (kept, earlier);
        if (!list.Shows(id, this)) return;
        var change = new Change("message", new ScriptMessage(id, path, attempt, message.Time, m.Kind, m.Text));
        if (held is not null) held.Add(change); else list.Publish(change);
    }

    void Finish(string path, DateTimeOffset begin, Task<SqlResult> t)
    {
        lock (list.Gate)
        {
            var end = list.Time.GetUtcNow();
            if (tokens.Remove(path, out var cts)) cts.Dispose();
            var stopped = cancelled.Remove(path);
            SetLast(path, t.IsCompletedSuccessfully ? new Attempt(begin, end, t.Result.Status, t.Result.Rows, t.Result.Error)
                : t.IsCanceled && stopped ? new Attempt(begin, end, ScriptStatus.Cancelled, null, null)
                : t.IsCanceled ? new Attempt(begin, end, ScriptStatus.Failed, null, $"Timeout after {settings.TimeoutSeconds} s")
                : new Attempt(begin, end, ScriptStatus.Failed, null, t.Exception!.GetBaseException().Message));
            var a = scripts[path].Attempts[^1];
            if (!t.IsCompletedSuccessfully && a.Error is { } why) AddMessage(path, scripts[path].Attempts.Count, new(SqlMessageKind.Error, why)); // the executor did not send it
            log?.Append($"{Attempt(path)}: {a.Status}" + (a.Rows is { } rows ? $", {rows} rows" : "") + (a.Error is { } err ? $": {err}" : ""));
            running--;
            if (t.IsCompletedSuccessfully && t.Result.LogonFailed) list.ForgetPassword(id); // the page asks again at the next run
            if (scripts[path].Status == ScriptStatus.Failed) Failed();
            Pump();
        }
    }

    /// <summary>"1.sql #2": the script and the number of its last attempt, for run.log.</summary>
    string Attempt(string path) => $"{path} #{scripts[path].Attempts.Count}";
}
