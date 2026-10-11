using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlRunboard.Core;

/// <summary>
/// The log folder of one run: run.log (all messages of all attempts, a summary at each end of the run) and summary.csv
/// (written again at each status change, to a temporary file and then with a rename). Thread-safe.
/// A write that fails does not stop the run: it calls warn and is tried again later.
/// </summary>
sealed class RunLog
{
    public const string LogFile = "run.log", SummaryFile = "summary.csv";
    static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true); // Excel reads UTF-8 only with a BOM

    readonly string folder;
    readonly TimeProvider time;
    readonly Action<string> warn;
    readonly Lock gate = new();
    bool active = true, warned;

    RunLog(string folder, TimeProvider time, Action<string> warn) => (this.folder, this.time, this.warn) = (folder, time, warn);

    /// <summary>Makes the folder logs/&lt;batch&gt;_&lt;yyyyMMdd-HHmmss&gt; under parent. If the name exists, it adds -2, -3 and so on.</summary>
    public static RunLog Create(string parent, string batchName, TimeProvider time, Action<string> warn)
    {
        var name = Path.Combine(parent, "logs", $"{batchName}_{Local(time.GetUtcNow(), time):yyyyMMdd-HHmmss}");
        var folder = name;
        for (var n = 2; Directory.Exists(folder); n++) folder = $"{name}-{n}";
        Directory.CreateDirectory(folder);
        return new RunLog(folder, time, warn);
    }

    /// <summary>The log folder of an earlier run (after a restart): reruns add to it. Its run is not active.</summary>
    public static RunLog Open(string folder, TimeProvider time, Action<string> warn) => new(folder, time, warn) { active = false };

    /// <summary>The newest log folder of the batch under parent/logs that has a summary.csv, or null.</summary>
    public static string? Newest(string parent, string batchName)
    {
        var logs = Path.Combine(parent, "logs");
        if (!Directory.Exists(logs)) return null;
        var name = new Regex($@"^{Regex.Escape(batchName)}_\d{{8}}-\d{{6}}(-\d+)?$");
        // ponytail: the newest by folder name (local start time); a run in the repeated hour at a DST change can sort wrong.
        return Directory.EnumerateDirectories(logs).Where(d => name.IsMatch(Path.GetFileName(d)) && File.Exists(Path.Combine(d, SummaryFile)))
            .OrderBy(Path.GetFileName, FileScan.NaturalOrder).LastOrDefault();
    }

    /// <summary>
    /// Reads summary.csv back: the scripts in the start order, and the server and database of the run.
    /// A Queued or Running row changes to Cancelled with "The app stopped". Throws IOException or FormatException.
    /// </summary>
    public static (List<ScriptRun> Scripts, string Server, string Database) ReadSummary(string folder)
    {
        var rows = ParseCsv(File.ReadAllText(Path.Combine(folder, SummaryFile))).Skip(1).Where(r => r.Count >= 10).ToList();
        static DateTimeOffset? Time(string v) => v == "" ? null : DateTimeOffset.Parse(v, CultureInfo.InvariantCulture);
        // A Running attempt ends at the last time that the file knows: its last status change.
        var last = rows.SelectMany(r => new[] { Time(r[3]), Time(r[4]) }).Max();
        var scripts = new OrderedDictionary<string, ScriptRun>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var status = Enum.Parse<ScriptStatus>(r[2]);
            var stopped = status is ScriptStatus.Queued or ScriptStatus.Running;
            var error = stopped ? "The app stopped" : r[7] == "" ? null : r[7];
            if (stopped) status = ScriptStatus.Cancelled;
            var s = scripts.GetValueOrDefault(r[0]) ?? new ScriptRun(r[0], status, []);
            scripts[r[0]] = r[1] == "0" ? s with { Status = status, Error = error }
                : s with { Status = status, Attempts = [.. s.Attempts, new Attempt(Time(r[3])!.Value, Time(r[4]) ?? last, status, r[6] == "" ? null : long.Parse(r[6], CultureInfo.InvariantCulture), error)] };
        }
        return ([.. scripts.Values], rows.FirstOrDefault()?[8] ?? "", rows.FirstOrDefault()?[9] ?? "");
    }

    /// <summary>RFC 4180: quoted fields can have commas, "" and line breaks.</summary>
    static IEnumerable<List<string>> ParseCsv(string text)
    {
        List<string> row = [];
        var field = new StringBuilder();
        bool quoted = false, any = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            any = true;
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n')
            {
                row.Add(field.ToString().TrimEnd('\r')); field.Clear();
                yield return row;
                (row, any) = ([], false);
            }
            else field.Append(c);
        }
        if (any) { row.Add(field.ToString()); yield return row; }
    }

    /// <summary>Adds one line to run.log, with the local time.</summary>
    public void Append(string text)
    {
        string? error = null;
        lock (gate)
        {
            // ponytail: one open and close for each line; buffer it if many messages make the run slow.
            try { File.AppendAllText(Path.Combine(folder, LogFile), $"{Format(time.GetUtcNow())} {text}{Environment.NewLine}"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { error = e.Message; }
        }
        // Outside the lock: warn takes the BatchList lock, and a message thread must not hold this lock then.
        if (error is not null) warn($"Warning: cannot write {LogFile}: {error}");
    }

    /// <summary>Call at each status change. Writes summary.csv again, and the summary in run.log when the run ends.</summary>
    public void Update(IReadOnlyCollection<ScriptRun> scripts, BatchTarget target)
    {
        lock (gate)
        {
            WriteSummary(scripts, target);
            var now = scripts.Any(s => s.Status is ScriptStatus.Queued or ScriptStatus.Running);
            if (active && !now) AppendSummary(scripts);
            active = now;
        }
    }

    void WriteSummary(IReadOnlyCollection<ScriptRun> scripts, BatchTarget target)
    {
        var csv = new StringBuilder("script,attempt,status,start,end,duration,rows,error,server,database\r\n");
        void Row(string path, int n, ScriptStatus status, Attempt? a, string? error) =>
            csv.AppendJoin(',', [Csv(path), n.ToString(CultureInfo.InvariantCulture), status.ToString(),
                a is null ? "" : Format(a.Start), a?.End is { } end ? Format(end) : "",
                a?.End is { } e ? (e - a.Start).TotalSeconds.ToString("F3", CultureInfo.InvariantCulture) : "",
                a?.Rows?.ToString(CultureInfo.InvariantCulture) ?? "", Csv(error), Csv(target.Server), Csv(target.Database)]).Append("\r\n");
        foreach (var s in scripts)
            if (s.Attempts.Count == 0) Row(s.Path, 0, s.Status, null, s.Error);
            else for (var i = 0; i < s.Attempts.Count; i++) Row(s.Path, i + 1, s.Attempts[i].Status, s.Attempts[i], s.Attempts[i].Error);

        var file = Path.Combine(folder, SummaryFile);
        try
        {
            File.WriteAllText(file + ".tmp", csv.ToString(), Utf8Bom);
            File.Move(file + ".tmp", file, overwrite: true);
            warned = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (warned) return; // one warning until a write works again
            warned = true;
            var text = $"Warning: cannot write {SummaryFile}: {e.Message} The run continues. The app tries again at the next status change.";
            Append(text);
            warn(text);
        }
    }

    void AppendSummary(IReadOnlyCollection<ScriptRun> scripts)
    {
        int Count(ScriptStatus s) => scripts.Count(x => x.Status == s);
        Append($"Run ended: {scripts.Count} scripts, {Count(ScriptStatus.Succeeded)} succeeded, {Count(ScriptStatus.Failed)} failed, {Count(ScriptStatus.Cancelled)} cancelled.");
        foreach (var s in scripts)
        {
            var last = s.Attempts.LastOrDefault();
            Append($"  {s.Path}: {s.Status}, attempts: {s.Attempts.Count}" + (last?.Rows is { } r ? $", rows: {r}" : "") + ((last?.Error ?? s.Error) is { } err ? $", error: {err}" : ""));
        }
    }

    static string Csv(string? v) => v is null ? "" : v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v;

    static DateTimeOffset Local(DateTimeOffset t, TimeProvider time) => TimeZoneInfo.ConvertTime(t, time.LocalTimeZone);

    /// <summary>Local time with the UTC offset, for example 2026-10-09T14:03:12+02:00.</summary>
    string Format(DateTimeOffset t) => Local(t, time).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
}
