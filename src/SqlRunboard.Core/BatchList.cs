using System.Threading.Channels;

namespace SqlRunboard.Core;

/// <summary>A change for the page. Kind is the SSE event type; Data is the JSON payload.</summary>
public sealed record Change(string Kind, object Data);

/// <summary>
/// The side list: all batches, in the order you added them. The server keeps this state; the page shows it.
/// Each subscriber gets the full state, then every change after it.
/// </summary>
public sealed class BatchList
{
    readonly Lock gate = new();
    readonly List<Batch> batches = [];
    readonly List<Channel<Change>> subscribers = [];
    int lastId, lastUntitled;

    public Batch NewBatch()
    {
        lock (gate)
        {
            var batch = new Batch(++lastId, $"Untitled {++lastUntitled}", File: null, Dirty: true, new BatchSettings());
            batches.Add(batch);
            Publish(new Change("batch", batch));
            return batch;
        }
    }

    /// <summary>The snapshot and the subscription are taken under one lock, so no change is lost between them.</summary>
    public Subscription Subscribe()
    {
        var channel = Channel.CreateUnbounded<Change>();
        lock (gate)
        {
            subscribers.Add(channel);
            return new Subscription([.. batches], channel.Reader, () => { lock (gate) subscribers.Remove(channel); });
        }
    }

    // Call under the lock, so each subscriber sees the changes in order.
    void Publish(Change change)
    {
        foreach (var s in subscribers) s.Writer.TryWrite(change);
    }

    public sealed class Subscription(IReadOnlyList<Batch> snapshot, ChannelReader<Change> changes, Action unsubscribe) : IDisposable
    {
        public IReadOnlyList<Batch> Snapshot => snapshot;
        public ChannelReader<Change> Changes => changes;
        public void Dispose() => unsubscribe();
    }
}
