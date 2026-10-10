using System.Net;

/// <summary>docs/SPEC.md "Security": Host, Origin and Content-Type checks. The token check is in HostTests.</summary>
public class RequestSecurityTests
{
    static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("127.0.0.1", HttpStatusCode.OK)]
    [InlineData("localhost", HttpStatusCode.OK)]
    [InlineData("evil.example", HttpStatusCode.BadRequest)]
    [InlineData("127.0.0.1.evil.example", HttpStatusCode.BadRequest)]
    public async Task Only_the_allowed_hosts_are_served(string host, HttpStatusCode expected)
    {
        await using var app = await TestApp.StartAsync();
        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Host = $"{host}:{app.Host.BaseUrl.Port}";

        var res = await app.Client().SendAsync(req, Ct);

        Assert.Equal(expected, res.StatusCode);
    }

    [Theory]
    [InlineData("http://127.0.0.1:{port}", HttpStatusCode.OK)]
    [InlineData("http://evil.example", HttpStatusCode.Forbidden)]
    [InlineData("http://localhost:{port}", HttpStatusCode.Forbidden)]
    [InlineData("http://127.0.0.1:1", HttpStatusCode.Forbidden)]
    [InlineData("null", HttpStatusCode.Forbidden)]
    public async Task A_request_from_a_different_origin_is_rejected(string origin, HttpStatusCode expected)
    {
        await using var app = await TestApp.StartAsync();
        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("Origin", origin.Replace("{port}", app.Host.BaseUrl.Port.ToString()));

        var res = await app.Client().SendAsync(req, Ct);

        Assert.Equal(expected, res.StatusCode);
    }
}
