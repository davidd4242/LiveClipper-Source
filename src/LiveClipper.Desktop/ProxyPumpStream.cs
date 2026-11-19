using System.IO;
using System.Threading.Channels;

namespace LiveClipper.Desktop;

// TestServer reads run off the WebView COM synchronization context, including FLV.
internal sealed class ProxyPumpStream : Stream
{
    private readonly Stream _inner;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte[]> _chunks = Channel.CreateBounded<byte[]>(32);
    private byte[]? _current;
    private int _offset, _disposed, _readers;
    private readonly object _lifetime = new();
    private bool _pumpEnded, _sourceReleased;
    internal Task Completion { get; }
    public ProxyPumpStream(Stream inner) { _inner = inner; Completion = Task.Run(PumpAsync); }
    private async Task PumpAsync()
    {
        Exception? failure = null;
        try {
            var buffer = new byte[8192];
            while (true) {
                var count = await _inner.ReadAsync(buffer.AsMemory(), _stop.Token).ConfigureAwait(false);
                if (count == 0) break;
                await _chunks.Writer.WriteAsync(buffer.AsSpan(0,count).ToArray(), _stop.Token).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
          catch (Exception error) { if (!_stop.IsCancellationRequested) failure = error; }
        finally { _chunks.Writer.TryComplete(failure); lock (_lifetime) { _pumpEnded=true; ReleaseSource(); } }
    }
    public override int Read(byte[] buffer,int offset,int count) => ReadAsync(buffer.AsMemory(offset,count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token) => ReadAsync(buffer.AsMemory(offset,count),token).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
    {
        if (buffer.Length == 0) return 0;
        CancellationTokenSource linked;
        lock (_lifetime) {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(token,_stop.Token);
            _readers++;
        }
        try {
        while (_current is null || _offset == _current.Length) {
            if (!await _chunks.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false)) return 0;
            if (_chunks.Reader.TryRead(out _current)) _offset=0;
        }
        var count=Math.Min(buffer.Length,_current.Length-_offset);
        _current.AsMemory(_offset,count).CopyTo(buffer); _offset+=count; return count;
        } finally { linked.Dispose(); lock (_lifetime) { _readers--; ReleaseSource(); } }
    }
    protected override void Dispose(bool disposing) {
        if (disposing) lock (_lifetime) {
            if (_disposed==0) { _disposed=1; _stop.Cancel(); _inner.Dispose(); ReleaseSource(); }
        }
        base.Dispose(disposing);
    }
    private void ReleaseSource() {
        if (_disposed!=0 && _pumpEnded && _readers==0 && !_sourceReleased) { _sourceReleased=true; _stop.Dispose(); }
    }
    public override bool CanRead=>true;
    public override bool CanWrite=>false;
    public override bool CanSeek=>false;
    public override long Length=>throw new NotSupportedException();
    public override long Position {get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
    public override void Flush() { }
    public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
    public override void SetLength(long length)=>throw new NotSupportedException();
    public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
}

// Normal EOF and explicit disposal both release response ownership exactly once.
internal sealed class ProxyResponseStream(Stream inner, System.Net.Http.HttpResponseMessage response, Action<ProxyResponseStream> released) : Stream
{
    private int _disposed;
    private int End(int count,int requested) { if(count==0 && requested>0) Dispose(); return count; }
    public override int Read(byte[] buffer,int offset,int count) {
        if(Volatile.Read(ref _disposed)!=0)return 0;
        try { return End(inner.Read(buffer,offset,count),count); } catch { Dispose(); throw; }
    }
    public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token)=>ReadAsync(buffer.AsMemory(offset,count),token).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default) {
        if(Volatile.Read(ref _disposed)!=0)return 0;
        try { return End(await inner.ReadAsync(buffer,token).ConfigureAwait(false),buffer.Length); } catch { Dispose(); throw; }
    }
    protected override void Dispose(bool disposing) {
        if(disposing && Interlocked.Exchange(ref _disposed,1)==0) {
            try { inner.Dispose(); response.Dispose(); } finally { released(this); }
        }
        base.Dispose(disposing);
    }
    public override bool CanRead=>true;
    public override bool CanWrite=>false;
    public override bool CanSeek=>false;
    public override long Length=>throw new NotSupportedException();
    public override long Position {get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
    public override void Flush() { }
    public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
    public override void SetLength(long length)=>throw new NotSupportedException();
    public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
}
