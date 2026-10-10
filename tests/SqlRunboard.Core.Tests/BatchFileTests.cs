using SqlRunboard.Core;

/// <summary>Save, Save as, Open, Load again and Remove on the side list, against real temp folders.</summary>
public sealed class BatchFileTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("sqlrunboard-batch-").FullName;
    readonly BatchList list = new(new FakeSqlExecutor(TimeProvider.System), TimeProvider.System); // no runs here

    void Touch(params string[] paths)
    {
        foreach (var p in paths)
        {
            var full = Path.Combine(root, p);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "select 1");
        }
    }

    Batch NewBatchWithFolder(string folder)
    {
        var b = list.NewBatch();
        return list.UpdateSettings(b.Id, new BatchSettings { Folder = folder })!;
    }

    [Fact]
    public void Save_as_writes_schema_version_1_with_the_folder_relative_to_the_batch_file()
    {
        Touch("scripts/001_a.sql", "scripts/002_b.sql");
        var b = NewBatchWithFolder(Path.Combine(root, "scripts"));
        list.SetInclude(b.Id, "002_b.sql", false);
        list.UpdateSettings(b.Id, b.Settings with { ConfirmBeforeRun = true, Target = new BatchTarget { Server = "sql01", Database = "Sales" } });

        var saved = list.Save(b.Id, Path.Combine(root, "deploy"), overwrite: false)!;

        Assert.Equal("deploy", saved.Name);
        Assert.Equal(Path.Combine(root, "deploy.sqlrunboard.json"), saved.File);
        Assert.False(saved.Dirty);
        Assert.Equal("scripts", saved.Settings.Folder);
        Assert.Equal("""
            {
              "version": 1,
              "folder": "scripts",
              "filters": "*.sql",
              "recurse": false,
              "parallelLimit": 4,
              "stopOnFirstFailure": false,
              "confirmBeforeRun": true,
              "timeoutSeconds": null,
              "target": {
                "server": "sql01",
                "database": "Sales",
                "auth": "Integrated",
                "user": null,
                "passwordVariable": null,
                "encrypt": "Mandatory",
                "trustServerCertificate": false,
                "extraConnectionString": ""
              },
              "scripts": [
                {
                  "path": "001_a.sql",
                  "include": true
                },
                {
                  "path": "002_b.sql",
                  "include": false
                }
              ]
            }
            """.ReplaceLineEndings("\n"), File.ReadAllText(saved.File!).ReplaceLineEndings("\n").TrimEnd());
    }

    [Fact]
    public void Save_asks_before_it_overwrites_a_file_that_changed_on_disk()
    {
        var b = list.Save(list.NewBatch().Id, Path.Combine(root, "x.sqlrunboard.json"), overwrite: false)!;
        Assert.NotNull(list.Save(b.Id, null, overwrite: false)); // unchanged on disk: no question
        File.WriteAllText(b.File!, File.ReadAllText(b.File!).Replace("\"recurse\": false", "\"recurse\": true"));

        Assert.Throws<BatchFileChangedException>(() => list.Save(b.Id, null, overwrite: false));
        list.Save(b.Id, null, overwrite: true);
        Assert.Contains("\"recurse\": false", File.ReadAllText(b.File!));
    }

    [Fact]
    public void Save_of_an_unsaved_batch_needs_a_path()
    {
        var b = list.NewBatch();

        Assert.Throws<ArgumentException>(() => list.Save(b.Id, null, overwrite: false));
        Assert.Throws<ArgumentException>(() => list.Save(b.Id, "relative/x", overwrite: false));
    }

    [Fact]
    public void Save_as_to_another_folder_keeps_a_relative_folder_pointing_at_the_same_scripts()
    {
        Touch("scripts/a.sql");
        var b = NewBatchWithFolder(Path.Combine(root, "scripts"));
        list.Save(b.Id, Path.Combine(root, "x"), overwrite: false);
        Directory.CreateDirectory(Path.Combine(root, "sub"));

        var moved = list.Save(b.Id, Path.Combine(root, "sub", "y.sqlrunboard.json"), overwrite: false)!;

        Assert.Equal("../scripts", moved.Settings.Folder);
        Assert.Equal("y", moved.Name);
    }

    [Fact]
    public void An_absolute_folder_stays_absolute_after_the_first_save()
    {
        var b = list.Save(list.NewBatch().Id, Path.Combine(root, "x"), overwrite: false)!;
        var other = Path.Combine(root, "elsewhere");
        list.UpdateSettings(b.Id, b.Settings with { Folder = other });

        Assert.Equal(other, list.Save(b.Id, null, overwrite: false)!.Settings.Folder);
    }

    [Fact]
    public void Open_reads_the_settings_and_the_start_order_with_the_cleared_check_boxes()
    {
        Touch("scripts/a.sql", "scripts/b.sql", "scripts/c.sql");
        File.WriteAllText(Path.Combine(root, "deploy.sqlrunboard.json"), """
            {
              "version": 1,
              "folder": "scripts",
              "parallelLimit": 2,
              "target": { "server": "sql01", "auth": "SqlServer" },
              "scripts": [
                { "path": "c.sql", "include": true },
                { "path": "a.sql", "include": false },
                { "path": "b.sql", "include": true }
              ]
            }
            """);

        var b = list.Open(Path.Combine(root, "deploy.sqlrunboard.json"));

        Assert.Equal("deploy", b.Name);
        Assert.False(b.Dirty);
        Assert.Equal(2, b.Settings.ParallelLimit);
        Assert.Equal("*.sql", b.Settings.Filters);
        Assert.Equal(Auth.SqlServer, b.Settings.Target.Auth);
        Assert.Equal([new("c.sql", true), new("a.sql", false), new("b.sql", true)], b.Scripts);
    }

    [Fact]
    public void Opening_a_file_that_is_in_the_list_gives_the_existing_entry()
    {
        var saved = list.Save(list.NewBatch().Id, Path.Combine(root, "x"), overwrite: false)!;

        var opened = list.Open(Path.Combine(root, "sub", "..", "x.sqlrunboard.json"));

        Assert.Equal(saved.Id, opened.Id);
        using var sub = list.Subscribe();
        Assert.Single(sub.Snapshot);
    }

    [Fact]
    public void Open_accepts_only_batch_files()
    {
        File.WriteAllText(Path.Combine(root, "x.json"), "{}");
        File.WriteAllText(Path.Combine(root, "bad.sqlrunboard.json"), """{ "version": 2 }""");

        Assert.Throws<ArgumentException>(() => list.Open(Path.Combine(root, "x.json")));
        Assert.Throws<InvalidDataException>(() => list.Open(Path.Combine(root, "bad.sqlrunboard.json")));
        Assert.Throws<FileNotFoundException>(() => list.Open(Path.Combine(root, "none.sqlrunboard.json")));
    }

    [Fact]
    public void Load_again_replaces_the_batch_with_the_file_on_disk()
    {
        var b = list.Save(list.NewBatch().Id, Path.Combine(root, "x"), overwrite: false)!;
        list.UpdateSettings(b.Id, b.Settings with { ParallelLimit = 9 });
        File.WriteAllText(b.File!, File.ReadAllText(b.File!).Replace("\"parallelLimit\": 4", "\"parallelLimit\": 7"));

        var reloaded = list.Reload(b.Id)!;

        Assert.Equal(7, reloaded.Settings.ParallelLimit);
        Assert.False(reloaded.Dirty);
        Assert.NotNull(list.Save(b.Id, null, overwrite: false)); // the loaded version is now the disk version
    }

    [Fact]
    public void Remove_takes_the_batch_off_the_list_and_keeps_the_file()
    {
        var b = list.Save(list.NewBatch().Id, Path.Combine(root, "x"), overwrite: false)!;

        Assert.True(list.Remove(b.Id));

        using var sub = list.Subscribe();
        Assert.Empty(sub.Snapshot);
        Assert.True(File.Exists(b.File));
        Assert.False(list.Remove(b.Id));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
