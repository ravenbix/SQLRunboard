using Microsoft.Playwright;

/// <summary>Save, Save as, Open, Remove from list and Open app data folder in the browser, with the in-page dialogs.</summary>
public class BatchFileTests : AppPageTest
{
    readonly string root = Directory.CreateTempSubdirectory("sqlrunboard-files-").FullName;

    ILocator Panel => Page.Locator("#panel");
    ILocator Dialog => Page.GetByRole(AriaRole.Dialog);
    ILocator Side => Page.GetByRole(AriaRole.Navigation, new() { Name = "Batches" }).GetByRole(AriaRole.Option);
    ILocator Top(string name) => Page.GetByRole(AriaRole.Toolbar, new() { Name = "App" }).GetByRole(AriaRole.Button, new() { Name = name });

    async Task NewBatchWithFolderAsync(string folder)
    {
        await Page.Keyboard.PressAsync("ControlOrMeta+n");
        await Panel.GetByLabel("Folder", new() { Exact = true }).FillAsync(folder);
        await Panel.GetByLabel("Folder", new() { Exact = true }).PressAsync("Enter");
    }

    string SaveOnServer(string name)
    {
        var b = App.Host.Batches.NewBatch();
        return App.Host.Batches.Save(b.Id, Path.Combine(root, name), overwrite: false)!.File!;
    }

    [Fact]
    public async Task Ctrl_S_on_an_unsaved_batch_does_Save_as_and_adds_the_extension()
    {
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        await NewBatchWithFolderAsync(Path.Combine(root, "scripts"));

        await Page.Keyboard.PressAsync("ControlOrMeta+s");
        await Dialog.GetByLabel("File name").FillAsync(Path.Combine(root, "deploy"));
        await Dialog.GetByLabel("File name").PressAsync("Enter");

        await Expect(Dialog).ToHaveCountAsync(0);
        await Expect(Page.Locator("#hdr h1")).ToHaveTextAsync("deploy");
        await Expect(Panel.GetByLabel("Folder", new() { Exact = true })).ToHaveValueAsync("scripts");
        Assert.Contains("\"folder\": \"scripts\"", File.ReadAllText(Path.Combine(root, "deploy.sqlrunboard.json")));
    }

    [Fact]
    public async Task Save_as_saves_in_a_folder_selected_in_the_server_list()
    {
        Directory.CreateDirectory(Path.Combine(root, "out"));
        await NewBatchWithFolderAsync(root);

        await Page.Keyboard.PressAsync("ControlOrMeta+Shift+s");
        await Dialog.GetByRole(AriaRole.Button, new() { Name = "out", Exact = true }).ClickAsync();
        await Expect(Dialog).ToContainTextAsync(Path.Combine(root, "out"));
        await Dialog.GetByLabel("File name").FillAsync("nightly");
        await Dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();

        await Expect(Dialog).ToHaveCountAsync(0);
        await Expect(Page.Locator("#hdr h1")).ToHaveTextAsync("nightly");
        Assert.True(File.Exists(Path.Combine(root, "out", "nightly.sqlrunboard.json")));
    }

    [Fact]
    public async Task Save_asks_to_overwrite_a_file_that_changed_on_disk()
    {
        var file = SaveOnServer("x");
        await OpenAsync(Page, App.Host.BaseUrl); // the page gets the batch that the test saved on the server
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"recurse\": false", "\"recurse\": true"));

        await Page.Keyboard.PressAsync("ControlOrMeta+s");
        await Dialog.GetByRole(AriaRole.Button, new() { Name = "Overwrite" }).ClickAsync();

        await Expect(Dialog).ToHaveCountAsync(0);
        await Expect(Page.Locator("#live")).ToHaveTextAsync("Saved x.");
        Assert.Contains("\"recurse\": false", File.ReadAllText(file));
    }

    [Fact]
    public async Task Save_can_load_a_changed_file_again()
    {
        var file = SaveOnServer("x");
        await OpenAsync(Page, App.Host.BaseUrl); // the page gets the batch that the test saved on the server
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"parallelLimit\": 4", "\"parallelLimit\": 7"));

        await Page.Keyboard.PressAsync("ControlOrMeta+s");
        await Dialog.GetByRole(AriaRole.Button, new() { Name = "Load again" }).ClickAsync();

        await Page.GetByRole(AriaRole.Tab, new() { Name = "Settings" }).ClickAsync();
        await Expect(Panel.GetByLabel("Parallel limit")).ToHaveValueAsync("7");
    }

    [Fact]
    public async Task Ctrl_O_lists_only_batch_files_and_opens_the_selected_one_once()
    {
        File.WriteAllText(Path.Combine(root, "notes.json"), "{}");
        await using (var other = await TestApp.StartAsync()) // write the file with another server, so this page has no entry yet
        {
            var b = other.Host.Batches.NewBatch();
            other.Host.Batches.Save(b.Id, Path.Combine(root, "deploy"), overwrite: false);
        }

        for (var i = 0; i < 2; i++)
        {
            await Page.Keyboard.PressAsync("ControlOrMeta+o");
            await Dialog.GetByLabel("Path").FillAsync(root);
            await Dialog.GetByLabel("Path").PressAsync("Enter");
            await Expect(Dialog.GetByRole(AriaRole.Button, new() { Name = "notes.json" })).ToHaveCountAsync(0);
            await Dialog.GetByRole(AriaRole.Button, new() { Name = "deploy.sqlrunboard.json" }).ClickAsync();
            await Expect(Dialog).ToHaveCountAsync(0);
        }

        await Expect(Side).ToHaveCountAsync(1);
        await Expect(Side.First).ToHaveAttributeAsync("aria-selected", "true");
        await Expect(Page.Locator("#hdr h1")).ToHaveTextAsync("deploy");
    }

    [Fact]
    public async Task Remove_from_list_keeps_the_file()
    {
        var file = SaveOnServer("x");
        await OpenAsync(Page, App.Host.BaseUrl); // the page gets the batch that the test saved on the server
        await Expect(Side).ToHaveCountAsync(1);

        await Top("Remove from list").ClickAsync();

        await Expect(Side).ToHaveCountAsync(0);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Open_app_data_folder_opens_it()
    {
        await Top("Open app data folder").ClickAsync();

        for (var i = 0; i < 50 && App.OpenedFolders.IsEmpty; i++) await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal([App.AppData], App.OpenedFolders);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(root, recursive: true);
    }
}
