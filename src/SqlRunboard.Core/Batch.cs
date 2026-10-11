using System.Text.Json.Serialization;

namespace SqlRunboard.Core;

public enum Auth { Integrated, SqlServer, EntraInteractive, EntraDefault }

public enum Encrypt { Mandatory, Optional, Strict }

/// <summary>The target block of the batch file. See docs/SPEC.md "Batch".</summary>
public sealed record BatchTarget
{
    public string? Server { get; init; }
    public string? Database { get; init; }
    public Auth Auth { get; init; } = Auth.Integrated;
    public string? User { get; init; }
    public string? PasswordVariable { get; init; }
    public Encrypt Encrypt { get; init; } = Encrypt.Mandatory;
    public bool TrustServerCertificate { get; init; }
    public string ExtraConnectionString { get; init; } = "";
}

/// <summary>The batch settings, with the defaults from docs/SPEC.md "Batch".</summary>
public sealed record BatchSettings
{
    public string? Folder { get; init; }
    public string Filters { get; init; } = "*.sql";
    public bool Recurse { get; init; }
    public int ParallelLimit { get; init; } = 4;
    public bool StopOnFirstFailure { get; init; }
    public bool ConfirmBeforeRun { get; init; }
    public int? TimeoutSeconds { get; init; }
    public BatchTarget Target { get; init; } = new();

    /// <summary>The ranges of docs/SPEC.md "Batch". JSON input can also give null for the non-null properties.</summary>
    public bool IsValid => this is { ParallelLimit: >= 1 and <= 64, TimeoutSeconds: null or >= 1, Filters: not null, Target: not null };
}

/// <summary>
/// One file of the batch. Path is relative to the folder, with /. Include false: a cleared check box.
/// State: New or Missing until the next save (docs/SPEC.md "Script order"). The batch file never has it.
/// </summary>
public sealed record ScriptEntry(string Path, bool Include, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScriptState? State = null);

public enum ScriptState { New, Missing }

/// <summary>
/// One entry in the side list. File is null for an unsaved batch. Scripts is the start order. Run is the current run, or null.
/// FileError: the app could not read the batch file at the start (for example "File not found"). The entry stays until you remove it.
/// Version: each change of the batch gets a higher number, so the page can drop a state that is older than the one it has.
/// </summary>
public sealed record Batch(int Id, string Name, string? File, bool Dirty, BatchSettings Settings, IReadOnlyList<ScriptEntry> Scripts, RunState? Run = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FileError = null, long Version = 0);

/// <summary>The move commands of docs/SPEC.md "Script order". One item at a time.</summary>
public enum MoveTo { Up, Down, Top, Bottom }
