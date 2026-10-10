using SqlRunboard.Core;

/// <summary>Save, Save as, Open, Load again and Remove on the side list, against real temp folders.</summary>
public sealed class BatchFileTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("sqlrunboard-batch-").FullName;
    readonly BatchList list = new();

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

    public void Dispose() => Directory.Delete(root, recursive: true);
}
