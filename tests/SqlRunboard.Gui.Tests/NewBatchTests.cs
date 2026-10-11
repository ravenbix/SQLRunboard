using Microsoft.Playwright;

public class NewBatchTests : AppPageTest
{
    ILocator SideOption(IPage page, string name) =>
        page.GetByRole(AriaRole.Navigation, new() { Name = "Batches" }).GetByRole(AriaRole.Option, new() { Name = name });

    [Fact]
    public async Task The_start_url_opens_the_page_without_the_token()
    {
        await Expect(Page).ToHaveURLAsync(App.Host.BaseUrl.ToString());
        await Expect(Page.GetByRole(AriaRole.Navigation, new() { Name = "Batches" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Ctrl_N_adds_Untitled_1_to_the_side_list_and_opens_its_Settings_tab()
    {
        // The prototype maps Ctrl to Cmd on macOS.
        await Page.Keyboard.PressAsync("ControlOrMeta+n");

        await Expect(SideOption(Page, "Untitled 1")).ToHaveAttributeAsync("aria-selected", "true");
        await Expect(Page.GetByRole(AriaRole.Tab, new() { Name = "Settings" })).ToHaveAttributeAsync("aria-selected", "true");
        using var sub = App.Host.Batches.Subscribe();
        Assert.Equal("Untitled 1", Assert.Single(sub.Snapshot).Name);
    }

    [Fact]
    public async Task Another_open_page_gets_the_new_batch_from_the_stream()
    {
        var other = await Context.NewPageAsync();
        await OpenAsync(other, App.Host.BaseUrl);

        await Page.Keyboard.PressAsync("ControlOrMeta+n");
        await Page.Keyboard.PressAsync("ControlOrMeta+n");

        await Expect(SideOption(other, "Untitled 1")).ToBeVisibleAsync();
        await Expect(SideOption(other, "Untitled 2")).ToBeVisibleAsync();
    }
}
