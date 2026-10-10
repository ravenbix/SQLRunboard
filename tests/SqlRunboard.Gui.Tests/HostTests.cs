using System.Net;

public class HostTests
{
    static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Listens_on_loopback_on_a_random_port()
    {
        await using var app = await TestApp.StartAsync();

        Assert.Equal("127.0.0.1", app.Host.BaseUrl.Host);
        Assert.NotEqual(0, app.Host.BaseUrl.Port);
        Assert.StartsWith(app.Host.BaseUrl.ToString(), app.Host.StartUrl.ToString());
    }

    [Fact]
    public async Task Start_url_token_becomes_a_strict_HttpOnly_cookie_and_the_redirect_drops_the_token()
    {
        await using var app = await TestApp.StartAsync();
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

        var res = await http.GetAsync(app.Host.StartUrl, Ct);

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/", res.Headers.Location?.OriginalString);
        var cookie = Assert.Single(res.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith($"sqlrunboard={app.Host.Token};", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_cookie_opens_the_page()
    {
        await using var app = await TestApp.StartAsync();

        var res = await app.Client().GetAsync("/", Ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/html", res.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/?token=wrong")]
    [InlineData("/index.html")]
    [InlineData("/events")]
    [InlineData("/api/batches")]
    public async Task A_request_without_a_valid_token_or_cookie_is_rejected(string path)
    {
        await using var app = await TestApp.StartAsync();
        var cookies = new CookieContainer();
        cookies.Add(app.Host.BaseUrl, new Cookie("sqlrunboard", "wrong"));
        using var withBadCookie = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = app.Host.BaseUrl };
        using var anonymous = new HttpClient { BaseAddress = app.Host.BaseUrl };

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await withBadCookie.GetAsync(path, Ct)).StatusCode);
    }
}
