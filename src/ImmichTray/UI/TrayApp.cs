using ImmichTray.Settings;
using ImmichTray.Thumbnails;
using ImmichTray.Updates;

namespace ImmichTray.UI;

/// <summary>The tray icon, its right-click menu, and the app's lifetime.</summary>
internal sealed class TrayApp : ApplicationContext, ISettingsHost
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _status = new() { Enabled = false };
    private readonly ToolStripMenuItem _openFolder = new("Open Immich folder");
    private readonly ToolStripMenuItem _openWeb = new("Open Immich in browser");
    private readonly ToolStripMenuItem _refresh = new("Refresh now");
    private readonly ToolStripMenuItem _mount = new("Mount");
    private readonly ToolStripMenuItem _autostart = new("Start with Windows") { CheckOnClick = false };
    private readonly ToolStripMenuItem _checkUpdates = new("Check for updates");
    private readonly ToolStripMenuItem _updateNow = new("Update now");
    private readonly System.Windows.Forms.Timer _refreshTimer = new();
    private readonly SynchronizationContext _ui;
    private readonly ThumbnailServer _thumbnails;
    private readonly Icon _onlineIcon = LoadIcon(false);
    private readonly Icon _offlineIcon = LoadIcon(true);
    private SettingsForm? _settingsForm;
    private bool _busy;

    public MountManager Manager { get; }
    public Updater Updater { get; } = new();
    public AppSettings CurrentSettings => Manager.Settings;

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Manager = new MountManager(SettingsStore.Load());
        Manager.Changed += () => _ui.Post(_ => UpdateStatus(), null);
        _thumbnails = new ThumbnailServer(() => Manager.Client, enabled: !AppInfo.IsTestProfile);

        _menu.Items.Add(_status);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_openFolder);
        _menu.Items.Add(_openWeb);
        _menu.Items.Add(_refresh);
        _menu.Items.Add(_mount);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Settings...", null, (_, _) => ShowSettings());
        _menu.Items.Add(_autostart);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_checkUpdates);
        _menu.Items.Add(_updateNow);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Quit", null, (_, _) => Quit());

        _openFolder.Font = new Font(_openFolder.Font, FontStyle.Bold);
        _openFolder.Click += (_, _) => OpenFolder();
        _openWeb.Click += (_, _) => SettingsForm.Open(AppSettings.NormalizeUrl(Manager.Settings.ServerUrl));
        _refresh.Click += async (_, _) => await Manager.RefreshAsync(force: true);
        _mount.Click += async (_, _) => await ToggleMountAsync();
        _autostart.Click += (_, _) => ToggleAutostart();
        _checkUpdates.Click += async (_, _) => await CheckForUpdatesAsync();
        _updateNow.Click += async (_, _) => await UpdateNowAsync(null);
        _menu.Opening += (_, _) => UpdateStatus();

        _icon = new NotifyIcon
        {
            Icon = _offlineIcon,
            Text = AppInfo.ProductName,
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenFolder(); };

        _refreshTimer.Tick += async (_, _) => await Manager.RefreshAsync(force: false);
        ResetTimer();
        UpdateStatus();

        if (AppInfo.IsTestProfile)
        {
            _autostart.Enabled = false;
            _icon.Text = $"{AppInfo.ProductName} (test profile {AppInfo.Profile})";
        }
        else
        {
            AutoStart.RefreshPathIfEnabled();
        }
        _ui.Post(async _ =>
        {
            if (!Manager.Settings.IsConfigured) ShowSettings();
            await Manager.StartAsync();
        }, null);
    }

    public static Icon LoadIcon(bool offline)
    {
        var name = offline ? "immich-tray-offline.ico" : "immich-tray.ico";
        using var s = typeof(TrayApp).Assembly.GetManifestResourceStream(name);
        return s != null ? new Icon(s, SystemInformation.SmallIconSize) : SystemIcons.Application;
    }

    private void ResetTimer()
    {
        _refreshTimer.Stop();
        _refreshTimer.Interval = (int)TimeSpan.FromMinutes(Math.Clamp(Manager.Settings.RefreshMinutes, 1, 1440)).TotalMilliseconds;
        _refreshTimer.Start();
    }

    private void UpdateStatus()
    {
        var m = Manager;
        var mounted = m.State == MountState.Mounted;
        var text = m.StatusText;
        if (mounted && m.ActiveDownloads > 0) text += $", downloading {m.ActiveDownloads}";
        _status.Text = text;
        _icon.Icon = mounted && m.ServerReachable ? _onlineIcon : _offlineIcon;
        var tip = $"{AppInfo.ProductName}: {text}";
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;

        _openFolder.Enabled = mounted;
        _openWeb.Enabled = m.Settings.IsConfigured;
        _refresh.Enabled = mounted && !_busy;
        _mount.Text = mounted ? "Unmount" : "Mount";
        _mount.Enabled = m.Settings.IsConfigured && !_busy && m.State != MountState.Mounting;
        try { _autostart.Checked = AutoStart.IsEnabled; } catch { }
        _updateNow.Text = Updater.LastFound is { } f ? $"Update now (v{f.Version})" : "Update now";
    }

    // ------------------------------------------------------------------ actions

    private void OpenFolder()
    {
        if (Manager.MountedPath is { } path && Directory.Exists(path))
            SettingsForm.Open(path);
        else if (!Manager.Settings.IsConfigured)
            ShowSettings();
    }

    public void ShowSettings(int tab = 0)
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.WindowState = FormWindowState.Normal;
            _settingsForm.Activate();
            return;
        }
        _settingsForm = new SettingsForm(this, Manager.Settings, tab);
        _settingsForm.FormClosed += (_, _) => _settingsForm = null;
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    public async Task ApplySettingsAsync(AppSettings updated)
    {
        _busy = true;
        UpdateStatus();
        try
        {
            await Manager.ApplySettingsAsync(updated);
            ResetTimer();
        }
        finally
        {
            _busy = false;
            UpdateStatus();
        }
    }

    private async Task ToggleMountAsync()
    {
        _busy = true;
        UpdateStatus();
        try
        {
            if (Manager.State == MountState.Mounted)
            {
                var answer = MessageBox.Show(
                    "Unmount removes the Immich folder from Explorer and deletes any photos downloaded to this PC (they stay safe in Immich). Continue?",
                    AppInfo.ProductName, MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.OK) await Manager.UnmountAsync();
            }
            else
            {
                if (!await Manager.MountAsync())
                    MessageBox.Show(Manager.StatusText, AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            _busy = false;
            UpdateStatus();
        }
    }

    private void ToggleAutostart()
    {
        try
        {
            AutoStart.Set(!AutoStart.IsEnabled);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not change the startup setting: " + ex.Message, AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        UpdateStatus();
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var found = await Updater.CheckAsync();
            UpdateStatus();
            if (found == null)
            {
                MessageBox.Show($"You have the latest version ({AppInfo.Version}).", AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var answer = MessageBox.Show($"Version {found.Version} is available (you have {AppInfo.Version}). Install it now?",
                AppInfo.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer == DialogResult.Yes) await UpdateNowAsync(null);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not check for updates: " + ex.Message, AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public async Task UpdateNowAsync(IWin32Window? owner)
    {
        try
        {
            var release = Updater.LastFound ?? await Updater.CheckAsync();
            if (release == null)
            {
                MessageBox.Show(owner, $"You have the latest version ({AppInfo.Version}).", AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _updateNow.Enabled = false;
            _status.Text = $"Downloading version {release.Version}...";
            _icon.Text = $"{AppInfo.ProductName}: downloading update";
            await Updater.DownloadAndStartAsync(release, null);
            // The installer closes and restarts the app; leave now so files aren't locked.
            Quit();
        }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            MessageBox.Show(owner, "The update did not work: " + ex.Message, AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _updateNow.Enabled = true;
            UpdateStatus();
        }
    }

    /// <summary>Called from another instance (e.g. the user started the app again).</summary>
    public void OnSecondInstance() => _ui.Post(_ =>
    {
        if (Manager.State == MountState.Mounted) OpenFolder();
        else ShowSettings();
    }, null);

    public void Quit()
    {
        _refreshTimer.Stop();
        _icon.Visible = false;
        try { Manager.Dispose(); } catch (Exception ex) { Log.Warn("Shutdown problem", ex); }
        _thumbnails.Dispose();
        _settingsForm?.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Dispose();
            _menu.Dispose();
            _refreshTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
