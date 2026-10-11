using System.Text.Json;
using Microsoft.Playwright;

/// <summary>
/// The page in the desktop shell. NativeWebView injects invokeCSharpAction; here a fake bridge records the requests,
/// and the test answers them as the shell does, with shellReply(id, path). Playwright does not test the shell itself.
/// </summary>
public class ShellBridgeTests : AppPageTest
{
    readonly string root = Directory.CreateTempSubdirectory("sqlrunboard-shell-").FullName;

    ILocator Panel => Page.Locator("#panel");
    ILocator Dialog => Page.GetByRole(AriaRole.Dialog);
    ILocator Top(string name) => Page.GetByRole(AriaRole.Toolbar, new() { Name = "App" }).GetByRole(AriaRole.Button, new() { Name = name });

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await Page.AddInitScriptAsync("window.sent = []; window.invokeCSharpAction = data => sent.push(JSON.parse(data));");
        await OpenAsync(Page, App.Host.BaseUrl);
    }

    /// <summary>Waits for the next request to the shell and returns it.</summary>
    async Task<JsonElement> RequestAsync(int n = 0)
    {
        await Page.WaitForFunctionAsync($"() => sent.length > {n}");
        return await Page.EvaluateAsync<JsonElement>($"() => sent[{n}]");
    }

    Task ReplyAsync(JsonElement request, string? path) => Page.EvaluateAsync("([id, p]) => shellReply(id, p)", new object?[] { request.GetProperty("id").GetInt32(), path });

    async Task NewBatchAsync()
    {
        await Page.Keyboard.PressAsync("ControlOrMeta+n");
        await Expect(Panel.GetByLabel("Folder", new() { Exact = true })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Browse_uses_the_native_folder_picker_and_sets_the_real_path()
    {
        await NewBatchAsync();

        await Panel.GetByRole(AriaRole.Button, new() { Name = "Browse" }).ClickAsync();
        var req = await RequestAsync();
        Assert.Equal("folder", req.GetProperty("cmd").GetString());
        await Expect(Dialog).ToHaveCountAsync(0);
        await ReplyAsync(req, root);

        await Expect(Panel.GetByLabel("Folder", new() { Exact = true })).ToHaveValueAsync(root);
    }

    [Fact]
    public async Task A_cancelled_folder_picker_keeps_the_folder()
    {
        await NewBatchAsync();

        await Panel.GetByRole(AriaRole.Button, new() { Name = "Browse" }).ClickAsync();
        await ReplyAsync(await RequestAsync(), null);

        await Expect(Panel.GetByLabel("Folder", new() { Exact = true })).ToHaveValueAsync("");
    }

    [Fact]
    public async Task Open_uses_the_native_file_dialog_and_opens_the_batch_file()
    {
        var b = App.Host.Batches.NewBatch();
        var file = App.Host.Batches.Save(b.Id, Path.Combine(root, "deploy"), overwrite: false)!.File!;
        App.Host.Batches.Remove(b.Id);

        await Top("Open…").ClickAsync();
        var req = await RequestAsync();
        Assert.Equal("open", req.GetProperty("cmd").GetString());
        await Expect(Dialog).ToHaveCountAsync(0);
        await ReplyAsync(req, file);

        await Expect(Page.Locator("#hdr h1")).ToHaveTextAsync("deploy");
    }

    [Fact]
    public async Task Open_accepts_only_batch_files()
    {
        var other = Path.Combine(root, "notes.json");
        File.WriteAllText(other, "{}");

        await Top("Open…").ClickAsync();
        await ReplyAsync(await RequestAsync(), other);

        await Expect(Page.Locator("#toast")).ToContainTextAsync(".sqlrunboard.json");
    }

    [Fact]
    public async Task Save_as_uses_the_native_file_dialog_and_saves_to_the_real_path()
    {
        await NewBatchAsync();

        await Top("Save as…").ClickAsync();
        var req = await RequestAsync();
        Assert.Equal("saveAs", req.GetProperty("cmd").GetString());
        Assert.Equal("Untitled 1.sqlrunboard.json", req.GetProperty("name").GetString());
        await Expect(Dialog).ToHaveCountAsync(0);
        await ReplyAsync(req, Path.Combine(root, "nightly.sqlrunboard.json"));

        await Expect(Page.Locator("#hdr h1")).ToHaveTextAsync("nightly");
        Assert.True(File.Exists(Path.Combine(root, "nightly.sqlrunboard.json")));
    }

    [Fact]
    public async Task Quit_saves_a_never_saved_batch_with_the_native_dialog_then_tells_the_shell_to_close()
    {
        App.Host.Batches.NewBatch();
        await OpenAsync(Page, App.Host.BaseUrl);

        _ = Page.EvaluateAsync("() => { quit(); }"); // the shell calls quit() from the close event of the window
        await Dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await ReplyAsync(await RequestAsync(), Path.Combine(root, "nightly.sqlrunboard.json"));

        Assert.Equal("stopped", (await RequestAsync(1)).GetProperty("cmd").GetString());
        await Expect(Dialog).ToContainTextAsync("SQLRunboard stopped");
        Assert.True(File.Exists(Path.Combine(root, "nightly.sqlrunboard.json")));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(root, recursive: true);
    }
}
