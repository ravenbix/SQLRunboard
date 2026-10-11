using SqlRunboard.Core;

public sealed class FileScanTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("sqlrunboard-scan-").FullName;

    void Touch(params string[] paths)
    {
        foreach (var p in paths)
        {
            var full = Path.Combine(root, p);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "select 1");
        }
    }

    [Fact]
    public void Lists_the_matching_files_in_natural_order()
    {
        Touch("10_x.sql", "2_x.sql", "1_x.sql", "notes.txt");

        Assert.Equal(["1_x.sql", "2_x.sql", "10_x.sql"], FileScan.Scan(root, "*.sql", recurse: false));
    }

    [Fact]
    public void Many_filters_are_separated_by_semicolons()
    {
        Touch("a.sql", "b.tsql", "c.txt");

        Assert.Equal(["a.sql", "b.tsql"], FileScan.Scan(root, " *.sql ; *.tsql;", recurse: false));
    }

    [Fact]
    public void A_file_that_matches_two_filters_shows_once()
    {
        Touch("a.sql");

        Assert.Equal(["a.sql"], FileScan.Scan(root, "*.sql;a.*", recurse: false));
    }

    [Fact]
    public void Search_subfolders_gives_relative_paths_with_slashes()
    {
        Touch("b.sql", "sub/a.sql", "sub/deeper/c.sql");

        Assert.Equal(["b.sql"], FileScan.Scan(root, "*.sql", recurse: false));
        Assert.Equal(["b.sql", "sub/a.sql", "sub/deeper/c.sql"], FileScan.Scan(root, "*.sql", recurse: true));
    }

    [Fact]
    public void A_missing_or_empty_folder_gives_no_files()
    {
        Assert.Empty(FileScan.Scan(Path.Combine(root, "nope"), "*.sql", recurse: false));
        Assert.Empty(FileScan.Scan(null, "*.sql", recurse: false));
        Assert.Empty(FileScan.Scan("", "*.sql", recurse: false));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
