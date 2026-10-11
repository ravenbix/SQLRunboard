using System.Net;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;

public class EventsTests
{
    static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_stream_sends_the_full_state_first_then_each_change()
    {
        await using var app = await TestApp.StartAsync();
        app.Host.Batches.NewBatch();
        using var http = app.Client();

        await using var stream = await http.GetStreamAsync("/events", Ct);
        await using var events = SseParser.Create(stream).EnumerateAsync(Ct).GetAsyncEnumerator(Ct);

        Assert.True(await events.MoveNextAsync());
        Assert.Equal("snapshot", events.Current.EventType);
        var snapshot = JsonDocument.Parse(events.Current.Data).RootElement;
        var first = Assert.Single(snapshot.GetProperty("batches").EnumerateArray());
        Assert.Equal("Untitled 1", first.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("file").ValueKind);
        Assert.True(first.GetProperty("dirty").GetBoolean());
        Assert.Equal(4, first.GetProperty("settings").GetProperty("parallelLimit").GetInt32());
        Assert.Equal("Integrated", first.GetProperty("settings").GetProperty("target").GetProperty("auth").GetString());

        var post = await http.PostAsync("/api/batches", new StringContent("{}", Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Equal("Untitled 2", JsonDocument.Parse(await post.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("name").GetString());

        Assert.True(await events.MoveNextAsync());
        Assert.Equal("batch", events.Current.EventType);
        var change = JsonDocument.Parse(events.Current.Data).RootElement;
        Assert.Equal("Untitled 2", change.GetProperty("name").GetString());
        Assert.NotEqual(first.GetProperty("id").GetInt32(), change.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Stopping_the_server_ends_an_open_stream()
    {
        var app = await TestApp.StartAsync();
        using var http = app.Client();
        await using var stream = await http.GetStreamAsync("/events", Ct);
        await using var events = SseParser.Create(stream).EnumerateAsync(Ct).GetAsyncEnumerator(Ct);
        Assert.True(await events.MoveNextAsync()); // the snapshot

        await app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct); // not the shutdown timeout of the host

        Assert.False(await events.MoveNextAsync());
    }
}
