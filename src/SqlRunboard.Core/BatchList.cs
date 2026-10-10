using System.Threading.Channels;

namespace SqlRunboard.Core;

/// <summary>A change for the page. Kind is the SSE event type; Data is the JSON payload.</summary>
public sealed record Change(string Kind, object Data);

/// <summary>
/// The side list: all batches, in the order you added them. The server keeps this state; the page shows it.
/// Each subscriber gets the full state, then every change after it.
/// </summary>
public sealed class BatchList
{
    readonly Lock gate = new();
    readonly List<Batch> batches = [];
    readonly List<Channel<Change>> subscribers = [];
    readonly Dictionary<int, string> loaded = []; // batch id -> the file text that the app last read or wrote
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
            var text = BatchFile.Format(settings, b.Scripts);
            File.WriteAllText(file, text);
            loaded[id] = text;
            var batch = batches[i] = b with { Name = BatchFile.NameOf(file), File = file, Dirty = false, Settings = settings };
            Publish(new Change("batch", batch));
            return batch;
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
        var batch = batches[i] = change(batches[i]) with { Dirty = true };
        Publish(new Change("batch", batch));
        return batch;
    }

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
