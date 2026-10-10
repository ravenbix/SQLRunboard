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
}

/// <summary>One file of the batch. Path is relative to the folder, with /. Include false: a cleared check box.</summary>
public sealed record ScriptEntry(string Path, bool Include);

/// <summary>One entry in the side list. File is null for an unsaved batch. Scripts is the start order. Run is the current run, or null.</summary>
public sealed record Batch(int Id, string Name, string? File, bool Dirty, BatchSettings Settings, IReadOnlyList<ScriptEntry> Scripts, RunState? Run = null);
