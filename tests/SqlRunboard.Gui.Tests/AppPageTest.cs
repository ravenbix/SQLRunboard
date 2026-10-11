using Microsoft.Playwright.Xunit.v3;

/// <summary>
/// Base for browser tests: each test gets its own real server (fakes, temporary app data) and a page
/// opened with the start URL. The browser comes from the BROWSER env var (chromium, firefox, webkit).
/// </summary>
public abstract class AppPageTest : PageTest
{
    protected TestApp App { get; private set; } = null!;
    readonly List<string> pageErrors = [];

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        Context.WebError += (_, e) => pageErrors.Add(e.Error); // uncaught JS errors on any page fail the test
        App = await TestApp.StartAsync();
        await OpenAsync(Page, App.Host.StartUrl);
    }

    /// <summary>Opens the app and waits until the page has the first SSE snapshot.</summary>
    protected static async Task OpenAsync(Microsoft.Playwright.IPage page, Uri url)
    {
        await page.GotoAsync(url.ToString());
        await page.Locator("html[data-connected]").WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Attached });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await App.DisposeAsync();
        Assert.Empty(pageErrors);
    }
}
