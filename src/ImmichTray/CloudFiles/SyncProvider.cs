using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ImmichTray.Immich;
using ImmichTray.Views;
using static ImmichTray.CloudFiles.Native;

namespace ImmichTray.CloudFiles;

/// <summary>Per-folder locks so a refresh and an Explorer request never edit the same folder at once.</summary>
internal sealed class DirectoryLocks
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    public SemaphoreSlim For(string relPath) => _locks.GetOrAdd(relPath, _ => new SemaphoreSlim(1, 1));
}

/// <summary>
/// Connects to the sync root and answers the Cloud Files callbacks: listing folders,
/// downloading file contents, and refusing deletes/renames (the folder is read-only).
/// </summary>
internal sealed class SyncProvider : IDisposable
{
    private static volatile SyncProvider? _current;

    // Pieces handed to Windows as they arrive. Small, so a slow server still makes progress well inside
    // the 60 second limit Windows allows between transfers (after that it cancels and the app sees an error).
    private const int ChunkSize = 64 * 1024;        // must be a multiple of 4096
    private const int PlaceholderBatchSize = 1000;
    private static readonly TimeSpan ListingDeadline = TimeSpan.FromSeconds(45);

    private readonly string _root;
    private readonly Func<VirtualTree?> _tree;
    private readonly Func<IImmichClient?> _client;
    private readonly DirectoryLocks _locks;
    private readonly int _pid = Environment.ProcessId;
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<int, CancellationTokenSource>> _transfers = new();
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<TreeEntry>>> _listings = new(StringComparer.OrdinalIgnoreCase);
    private int _opCounter;
    private long _connectionKey;
    private unsafe CF_CALLBACK_REGISTRATION* _table;

    public SyncProvider(string root, Func<VirtualTree?> tree, Func<IImmichClient?> client, DirectoryLocks locks)
    {
        _root = Path.GetFullPath(root).TrimEnd('\\');
        _tree = tree;
        _client = client;
        _locks = locks;
    }

    public bool IsConnected => _connectionKey != 0;

    /// <summary>Raised when a download starts or ends, for the tray status text.</summary>
    public event Action<int>? ActiveDownloadsChanged;
    private int _activeDownloads;

    public unsafe void Connect()
    {
        if (IsConnected) return;
        _current = this;
        _table = (CF_CALLBACK_REGISTRATION*)NativeMemory.AllocZeroed(7, (nuint)sizeof(CF_CALLBACK_REGISTRATION));
        _table[0] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE_FETCH_DATA, Callback = (IntPtr)(delegate* unmanaged[Stdcall]<CF_CALLBACK_INFO*, byte*, void>)&OnFetchData };
        _table[1] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE_CANCEL_FETCH_DATA, Callback = (IntPtr)(delegate* unmanaged[Stdcall]<CF_CALLBACK_INFO*, byte*, void>)&OnCancelFetchData };
        _table[2] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS, Callback = (IntPtr)(delegate* unmanaged[Stdcall]<CF_CALLBACK_INFO*, byte*, void>)&OnFetchPlaceholders };
        _table[3] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE_CANCEL_FETCH_PLACEHOLDERS, Callback = (IntPtr)(delegate* unmanaged[Stdcall]<CF_CALLBACK_INFO*, byte*, void>)&OnCancelFetchPlaceholders };
        _table[4] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE_NOTIFY_DELETE, Callback = (IntPtr)(delegate* unmanaged[Stdcall]<CF_CALLBACK_INFO*, byte*, void>)&OnNotifyDelete };
        _table[5] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE_NOTIFY_RENAME, Callback = (IntPtr)(delegate* unmanaged[Stdcall]<CF_CALLBACK_INFO*, byte*, void>)&OnNotifyRename };
        _table[6] = new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE_NONE, Callback = IntPtr.Zero };

        var hr = CfConnectSyncRoot(_root, _table, IntPtr.Zero,
            CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO | CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH, out var key);
        if (hr < 0)
        {
            NativeMemory.Free(_table);
            _table = null;
            _current = null;
            ThrowIfFailed(hr, "Connecting to the Immich folder");
        }
        _connectionKey = key;
        Log.Info($"Connected sync root {_root}");
    }

    public unsafe void Disconnect()
    {
        if (!IsConnected) return;
        foreach (var ops in _transfers.Values)
            foreach (var cts in ops.Values) cts.Cancel();
        var hr = CfDisconnectSyncRoot(_connectionKey);
        if (hr < 0) Log.Warn($"CfDisconnectSyncRoot returned 0x{hr:X8}");
        _connectionKey = 0;
        if (_current == this) _current = null;
        if (_table != null) { NativeMemory.Free(_table); _table = null; }
        Log.Info("Disconnected sync root");
    }

    public void Dispose() => Disconnect();

    /// <summary>Forget cached listings (after a refresh or a settings change).</summary>
    public void ClearListingCache() => _listings.Clear();

    // ------------------------------------------------------------------ native entry points

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private unsafe static void OnFetchData(CF_CALLBACK_INFO* info, byte* p) => Guard(info, p, (s, r) => s.FetchData(r));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private unsafe static void OnCancelFetchData(CF_CALLBACK_INFO* info, byte* p) => Guard(info, p, (s, r) => s.CancelFetchData(r));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private unsafe static void OnFetchPlaceholders(CF_CALLBACK_INFO* info, byte* p) => Guard(info, p, (s, r) => s.FetchPlaceholders(r));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private unsafe static void OnCancelFetchPlaceholders(CF_CALLBACK_INFO* info, byte* p) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private unsafe static void OnNotifyDelete(CF_CALLBACK_INFO* info, byte* p) => Guard(info, p, (s, r) => s.Ack(r, CF_OPERATION_TYPE_ACK_DELETE, "delete"));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private unsafe static void OnNotifyRename(CF_CALLBACK_INFO* info, byte* p) => Guard(info, p, (s, r) => s.Ack(r, CF_OPERATION_TYPE_ACK_RENAME, "rename"));

    /// <summary>Everything the handlers need, copied out of the native structs before the callback returns.</summary>
    private sealed record Request(
        long ConnectionKey, long TransferKey, long RequestKey, string FullPath, string? Identity, long FileSize,
        int ProcessId, long RequiredOffset, long RequiredLength, long OptionalOffset, long OptionalLength);

    private unsafe static void Guard(CF_CALLBACK_INFO* info, byte* p, Action<SyncProvider, Request> handler)
    {
        try
        {
            var self = _current;
            if (self == null) return;
            handler(self, Copy(info, p));
        }
        catch (Exception ex)
        {
            Log.Error("Cloud Files callback failed", ex);
        }
    }

    private unsafe static Request Copy(CF_CALLBACK_INFO* info, byte* p)
    {
        var dos = info->VolumeDosName != IntPtr.Zero ? Marshal.PtrToStringUni(info->VolumeDosName) ?? "" : "";
        var norm = info->NormalizedPath != IntPtr.Zero ? Marshal.PtrToStringUni(info->NormalizedPath) ?? "" : "";
        string? identity = null;
        if (info->FileIdentity != IntPtr.Zero && info->FileIdentityLength > 0 && info->FileIdentityLength <= 4096)
            identity = Encoding.UTF8.GetString((byte*)info->FileIdentity, (int)info->FileIdentityLength);
        var pid = info->ProcessInfo != IntPtr.Zero ? *(int*)((byte*)info->ProcessInfo + 4) : 0;
        return new Request(
            info->ConnectionKey, info->TransferKey, info->RequestKey, dos + norm, identity, info->FileSize, pid,
            *(long*)(p + CallbackParams.FetchData_RequiredFileOffset),
            *(long*)(p + CallbackParams.FetchData_RequiredLength),
            *(long*)(p + CallbackParams.FetchData_OptionalFileOffset),
            *(long*)(p + CallbackParams.FetchData_OptionalLength));
    }

    private string Relative(string fullPath)
    {
        var full = fullPath.TrimEnd('\\');
        if (full.Equals(_root, StringComparison.OrdinalIgnoreCase)) return "";
        if (full.StartsWith(_root + "\\", StringComparison.OrdinalIgnoreCase)) return full[(_root.Length + 1)..];
        return full;
    }

    // ------------------------------------------------------------------ delete / rename

    private unsafe void Ack(Request r, int opType, string what)
    {
        var allowed = r.ProcessId == _pid;
        if (!allowed) Log.Info($"Blocked {what} of {r.FullPath} (pid {r.ProcessId}); the Immich folder is read-only");
        var op = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO), Type = opType,
            ConnectionKey = r.ConnectionKey, TransferKey = r.TransferKey, RequestKey = r.RequestKey,
        };
        var par = new CF_OPERATION_PARAMETERS
        {
            ParamSize = CF_OPERATION_PARAMETERS.SizeAck,
            CompletionStatus = allowed ? STATUS_SUCCESS : STATUS_ACCESS_DENIED,
        };
        var hr = CfExecute(&op, &par);
        if (hr < 0) Log.Warn($"Ack {what} returned 0x{hr:X8}");
    }

    // ------------------------------------------------------------------ folder listing

    private void FetchPlaceholders(Request r)
    {
        var rel = Relative(r.FullPath);
        _ = Task.Run(async () =>
        {
            IReadOnlyList<TreeEntry>? entries = null;
            var status = STATUS_SUCCESS;
            var gate = _locks.For(rel);
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var listing = _listings.GetOrAdd(rel, key => StartListing(key));
                var finished = await Task.WhenAny(listing, Task.Delay(ListingDeadline)).ConfigureAwait(false);
                if (finished != listing)
                {
                    // Keep loading in the background; the next attempt gets the cached result.
                    Log.Warn($"Listing '{rel}' is taking a long time; Explorer will need to retry");
                    status = STATUS_UNSUCCESSFUL;
                }
                else if (listing.IsFaulted)
                {
                    _listings.TryRemove(rel, out _);
                    Log.Warn($"Listing '{rel}' failed", listing.Exception?.GetBaseException());
                    status = STATUS_UNSUCCESSFUL;
                }
                else
                {
                    entries = listing.Result;
                    _listings.TryRemove(rel, out _);
                }

                TransferPlaceholders(r, entries ?? Array.Empty<TreeEntry>(), status);
            }
            catch (Exception ex)
            {
                Log.Error($"Fetching placeholders for '{rel}' failed", ex);
                try { TransferPlaceholders(r, Array.Empty<TreeEntry>(), STATUS_UNSUCCESSFUL); } catch { }
            }
            finally
            {
                gate.Release();
            }
        });
    }

    private Task<IReadOnlyList<TreeEntry>> StartListing(string rel)
    {
        var tree = _tree() ?? throw new InvalidOperationException("Not connected to Immich");
        var task = tree.ListAsync(rel);
        // Drop cached results that nobody picks up within a few minutes.
        _ = task.ContinueWith(_ => Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(__ =>
        {
            if (_listings.TryGetValue(rel, out var t) && ReferenceEquals(t, task)) _listings.TryRemove(rel, out var _);
        }), TaskScheduler.Default);
        return task;
    }

    private unsafe void TransferPlaceholders(Request r, IReadOnlyList<TreeEntry> entries, int status)
    {
        var op = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO), Type = CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS,
            ConnectionKey = r.ConnectionKey, TransferKey = r.TransferKey, RequestKey = r.RequestKey,
        };

        if (status != STATUS_SUCCESS || entries.Count == 0)
        {
            var par = new CF_OPERATION_PARAMETERS
            {
                ParamSize = CF_OPERATION_PARAMETERS.SizeTransferPlaceholders,
                Flags = status == STATUS_SUCCESS ? CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_DISABLE_ON_DEMAND_POPULATION : 0,
                CompletionStatus = status,
            };
            var hr0 = CfExecute(&op, &par);
            if (hr0 < 0) Log.Warn($"TransferPlaceholders (empty/failed) for {r.FullPath} returned 0x{hr0:X8}");
            return;
        }

        for (var start = 0; start < entries.Count; start += PlaceholderBatchSize)
        {
            var count = Math.Min(PlaceholderBatchSize, entries.Count - start);
            var last = start + count >= entries.Count;
            using var batch = new PlaceholderBatch(entries, start, count);
            var par = new CF_OPERATION_PARAMETERS
            {
                ParamSize = CF_OPERATION_PARAMETERS.SizeTransferPlaceholders,
                Flags = last ? CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_DISABLE_ON_DEMAND_POPULATION : 0,
                CompletionStatus = STATUS_SUCCESS,
                TP_PlaceholderTotalCount = entries.Count,
                TP_PlaceholderArray = (IntPtr)batch.Array,
                TP_PlaceholderCount = (uint)count,
            };
            var hr = CfExecute(&op, &par);
            if (hr < 0) Log.Warn($"TransferPlaceholders for {r.FullPath} returned 0x{hr:X8}");
        }
        Log.Info($"Listed {entries.Count} items in '{Relative(r.FullPath)}'");
    }

    // ------------------------------------------------------------------ downloads

    private void FetchData(Request r)
    {
        var assetId = Placeholders.AssetIdFromIdentity(r.Identity);
        var fileSize = r.FileSize;
        var start = r.RequiredOffset;
        var end = r.RequiredLength < 0 ? fileSize : r.RequiredOffset + r.RequiredLength;
        // Also fetch the optional range when it continues straight on from the required one.
        if (r.OptionalLength > 0 && r.OptionalOffset <= end && r.OptionalOffset + r.OptionalLength > end)
            end = r.OptionalOffset + r.OptionalLength;
        end = Math.Min(AlignUp(end), fileSize);

        if (assetId == null || _client() is not { } client)
        {
            Log.Warn($"Cannot download {r.FullPath}: {(assetId == null ? "unknown file" : "not connected")}");
            Fail(r, start, end - start);
            return;
        }

        var opId = Interlocked.Increment(ref _opCounter);
        var cts = new CancellationTokenSource();
        _transfers.GetOrAdd(r.TransferKey, _ => new()).TryAdd(opId, cts);
        ActiveDownloadsChanged?.Invoke(Interlocked.Increment(ref _activeDownloads));

        _ = Task.Run(async () =>
        {
            var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
            var offset = start;
            try
            {
                await using var stream = await client.OpenOriginalAsync(assetId, start, end, cts.Token).ConfigureAwait(false);
                while (offset < end)
                {
                    var want = (int)Math.Min(ChunkSize, end - offset);
                    var got = 0;
                    while (got < want)
                    {
                        var n = await stream.ReadAsync(buffer.AsMemory(got, want - got), cts.Token).ConfigureAwait(false);
                        if (n == 0) break;
                        got += n;
                    }
                    if (got < want)
                        throw new IOException($"Server sent {offset + got - start} bytes, expected {end - start}");

                    if (!Transfer(r, buffer, offset, got)) return;
                    offset += got;
                    CfReportProviderProgress(r.ConnectionKey, r.TransferKey, fileSize, offset);
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                Log.Info($"Download of {r.FullPath} canceled");
            }
            catch (Exception ex)
            {
                Log.Warn($"Download of {r.FullPath} failed at {offset}/{fileSize}", ex);
                Fail(r, offset, end - offset);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                if (_transfers.TryGetValue(r.TransferKey, out var ops))
                {
                    ops.TryRemove(opId, out _);
                    if (ops.IsEmpty) _transfers.TryRemove(r.TransferKey, out _);
                }
                cts.Dispose();
                ActiveDownloadsChanged?.Invoke(Interlocked.Decrement(ref _activeDownloads));
            }
        });
    }

    private void CancelFetchData(Request r)
    {
        if (_transfers.TryGetValue(r.TransferKey, out var ops))
            foreach (var cts in ops.Values)
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            }
    }

    private unsafe bool Transfer(Request r, byte[] buffer, long offset, int length)
    {
        fixed (byte* b = buffer)
        {
            var op = new CF_OPERATION_INFO
            {
                StructSize = (uint)sizeof(CF_OPERATION_INFO), Type = CF_OPERATION_TYPE_TRANSFER_DATA,
                ConnectionKey = r.ConnectionKey, TransferKey = r.TransferKey, RequestKey = r.RequestKey,
            };
            var par = new CF_OPERATION_PARAMETERS
            {
                ParamSize = CF_OPERATION_PARAMETERS.SizeTransferData,
                CompletionStatus = STATUS_SUCCESS,
                TD_Buffer = (IntPtr)b,
                TD_Offset = offset,
                TD_Length = length,
            };
            var hr = CfExecute(&op, &par);
            if (hr < 0)
            {
                // Usually means the request was canceled (file closed) or the range is no longer needed.
                Log.Info($"TransferData {r.FullPath} @{offset} returned 0x{hr:X8}; stopping");
                return false;
            }
            return true;
        }
    }

    private unsafe void Fail(Request r, long offset, long length)
    {
        var op = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO), Type = CF_OPERATION_TYPE_TRANSFER_DATA,
            ConnectionKey = r.ConnectionKey, TransferKey = r.TransferKey, RequestKey = r.RequestKey,
        };
        var par = new CF_OPERATION_PARAMETERS
        {
            ParamSize = CF_OPERATION_PARAMETERS.SizeTransferData,
            CompletionStatus = STATUS_UNSUCCESSFUL,
            TD_Buffer = IntPtr.Zero,
            TD_Offset = offset,
            TD_Length = Math.Max(0, length),
        };
        CfExecute(&op, &par);
    }

    internal static long AlignUp(long value) => (value + 4095) & ~4095L;
}
