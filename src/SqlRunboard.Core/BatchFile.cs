using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlRunboard.Core;

/// <summary>The file on disk changed after the app loaded or saved it. The user must choose: overwrite it, or load it again.</summary>
public sealed class BatchFileChangedException(string file) : IOException($"The file changed on disk: {file}");

/// <summary>The batch file, schema version 1. See docs/SPEC.md "Batch". It holds no secrets: the password is never in BatchSettings.</summary>
public static class BatchFile
{
    public const string Extension = ".sqlrunboard.json";

    // A key that is not in the file gets the spec default.
    sealed record Json(int Version, string? Folder = null, string Filters = "*.sql", bool Recurse = false, int ParallelLimit = 4, bool StopOnFirstFailure = false,
        bool ConfirmBeforeRun = false, int? TimeoutSeconds = null, BatchTarget? Target = null, IReadOnlyList<ScriptEntry>? Scripts = null);

    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static bool HasExtension(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>The batch name: the file name without .sqlrunboard.json.</summary>
    public static string NameOf(string file) => Path.GetFileName(file)[..^Extension.Length];

    public static string Format(BatchSettings s, IReadOnlyList<ScriptEntry> scripts) =>
        JsonSerializer.Serialize(new Json(1, s.Folder, s.Filters, s.Recurse, s.ParallelLimit, s.StopOnFirstFailure, s.ConfirmBeforeRun, s.TimeoutSeconds, s.Target, scripts), Options);

    /// <summary>Throws InvalidDataException for an incorrect file.</summary>
    public static (BatchSettings Settings, IReadOnlyList<ScriptEntry> Scripts) Parse(string text)
    {
        Json? j;
        try { j = JsonSerializer.Deserialize<Json>(text, Options); }
        catch (JsonException e) { throw new InvalidDataException($"Not a batch file: {e.Message}"); }
        if (j is not { Version: 1 }) throw new InvalidDataException("Not a batch file of version 1.");
        var settings = new BatchSettings
        {
            Folder = j.Folder, Filters = j.Filters, Recurse = j.Recurse, ParallelLimit = j.ParallelLimit, StopOnFirstFailure = j.StopOnFirstFailure,
            ConfirmBeforeRun = j.ConfirmBeforeRun, TimeoutSeconds = j.TimeoutSeconds, Target = j.Target ?? new(),
        };
        if (!settings.IsValid || j.Scripts?.Any(s => s?.Path is null) == true) throw new InvalidDataException("The batch file has an incorrect setting.");
        return (settings, j.Scripts ?? []);
    }
}
