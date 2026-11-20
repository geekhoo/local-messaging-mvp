using LocalMessaging.Contracts;

namespace LocalMessaging.Host;

internal sealed class StreamRegistry(int maximum) : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Dictionary<long, CancellationTokenSource>> _streams = [];
    private long _generation;
    private long _next;
    private int _count;
    private bool _stopping;
    private TaskCompletionSource _wake = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Count { get { lock (_sync) return _count; } }
    public int CountFor(Guid id) { lock (_sync) return _streams.TryGetValue(id,out var streams) ? streams.Count : 0; }
    public Task CaptureWake() { lock (_sync) return _wake.Task; }
    public void Pulse()
    {
        TaskCompletionSource old;
        lock (_sync) { old = _wake; _wake = NewSignal(); }
        old.TrySetResult();
    }
    public async Task<Lease> RegisterAsync(Guid id, Func<Task> verifyDurableActive, CancellationToken ct)
    {
        // Version the short registration section; all durable I/O remains outside the lock.
        for (var attempt = 0; attempt < 16; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            long observed;
            lock (_sync) observed = _generation;
            await verifyDurableActive();
            lock (_sync)
            {
                if (_stopping) throw new ApiException(503,"host_not_ready","The host is stopping.");
                if (_generation != observed) continue;
                if (_count >= maximum) throw new ApiException(429,"stream_limit_reached","The active stream limit has been reached.");
                var cancellation = new CancellationTokenSource();
                var number = ++_next;
                if (!_streams.TryGetValue(id,out var entries)) _streams[id] = entries = [];
                entries[number] = cancellation;
                _count++;
                return new Lease(this,id,number,cancellation);
            }
        }
        throw new ApiException(503,"host_not_ready","Subscription lifecycle changed repeatedly. Retry with a bounded delay.");
    }
    public void Close(Guid id)
    {
        CancellationTokenSource[] sources;
        lock (_sync)
        {
            _generation++;
            sources = _streams.TryGetValue(id,out var entries) ? entries.Values.ToArray() : [];
        }
        foreach (var source in sources) { try { source.Cancel(); } catch (ObjectDisposedException) { } }
        Pulse();
    }
    public void Stop()
    {
        CancellationTokenSource[] sources;
        lock (_sync)
        {
            _stopping = true;
            _generation++;
            sources = _streams.Values.SelectMany(v => v.Values).ToArray();
        }
        foreach (var source in sources) { try { source.Cancel(); } catch (ObjectDisposedException) { } }
        Pulse();
    }
    private void Remove(Guid id,long number,CancellationTokenSource cancellation)
    {
        lock (_sync)
        {
            if (_streams.TryGetValue(id,out var entries) && entries.Remove(number))
            {
                _count--;
                if (entries.Count == 0) _streams.Remove(id);
            }
        }
        cancellation.Dispose();
    }
    public void Dispose() => Stop();
    public sealed class Lease(StreamRegistry owner, Guid id, long number, CancellationTokenSource cancellation) : IDisposable
    {
        private int _disposed;
        public CancellationToken Token => cancellation.Token;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed,1)==0) owner.Remove(id,number,cancellation); }
    }
}
