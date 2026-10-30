using ImmichTray.CloudFiles;
using ImmichTray.Immich;
using ImmichTray.Settings;
using ImmichTray.Views;

namespace ImmichTray;

public enum MountState { NotConfigured, Unmounted, Waiting, Mounting, Mounted, Error }

/// <summary>Owns the Immich connection and the Explorer folder: mount, unmount, refresh.</summary>
internal sealed class MountManager : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DirectoryLocks _locks = new();
    private readonly SyncState _state = SyncState.Load();
    private ImmichClient? _client;
    private VirtualTree? _tree;
    private SyncProvider? _provider;
    private FolderSync? _sync;
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _maintainDebounce;
    private CancellationTokenSource _cts = new();
    private readonly ChangeTracker _tracker = new();
    private readonly object _pendingLock = new();
    private ChangeSet? _pending;
    private CancellationTokenSource? _quickCts;

    /// <summary>How often the quick change check asks Immich what changed.</summary>
    public static TimeSpan QuickCheckInterval { get; set; } = TimeSpan.FromSeconds(15);

    public MountManager(AppSettings settings)
    {
        Settings = settings;
    }

    public AppSettings Settings { get; private set; }
    public MountState State { get; private set; } = MountState.Unmounted;
    public string StatusText { get; private set; } = "Not mounted";
    public bool ServerReachable { get; private set; }
    public DateTime? LastRefresh { get; private set; }
    public int ActiveDownloads { get; private set; }
    public string? MountedPath { get; private set; }

    /// <summary>Raised on any thread when the state or status text changes.</summary>
    public event Action? Changed;

    public IImmichClient? Client => _client;

    private void SetState(MountState state, string text)
    {
        State = state;
        StatusText = text;
        Log.Info($"State: {state} - {text}");
        Changed?.Invoke();
    }

    private ImmichClient EnsureClient()
    {
        if (_client == null)
        {
            _client = new ImmichClient(Settings.ServerUrl, Settings.ApiKey);
            _tree = new VirtualTree(_client, Settings, _state.MeId);
        }
        return _client;
    }

    private void ResetClient()
    {
        _tracker.Reset();
        _client?.Dispose();
        _client = null;
        _tree = null;
    }

    // ------------------------------------------------------------------ startup

    /// <summary>Applies the mount mode at app start.</summary>
    public async Task StartAsync()
    {
        if (!Settings.IsConfigured)
        {
            SetState(MountState.NotConfigured, "Not set up yet");
            return;
        }
        switch (Settings.MountMode)
        {
            case MountMode.AppStart:
                await MountAsync();
                break;
            case MountMode.Manual:
                if (Settings.ManualMounted) await MountAsync();
                else SetState(MountState.Unmounted, "Not mounted");
                break;
            case MountMode.WhenReachable:
                _ = WaitThenMountAsync(_cts.Token);
                break;
        }
    }

    private async Task WaitThenMountAsync(CancellationToken ct)
    {
        SetState(MountState.Waiting, "Waiting for the server");
        while (!ct.IsCancellationRequested)
        {
            if (await EnsureClient().PingAsync(ct))
            {
                await MountAsync();
                return;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
        }
    }

    // ------------------------------------------------------------------ mount / unmount

    public async Task<bool> MountAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_provider?.IsConnected == true) return true;
            if (!Settings.IsConfigured)
            {
                SetState(MountState.NotConfigured, "Not set up yet");
                return false;
            }
            SetState(MountState.Mounting, "Mounting");

            var client = EnsureClient();
            ServerReachable = await client.PingAsync();
            if (ServerReachable)
            {
                try
                {
                    var me = await client.GetMeAsync();
                    if (me.Id != _state.MeId)
                    {
                        _state.MeId = me.Id;
                        _state.Save();
                        _tree = new VirtualTree(client, Settings, me.Id);
                    }
                }
                catch (ImmichApiException ex) when (ex.Status is 401 or 403)
                {
                    SetState(MountState.Error, ex.Message);
                    return false;
                }
            }

            var path = Path.GetFullPath(Settings.MountFolder).TrimEnd('\\');
            var registered = SyncRootRegistrar.GetRegisteredPath();
            if (registered != null && !string.Equals(Path.GetFullPath(registered).TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"Folder moved from {registered} to {path}; removing the old one");
                RemoveFolder(registered);
                registered = null;
            }
            if (registered == null)
            {
                var problem = CheckFolder(path);
                if (problem != null)
                {
                    SetState(MountState.Error, problem);
                    return false;
                }
                await SyncRootRegistrar.RegisterAsync(path);
            }
            else
            {
                SyncRootRegistrar.RegisterThumbnailProvider();
            }

            _provider = new SyncProvider(path, () => _tree, () => _client, _locks);
            _provider.ActiveDownloadsChanged += n => { ActiveDownloads = n; Changed?.Invoke(); };
            _provider.Connect();
            _sync = new FolderSync(path, _tree!, _locks, _state);
            MountedPath = path;
            StartWatcher(path);

            if (Settings.MountMode == MountMode.Manual && !Settings.ManualMounted)
            {
                Settings.ManualMounted = true;
                SettingsStore.Save(Settings);
            }

            SetState(MountState.Mounted, ServerReachable ? "Connected" : "Mounted (server not reachable)");
        }
        catch (Exception ex)
        {
            Log.Error("Mount failed", ex);
            SetState(MountState.Error, "Could not mount: " + ex.Message);
            return false;
        }
        finally
        {
            _gate.Release();
        }

        _ = RefreshAsync(force: false);
        StartQuickChecks();
        return true;
    }

    /// <summary>Removes the folder from Explorer and deletes its online-only files and downloads.</summary>
    public async Task UnmountAsync()
    {
        await _gate.WaitAsync();
        try
        {
            StopQuickChecks();
            StopWatcher();
            var path = MountedPath ?? SyncRootRegistrar.GetRegisteredPath();
            _provider?.Disconnect();
            _provider = null;
            _sync = null;
            MountedPath = null;
            if (path != null) RemoveFolder(path);
            if (Settings.MountMode == MountMode.Manual && Settings.ManualMounted)
            {
                Settings.ManualMounted = false;
                SettingsStore.Save(Settings);
            }
            SetState(Settings.IsConfigured ? MountState.Unmounted : MountState.NotConfigured, "Not mounted");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Cleans up a registered folder: placeholders deleted, user files kept, registration removed.</summary>
    public static void RemoveFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(path).ToList())
                    FolderSync.RemovePlaceholderTree(child);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Cleaning {path} failed", ex);
        }
        SyncRootRegistrar.Unregister();
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not delete the empty folder {path}", ex);
        }
    }

    /// <summary>Returns a problem description, or null if the folder can be used.</summary>
    public static string? CheckFolder(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var rootDir = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(rootDir) || full.TrimEnd('\\').Length <= rootDir.TrimEnd('\\').Length)
                return "Pick a folder, not a whole drive.";
            var drive = new DriveInfo(rootDir);
            if (drive.DriveType != DriveType.Fixed)
                return "The folder must be on a local drive.";
            if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                return "The folder must be on an NTFS drive.";
            if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
                return $"The folder {full} is not empty. Pick an empty or new folder.";
            return null;
        }
        catch (Exception ex)
        {
            return "That folder can't be used: " + ex.Message;
        }
    }

    /// <summary>App is closing: stop answering Explorer, keep the folder and downloads.</summary>
    public void Disconnect()
    {
        _cts.Cancel();
        StopQuickChecks();
        StopWatcher();
        _provider?.Disconnect();
        _provider = null;
    }

    // ------------------------------------------------------------------ refresh

    public async Task RefreshAsync(bool force)
    {
        if (_sync == null || _client == null) return;
        if (!await _gate.WaitAsync(0)) return; // something else is running; the next timer tick will catch up
        ChangeSet? changes;
        lock (_pendingLock)
        {
            changes = _pending;
            _pending = null;
        }
        try
        {
            ServerReachable = await _client.PingAsync(_cts.Token);
            if (!ServerReachable)
            {
                if (State == MountState.Mounted) SetState(MountState.Mounted, "Mounted (server not reachable)");
                return;
            }
            if (State == MountState.Mounted) SetState(MountState.Mounted, "Refreshing");
            if (changes == null) SyncRootRegistrar.EnsureThumbnailProvider(); // regular refreshes only
            _provider?.ClearListingCache();
            await _sync.RefreshAsync(force || changes?.Everything == true, changes, _cts.Token);
            changes = null;
            LastRefresh = DateTime.Now;
            await _sync.MaintainFilesAsync(LimitBytes, FreeUnusedAfter, _cts.Token);
            if (State == MountState.Mounted) SetState(MountState.Mounted, "Connected");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Refresh failed", ex);
            if (State == MountState.Mounted) SetState(MountState.Mounted, "Last refresh failed: " + ex.Message);
        }
        finally
        {
            if (changes != null) Queue(changes); // not done: try those folders again next time
            _gate.Release();
        }
    }

    private long? LimitBytes => Settings.CacheLimitEnabled ? Math.Max(1, Settings.CacheLimitGb) * 1024L * 1024 * 1024 : null;

    private TimeSpan? FreeUnusedAfter => Settings.FreeUnusedEnabled ? TimeSpan.FromDays(Math.Max(1, Settings.FreeUnusedDays)) : null;

    private void Queue(ChangeSet changes)
    {
        lock (_pendingLock) _pending = changes.Merge(_pending);
    }

    // ------------------------------------------------------------------ quick change checks

    private void StartQuickChecks()
    {
        StopQuickChecks();
        _quickCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _ = QuickCheckLoopAsync(_quickCts.Token);
    }

    private void StopQuickChecks()
    {
        _quickCts?.Cancel();
        _quickCts?.Dispose();
        _quickCts = null;
    }

    private async Task QuickCheckLoopAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(QuickCheckInterval, ct); }
            catch (OperationCanceledException) { return; }

            var client = _client;
            if (!Settings.QuickChecks || client == null || _sync == null || State != MountState.Mounted) continue;
            try
            {
                var changes = await _tracker.CheckAsync(client, Settings.Views.Albums, ct);
                if (failures > 0) Log.Info("Quick change checks are working again");
                failures = 0;
                if (changes != null)
                {
                    Log.Info(changes.Everything
                        ? "Immich: many changes, checking every open folder"
                        : changes.AssetCount == 0 ? "Immich: albums changed"
                        : $"Immich: {changes.AssetCount} photos changed{(changes.AlbumListChanged ? ", albums changed" : "")}");
                    Queue(changes);
                }
                bool pending;
                lock (_pendingLock) pending = _pending != null;
                if (pending) await RefreshAsync(force: false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Server down or busy: say so once, keep trying quietly. The regular refresh still runs.
                if (failures++ == 0) Log.Warn("Quick change check failed", ex);
            }
        }
    }

    // ------------------------------------------------------------------ pin / free-up watcher

    private void StartWatcher(string path)
    {
        StopWatcher();
        try
        {
            _watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.Attributes,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Changed += (_, _) => ScheduleMaintain();
            _watcher.Error += (_, e) => { Log.Warn("Folder watcher overflow", e.GetException()); ScheduleMaintain(); };
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not watch the folder for pin changes", ex);
        }
    }

    private void StopWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    private void ScheduleMaintain()
    {
        _maintainDebounce ??= new System.Threading.Timer(_ => _ = MaintainNowAsync());
        _maintainDebounce.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }

    private async Task MaintainNowAsync()
    {
        var sync = _sync;
        if (sync == null) return;
        try { await sync.MaintainFilesAsync(LimitBytes, FreeUnusedAfter, _cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn("File maintenance failed", ex); }
    }

    // ------------------------------------------------------------------ settings

    /// <summary>Saves new settings and re-mounts / refreshes as needed.</summary>
    public async Task ApplySettingsAsync(AppSettings updated)
    {
        var old = Settings;
        var connectionChanged = AppSettings.NormalizeUrl(old.ServerUrl) != AppSettings.NormalizeUrl(updated.ServerUrl) || old.ApiKey != updated.ApiKey;
        var folderChanged = !string.Equals(Path.GetFullPath(old.MountFolder).TrimEnd('\\'), Path.GetFullPath(updated.MountFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        var contentChanged = old.ContentFingerprint != updated.ContentFingerprint;
        var wasMounted = _provider?.IsConnected == true;

        if (folderChanged && wasMounted) await UnmountAsync();

        Settings = updated;
        SettingsStore.Save(updated);

        if (connectionChanged || _client == null)
        {
            ResetClient();
            if (connectionChanged)
            {
                _state.MeId = "";
                _state.Save();
            }
        }
        else if (contentChanged)
        {
            _tree = new VirtualTree(_client, Settings, _state.MeId);
        }
        if (_tree != null && _provider != null && MountedPath != null)
            _sync = new FolderSync(MountedPath, _tree, _locks, _state);

        if (!Settings.IsConfigured) return;

        if (_provider?.IsConnected == true)
        {
            if (connectionChanged)
            {
                // Different server or account: start the folder over.
                await UnmountAsync();
                await MountAsync();
            }
            else
            {
                await RefreshAsync(force: contentChanged);
            }
        }
        else if (folderChanged && wasMounted || Settings.MountMode == MountMode.AppStart || State == MountState.NotConfigured)
        {
            await MountAsync();
        }
    }

    public void Dispose()
    {
        Disconnect();
        _maintainDebounce?.Dispose();
        _client?.Dispose();
    }
}
