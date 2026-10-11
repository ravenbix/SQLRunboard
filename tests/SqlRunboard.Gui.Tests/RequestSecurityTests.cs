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

    [Theory]
    [InlineData("application/json", HttpStatusCode.OK)]
    [InlineData("application/json; charset=utf-8", HttpStatusCode.OK)]
    [InlineData(null, HttpStatusCode.UnsupportedMediaType)]
    [InlineData("text/plain", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("application/x-www-form-urlencoded", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("multipart/form-data; boundary=x", HttpStatusCode.UnsupportedMediaType)]
    public async Task A_POST_is_accepted_only_as_JSON(string? contentType, HttpStatusCode expected)
    {
        await using var app = await TestApp.StartAsync();
        var body = new ByteArrayContent("{}"u8.ToArray());
        if (contentType is not null) body.Headers.TryAddWithoutValidation("Content-Type", contentType);

        var res = await app.Client().PostAsync("/api/batches", body, Ct);

        Assert.Equal(expected, res.StatusCode);
        using var sub = app.Host.Batches.Subscribe();
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, sub.Snapshot.Count);
    }
}
