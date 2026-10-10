using Microsoft.Playwright;

public class SettingsTests : AppPageTest
{
    readonly string scripts = Directory.CreateTempSubdirectory("sqlrunboard-scripts-").FullName;

    void Touch(params string[] paths)
    {
        foreach (var p in paths)
        {
            var full = Path.Combine(scripts, p);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "select 1");
        }
    }

    ILocator Panel => Page.Locator("#panel");
    ILocator Tab(string name) => Page.GetByRole(AriaRole.Tab, new() { Name = name });
    ILocator FileRows => Panel.Locator("tbody tr");

    async Task NewBatchAsync()
    {
        await Page.Keyboard.PressAsync("ControlOrMeta+n");
        await Expect(Tab("Settings")).ToHaveAttributeAsync("aria-selected", "true");
    }

    async Task SetFolderAsync(string folder)
    {
        await Panel.GetByLabel("Folder", new() { Exact = true }).FillAsync(folder);
        await Panel.GetByLabel("Folder", new() { Exact = true }).PressAsync("Enter");
    }

    [Fact]
    public async Task The_Settings_tab_shows_the_spec_defaults()
    {
        await NewBatchAsync();

        await Expect(Panel.GetByLabel("File filters")).ToHaveValueAsync("*.sql");
        await Expect(Panel.GetByLabel("Search subfolders")).Not.ToBeCheckedAsync();
        await Expect(Panel.GetByLabel("Parallel limit")).ToHaveValueAsync("4");
        await Expect(Panel.GetByLabel("Parallel limit")).ToHaveAttributeAsync("min", "1");
        await Expect(Panel.GetByLabel("Parallel limit")).ToHaveAttributeAsync("max", "64");
        await Expect(Panel.GetByLabel("Stop the queue at the first failure")).Not.ToBeCheckedAsync();
        await Expect(Panel.GetByLabel("Confirm before run")).Not.ToBeCheckedAsync();
        await Expect(Panel.GetByLabel("Command timeout (s)")).ToHaveValueAsync("");
    }

    [Fact]
    public async Task The_Files_tab_shows_the_matching_files_in_natural_order()
    {
        Touch("10_x.sql", "2_x.sql", "1_x.tsql", "notes.txt", "sub/3_x.sql");
        await NewBatchAsync();

        await Panel.GetByLabel("File filters").FillAsync("*.sql;*.tsql");
        await Panel.GetByLabel("Search subfolders").CheckAsync();
        await SetFolderAsync(scripts);
        await Tab("Files").ClickAsync();

        await Expect(FileRows.Locator("td.tnum")).ToHaveTextAsync(["1_x.tsql", "2_x.sql", "10_x.sql", "sub/3_x.sql"]);
    }

    [Fact]
    public async Task A_cleared_check_box_is_kept_by_the_server()
    {
        Touch("a.sql", "b.sql");
        await NewBatchAsync();
        await SetFolderAsync(scripts);
        await Tab("Files").ClickAsync();

        await Panel.GetByLabel("Include a.sql").UncheckAsync();

        var other = await Context.NewPageAsync();
        await OpenAsync(other, App.Host.BaseUrl);
        await other.GetByRole(AriaRole.Tab, new() { Name = "Files" }).ClickAsync();
        await Expect(other.GetByLabel("Include a.sql")).Not.ToBeCheckedAsync();
        await Expect(other.GetByLabel("Include b.sql")).ToBeCheckedAsync();

        await Panel.GetByLabel("Include a.sql").CheckAsync();
        await Expect(other.GetByLabel("Include a.sql")).ToBeCheckedAsync();
    }

    [Fact]
    public async Task A_settings_change_goes_to_the_server()
    {
        await NewBatchAsync();

        await Panel.GetByLabel("Parallel limit").FillAsync("100");
        await Panel.GetByLabel("Parallel limit").PressAsync("Enter");
        await Panel.GetByLabel("Command timeout (s)").FillAsync("30");
        await Panel.GetByLabel("Command timeout (s)").PressAsync("Enter");

        var other = await Context.NewPageAsync();
        await OpenAsync(other, App.Host.BaseUrl);
        await other.GetByRole(AriaRole.Tab, new() { Name = "Settings" }).ClickAsync();
        await Expect(other.GetByLabel("Parallel limit")).ToHaveValueAsync("64");
        await Expect(other.GetByLabel("Command timeout (s)")).ToHaveValueAsync("30");
    }

    [Fact]
    public async Task The_folder_dialog_selects_a_folder_from_the_server_list()
    {
        Touch("deploy/1_a.sql");
        await NewBatchAsync();
        await SetFolderAsync(scripts);

        await Panel.GetByRole(AriaRole.Button, new() { Name = "Browse" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "deploy", Exact = true }).ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Select this folder" }).ClickAsync();

        await Expect(dialog).ToHaveCountAsync(0);
        await Expect(Panel.GetByLabel("Folder", new() { Exact = true })).ToHaveValueAsync(Path.Combine(scripts, "deploy"));
        await Tab("Files").ClickAsync();
        await Expect(FileRows.Locator("td.tnum")).ToHaveTextAsync(["1_a.sql"]);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(scripts, recursive: true);
    }
}
