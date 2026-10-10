using System.Globalization;

namespace SqlRunboard.Core;

/// <summary>Finds the script files of a batch. See docs/SPEC.md "Scripts" and "Script order".</summary>
public static class FileScan
{
    /// <summary>Natural sort on the relative path: 2_x.sql comes before 10_x.sql.</summary>
    public static readonly IComparer<string> NaturalOrder =
        StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.NumericOrdering);

    /// <summary>The files in folder that match one of the ;-separated filters, as paths relative to folder with /, in natural order.</summary>
    public static IReadOnlyList<string> Scan(string? folder, string filters, bool recurse)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return [];
        // Simple match: only * and ? are wildcards, without the Win32 quirks (8.3 names, "*.sql" also matching "x.sqlx").
        var options = new EnumerationOptions { MatchType = MatchType.Simple, RecurseSubdirectories = recurse, IgnoreInaccessible = true };
        return filters.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(f => Directory.EnumerateFiles(folder, f, options))
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .Order(NaturalOrder).ThenBy(p => p, StringComparer.Ordinal)
            .ToList();
    }
}
