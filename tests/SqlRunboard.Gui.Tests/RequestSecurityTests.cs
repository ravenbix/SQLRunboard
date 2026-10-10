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
}
