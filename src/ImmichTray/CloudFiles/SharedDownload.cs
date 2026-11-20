using System.Buffers;
using ImmichTray.Immich;
using Microsoft.Win32.SafeHandles;

namespace ImmichTray.CloudFiles;

/// <summary>
/// One download of one photo, shared by every Windows request for it.
///
/// Apps like Photos ask for the same file many times at once (overlapping pieces, a second window,
/// the filmstrip). Downloading each request on its own multiplied the traffic, made everything slow,
/// and eventually tripped the server. Here the file is downloaded once, start to end, into a temp
/// file, and each request is answered from that as soon as its bytes are in.
/// </summary>
internal sealed class SharedDownload
{
    /// <summary>Pieces read from the server and handed to Windows. Multiple of 4096.</summary>
    public const int ChunkSize = 64 * 1024;

    /// <summary>How long a finished or abandoned download is kept for requests that come back.</summary>
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(30);

    private const int MaxAttempts = 3;

    /// <summary>A Windows request waiting for bytes [Next, End).</summary>
    public sealed class Waiter(object request, long transferKey, long next, long end)
    {
        public object Request { get; } = request;
        public long TransferKey { get; } = transferKey;
        public long Next { get; set; } = next;
        public long End { get; } = end;
    }

    /// <summary>Hands bytes to Windows; false when Windows no longer wants them.</summary>
    public delegate bool Deliver(Waiter w, byte[] buffer, long offset, int length);

    /// <summary>Tells Windows the request failed.</summary>
    public delegate void Failed(Waiter w);

    private readonly object _gate = new();
    private readonly List<Waiter> _waiters = new();
    private readonly string _assetId;
    private readonly Func<IImmichClient?> _client;
    private readonly Deliver _deliver;
    private readonly Failed _failed;
    private readonly Action<SharedDownload> _remove;
    private readonly Action<int> _activeDelta;
    private SafeFileHandle? _file;
    private CancellationTokenSource? _cts;
    private bool _running;
    private bool _disposed;
    private long _have;          // bytes [0, _have) are in the temp file
    private int _lingerGeneration;

    public SharedDownload(string assetId, long size, Func<IImmichClient?> client, Deliver deliver, Failed failed,
        Action<SharedDownload> remove, Action<int> activeDelta)
    {
        _assetId = assetId;
        Size = size;
        _client = client;
        _deliver = deliver;
        _failed = failed;
        _remove = remove;
        _activeDelta = activeDelta;
    }

    public string AssetId => _assetId;
    public long Size { get; }

    /// <summary>Bytes downloaded so far (from the start of the file).</summary>
    public long Have { get { lock (_gate) return _have; } }

    /// <summary>
    /// Adds a request. Whatever is already downloaded is handed over at once; the rest follows as it
    /// arrives. Returns false if this download was already thrown away (the caller makes a new one).
    /// </summary>
    public bool Attach(Waiter w)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            _waiters.Add(w);
            _lingerGeneration++;
            DeliverLocked(w);
            if (w.Next >= w.End) _waiters.Remove(w);
            if (_waiters.Count > 0 && _have < Size && !_running) Start();
            else if (_waiters.Count == 0 && !_running) ScheduleLingerLocked();
            return true;
        }
    }

    /// <summary>Windows canceled a request (file closed, app moved on).</summary>
    public void Cancel(long transferKey, long offset, long length)
    {
        lock (_gate)
        {
            _waiters.RemoveAll(w => w.TransferKey == transferKey &&
                                    (length <= 0 || (w.Next < offset + length && offset < w.End)));
            if (_waiters.Count == 0) ScheduleLingerLocked();
        }
    }

    /// <summary>Stops everything (app disconnecting).</summary>
    public void Abort()
    {
        Waiter[] dropped;
        lock (_gate)
        {
            dropped = _waiters.ToArray();
            _waiters.Clear();
            _cts?.Cancel();
            DisposeLocked();
        }
        // Tell Windows rather than leave the requests hanging until its 60 s timeout.
        foreach (var w in dropped)
        {
            try { _failed(w); } catch { }
        }
    }

    private void Start()
    {
        _running = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _activeDelta(+1);
        _ = Task.Run(() => RunAsync(ct));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        Exception? error = null;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await DownloadAsync(buffer, ct).ConfigureAwait(false);
                    error = null;
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    error = null;
                    break;
                }
                catch (Exception ex)
                {
                    error = ex;
                    if (attempt >= MaxAttempts || !HasWaiters()) break;
                    Log.Info($"Download of {_assetId} interrupted at {Have}/{Size}, retrying ({ex.GetBaseException().Message})");
                    await Task.Delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { error = null; }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            List<Waiter> failed = new();
            lock (_gate)
            {
                _running = false;
                if (error != null)
                {
                    Log.Warn($"Download of {_assetId} failed at {_have}/{Size}", error);
                    failed.AddRange(_waiters);
                    _waiters.Clear();
                }
                ScheduleLingerLocked();
            }
            foreach (var w in failed)
            {
                try { _failed(w); } catch { }
            }
            _activeDelta(-1);
        }
    }

    private async Task DownloadAsync(byte[] buffer, CancellationToken ct)
    {
        var client = _client() ?? throw new InvalidOperationException("Not connected to Immich");
        long from;
        lock (_gate)
        {
            if (_disposed) return;
            _file ??= OpenTempFile();
            from = _have;
        }
        if (from >= Size) return;

        await using var stream = await client.OpenOriginalAsync(_assetId, from, Size, ct).ConfigureAwait(false);
        var offset = from;
        while (offset < Size)
        {
            var want = (int)Math.Min(ChunkSize, Size - offset);
            var got = 0;
            while (got < want)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(got, want - got), ct).ConfigureAwait(false);
                if (n == 0) throw new IOException($"Server sent {offset + got} bytes, expected {Size}");
                got += n;
            }

            lock (_gate)
            {
                if (_disposed) return;
                RandomAccess.Write(_file!, buffer.AsSpan(0, got), offset);
                offset += got;
                _have = offset;
                foreach (var w in _waiters.ToArray())
                {
                    DeliverLocked(w);
                    if (w.Next >= w.End) _waiters.Remove(w);
                }
                // Nobody waiting any more: stop after this piece rather than finish a file no one wants.
                // A request that comes back picks up from here.
                if (_waiters.Count == 0) return;
            }
        }
    }

    /// <summary>Hands a request everything it can have from the temp file. Caller holds the lock.</summary>
    private void DeliverLocked(Waiter w)
    {
        if (_file == null || w.Next >= w.End) return;
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            while (w.Next < w.End && w.Next < _have)
            {
                var length = (int)Math.Min(ChunkSize, Math.Min(w.End, _have) - w.Next);
                var read = RandomAccess.Read(_file, buffer.AsSpan(0, length), w.Next);
                if (read != length) throw new IOException("Temp download file is shorter than expected");
                if (!_deliver(w, buffer, w.Next, length))
                {
                    w.Next = w.End;   // Windows doesn't want it any more; drop the request
                    return;
                }
                w.Next += length;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private bool HasWaiters()
    {
        lock (_gate) return _waiters.Count > 0;
    }

    /// <summary>Throws the download away if nothing uses it for a while. Caller holds the lock.</summary>
    private void ScheduleLingerLocked()
    {
        var generation = ++_lingerGeneration;
        _ = Task.Delay(Linger).ContinueWith(_ =>
        {
            lock (_gate)
            {
                if (generation != _lingerGeneration || _waiters.Count > 0 || _running || _disposed) return;
                DisposeLocked();
            }
        }, TaskScheduler.Default);
    }

    private void DisposeLocked()
    {
        if (_disposed) return;
        _disposed = true;
        _file?.Dispose();
        _file = null;
        _remove(this);
    }

    private static SafeFileHandle OpenTempFile()
    {
        var dir = Path.Combine(AppInfo.DataDir, "downloading");
        Directory.CreateDirectory(dir);
        return File.OpenHandle(Path.Combine(dir, Guid.NewGuid().ToString("N") + ".part"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, FileOptions.DeleteOnClose);
    }
}
