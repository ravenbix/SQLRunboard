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

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(root, recursive: true);
    }
}
