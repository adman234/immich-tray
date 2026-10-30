using System.Text.Json;
using ImmichTray.Views;
using static ImmichTray.CloudFiles.Native;

namespace ImmichTray.CloudFiles;

/// <summary>Remembers per-folder change signatures between runs.</summary>
internal sealed class SyncState
{
    public string Fingerprint { get; set; } = "";
    public string MeId { get; set; } = "";
    public Dictionary<string, string> Signatures { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static string FilePath => Path.Combine(AppInfo.DataDir, "state.json");

    public static SyncState Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<SyncState>(File.ReadAllText(FilePath));
                if (s != null)
                {
                    s.Signatures = new Dictionary<string, string>(s.Signatures ?? new(), StringComparer.OrdinalIgnoreCase);
                    return s;
                }
            }
        }
        catch (Exception ex) { Log.Warn("Could not read sync state", ex); }
        return new SyncState();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) { Log.Warn("Could not save sync state", ex); }
    }
}

/// <summary>
/// Keeps folders that have already been opened in step with Immich, and looks after
/// downloaded files (pinning, freeing space, the optional size limit).
/// </summary>
internal sealed class FolderSync
{
    private readonly string _root;
    private readonly VirtualTree _tree;
    private readonly DirectoryLocks _locks;
    private readonly SyncState _state;
    private readonly SemaphoreSlim _hydrations = new(3, 3);

    public FolderSync(string root, VirtualTree tree, DirectoryLocks locks, SyncState state)
    {
        _root = Path.GetFullPath(root).TrimEnd('\\');
        _tree = tree;
        _locks = locks;
        _state = state;
    }

    // ------------------------------------------------------------------ refresh

    public async Task RefreshAsync(bool force, CancellationToken ct)
    {
        var fingerprint = _tree.Settings.ContentFingerprint;
        if (_state.Fingerprint != fingerprint)
        {
            _state.Signatures.Clear();
            _state.Fingerprint = fingerprint;
            force = true;
        }

        _tree.InvalidateCaches();
        var started = DateTime.UtcNow;
        var diffed = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await DiffAsync("", ct);
        var queue = new Queue<string>(SubDirs(""));
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var rel = queue.Dequeue();
            var full = Full(rel);
            if (!Directory.Exists(full) || !Placeholders.IsPopulatedDirectory(full)) continue;
            seen.Add(rel);

            string? sig = null;
            try { sig = await _tree.GetSignatureAsync(rel, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Signature for '{rel}' failed", ex); }

            if (force || sig == null || !_state.Signatures.TryGetValue(rel, out var old) || old != sig)
            {
                if (await DiffAsync(rel, ct))
                {
                    diffed++;
                    if (sig != null) _state.Signatures[rel] = sig; else _state.Signatures.Remove(rel);
                }
            }
            foreach (var child in SubDirs(rel)) queue.Enqueue(child);
        }

        foreach (var stale in _state.Signatures.Keys.Where(k => !seen.Contains(k)).ToList())
            _state.Signatures.Remove(stale);
        _state.Save();
        Log.Info($"Refresh done: checked {seen.Count} open folders, re-listed {diffed}, {(DateTime.UtcNow - started).TotalSeconds:F1}s");
    }

    private IEnumerable<string> SubDirs(string rel)
    {
        string[] dirs;
        try { dirs = Directory.GetDirectories(Full(rel)); }
        catch (Exception ex) { Log.Warn($"Could not list '{rel}'", ex); yield break; }
        foreach (var d in dirs)
            yield return rel.Length == 0 ? Path.GetFileName(d) : rel + "\\" + Path.GetFileName(d);
    }

    /// <summary>Makes one folder on disk match the server. Returns false if the server could not be asked.</summary>
    private async Task<bool> DiffAsync(string rel, CancellationToken ct)
    {
        IReadOnlyList<TreeEntry> desired;
        try { desired = await _tree.ListAsync(rel, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Could not list '{rel}' from Immich", ex);
            return false;
        }

        var gate = _locks.For(rel);
        await gate.WaitAsync(ct);
        try
        {
            var full = Full(rel);
            if (!Directory.Exists(full)) return true;

            var wanted = new Dictionary<string, TreeEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in desired) wanted.TryAdd(e.Name, e);
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int removed = 0, updated = 0;

            foreach (var item in new DirectoryInfo(full).EnumerateFileSystemInfos())
            {
                present.Add(item.Name);
                if (!Placeholders.IsPlaceholder(item.FullName)) continue; // a file the user put here: leave it

                var isDir = (item.Attributes & FileAttributes.Directory) != 0;
                if (!wanted.TryGetValue(item.Name, out var want) || want.IsDirectory != isDir)
                {
                    if (RemovePlaceholderTree(item.FullName)) { present.Remove(item.Name); removed++; }
                    continue;
                }
                if (isDir) continue;

                var identity = Placeholders.ReadIdentity(item.FullName);
                var length = ((FileInfo)item).Length;
                if (identity != want.Identity || length != want.Size)
                {
                    if (Placeholders.Update(item.FullName, want, dehydrate: true)) updated++;
                }
                else if (Math.Abs((item.LastWriteTimeUtc - want.Modified.UtcDateTime).TotalSeconds) > 2)
                {
                    if (Placeholders.Update(item.FullName, want, dehydrate: false)) updated++;
                }
            }

            var toCreate = desired.Where(e => !present.Contains(e.Name)).ToList();
            var created = toCreate.Count > 0 ? Placeholders.Create(full, toCreate) : 0;
            if (created + removed + updated > 0)
                Log.Info($"'{rel}': +{created} -{removed} ~{updated}");
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Deletes placeholders under a path. Files the user added themselves are kept,
    /// and so are the folders holding them. Returns true if the path is gone.
    /// </summary>
    public static bool RemovePlaceholderTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                if (Placeholders.IsPlaceholder(path) && !Placeholders.IsPopulatedDirectory(path))
                {
                    // Never opened: nothing on disk inside. Delete without listing it (listing would fetch it).
                    try { Directory.Delete(path, false); return true; }
                    catch (IOException) { /* has real children after all; fall through */ }
                }
                foreach (var child in Directory.EnumerateFileSystemEntries(path).ToList())
                    RemovePlaceholderTree(child);
                if (!Directory.EnumerateFileSystemEntries(path).Any())
                {
                    Directory.Delete(path, false);
                    return true;
                }
                return false;
            }
            if (File.Exists(path))
            {
                if (!Placeholders.IsPlaceholder(path)) return false;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return true;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not remove {path}", ex);
            return false;
        }
    }

    // ------------------------------------------------------------------ downloaded files

    /// <summary>
    /// Downloads files marked "Always keep on this device", frees files marked
    /// "Free up space", and enforces the optional size limit.
    /// </summary>
    public async Task MaintainFilesAsync(long? limitBytes, CancellationToken ct)
    {
        var toHydrate = new List<string>();
        var toDehydrate = new List<string>();
        var cached = new List<(string Path, long Size, DateTime Access)>();
        long cachedTotal = 0;

        void Walk(string dir, bool parentPinned)
        {
            IEnumerable<FileSystemInfo> items;
            try { items = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
            catch (Exception ex) { Log.Warn($"Could not scan {dir}", ex); return; }

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var attrs = (uint)item.Attributes;
                var pinned = (attrs & FILE_ATTRIBUTE_PINNED) != 0 || (parentPinned && (attrs & FILE_ATTRIBUTE_UNPINNED) == 0);
                var unpinned = (attrs & FILE_ATTRIBUTE_UNPINNED) != 0;

                if ((attrs & FILE_ATTRIBUTE_DIRECTORY) != 0)
                {
                    if (!Placeholders.IsPlaceholder(item.FullName)) continue;
                    // A pinned folder that was never opened gets filled in so its files can download.
                    if (Placeholders.IsPopulatedDirectory(item.FullName) || pinned)
                        Walk(item.FullName, pinned);
                    continue;
                }

                // Placeholders are reparse points (this process runs with placeholders exposed).
                if ((attrs & FILE_ATTRIBUTE_REPARSE_POINT) == 0) continue; // a file the user added

                var hydrated = Placeholders.IsHydrated(attrs);
                if (pinned && !hydrated) toHydrate.Add(item.FullName);
                else if (unpinned && hydrated) toDehydrate.Add(item.FullName);
                else if (hydrated && !pinned)
                {
                    var size = ((FileInfo)item).Length;
                    cached.Add((item.FullName, size, item.LastAccessTimeUtc));
                    cachedTotal += size;
                }
            }
        }

        await Task.Run(() => Walk(_root, false), ct);

        foreach (var path in toDehydrate)
        {
            if (Placeholders.Dehydrate(path)) Placeholders.ClearPin(path);
        }

        if (limitBytes is long limit && cachedTotal > limit)
        {
            var freed = 0L;
            foreach (var f in cached.OrderBy(c => c.Access))
            {
                if (cachedTotal - freed <= limit) break;
                if (Placeholders.Dehydrate(f.Path)) freed += f.Size;
            }
            Log.Info($"Size limit: freed {freed / (1024 * 1024)} MB of downloaded files");
        }

        if (toHydrate.Count > 0)
        {
            Log.Info($"Downloading {toHydrate.Count} files marked 'Always keep on this device'");
            var tasks = toHydrate.Select(async path =>
            {
                await _hydrations.WaitAsync(ct);
                try
                {
                    var hr = await Task.Run(() => Placeholders.Hydrate(path), ct);
                    if (hr < 0) Log.Warn($"Could not download pinned file {path}: 0x{hr:X8}");
                }
                finally { _hydrations.Release(); }
            });
            await Task.WhenAll(tasks);
        }
    }

    private string Full(string rel) => rel.Length == 0 ? _root : Path.Combine(_root, rel);
}
