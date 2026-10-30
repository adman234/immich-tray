using System.Diagnostics;
using ImmichTray.Immich;
using ImmichTray.Settings;
using ImmichTray.Updates;

namespace ImmichTray.UI;

/// <summary>What the settings window needs from the app.</summary>
internal interface ISettingsHost
{
    AppSettings CurrentSettings { get; }
    Updater Updater { get; }
    Task ApplySettingsAsync(AppSettings updated);
    Task UpdateNowAsync(IWin32Window? owner);
}

/// <summary>The settings window. Built in code (no designer) so it scales cleanly with DPI.</summary>
internal sealed class SettingsForm : Form
{
    public const string RequiredPermissions =
        "asset.read, asset.view, asset.download, asset.statistics, album.read, person.read, user.read, server.about";

    private readonly AppSettings _settings;
    private readonly ISettingsHost _app;

    // Connection
    private readonly TextBox _server = new() { Width = 360 };
    private readonly TextBox _apiKey = new() { Width = 360, UseSystemPasswordChar = true };
    private readonly Label _testResult = new() { AutoSize = true, MaximumSize = new Size(520, 0) };

    // Folder
    private readonly TextBox _folder = new() { Width = 360 };
    private readonly RadioButton _mountStart = new() { Text = "When the app starts", AutoSize = true };
    private readonly RadioButton _mountReachable = new() { Text = "When the Immich server can be reached", AutoSize = true };
    private readonly RadioButton _mountManual = new() { Text = "Only when I choose Mount in the tray menu", AutoSize = true };
    private readonly NumericUpDown _refresh = new() { Minimum = 1, Maximum = 1440, Width = 70 };
    private readonly CheckBox _quick = new() { Text = "Show new and changed photos within about 15 seconds", AutoSize = true };
    private readonly CheckBox _freeUnused = new() { Text = "Free up photos I haven't opened in", AutoSize = true };
    private readonly NumericUpDown _freeDays = new() { Minimum = 1, Maximum = 3650, Width = 70 };
    private readonly CheckBox _limit = new() { Text = "Limit downloaded files to", AutoSize = true };
    private readonly NumericUpDown _limitGb = new() { Minimum = 1, Maximum = 100000, Width = 80 };

    // Layout
    private readonly CheckBox _recent = new() { Text = "Recent: the newest", AutoSize = true };
    private readonly NumericUpDown _recentCount = new() { Minimum = 1, Maximum = 10000, Width = 80 };
    private readonly CheckBox _timeline = new() { Text = "Timeline: one folder per year, then per month", AutoSize = true };
    private readonly CheckBox _albums = new() { Text = "Albums: one folder per album", AutoSize = true };
    private readonly CheckBox _sharedAlbums = new() { Text = "Include albums other people shared with me", AutoSize = true, Margin = new Padding(24, 3, 3, 3) };
    private readonly CheckBox _people = new() { Text = "People: one folder per named person", AutoSize = true };
    private readonly CheckBox _favorites = new() { Text = "Favorites", AutoSize = true };
    private readonly CheckBox _all = new() { Text = "All Photos: everything in one big folder", AutoSize = true };

    // Content
    private readonly CheckBox _videos = new() { Text = "Include videos", AutoSize = true };
    private readonly CheckBox _archived = new() { Text = "Include archived photos", AutoSize = true };
    private readonly CheckBox _partners = new() { Text = "Include photos from partners who share with me", AutoSize = true };
    private readonly CheckBox _live = new() { Text = "Show the video part of Live Photos as separate files", AutoSize = true };
    private readonly RadioButton _nameOriginal = new() { Text = "Original file name (IMG_1234.HEIC)", AutoSize = true };
    private readonly RadioButton _nameDate = new() { Text = "Date taken (2026-10-30 14.22.05.heic)", AutoSize = true };

    // General
    private readonly CheckBox _autostart = new() { Text = "Start Immich Tray when Windows starts", AutoSize = true };
    private readonly Label _updateStatus = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly Button _updateNow = new() { Text = "Update now", AutoSize = true, Enabled = false };

    public SettingsForm(ISettingsHost app, AppSettings current, int initialTab = 0)
    {
        _app = app;
        _settings = current.Clone();

        Text = "Immich Tray settings";
        Icon = TrayApp.LoadIcon(false);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);
        ShowInTaskbar = true;

        var tabs = new TabControl { Width = 600, Height = 580 };
        tabs.TabPages.Add(Page("Connection", BuildConnection()));
        tabs.TabPages.Add(Page("Folder", BuildFolder()));
        tabs.TabPages.Add(Page("Layout", BuildLayout()));
        tabs.TabPages.Add(Page("Content", BuildContent()));
        tabs.TabPages.Add(Page("General", BuildGeneral()));
        tabs.SelectedIndex = Math.Clamp(initialTab, 0, tabs.TabCount - 1);
        Tabs = tabs;

        var save = new Button { Text = "Save", AutoSize = true, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.Click += async (_, _) => await SaveAsync(save);
        AcceptButton = save;
        CancelButton = cancel;

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        root.Controls.Add(tabs);
        root.Controls.Add(buttons);
        Controls.Add(root);

        LoadValues();

        // The startup switch applies right away (no Save needed) and stays in step with the tray menu.
        _autostart.Click += (_, _) => ApplyAutostart();
        AutoStart.Changed += ShowAutostart;
        FormClosed += (_, _) => AutoStart.Changed -= ShowAutostart;
        Activated += (_, _) => ShowAutostart();
    }

    internal TabControl Tabs { get; }

    private void ApplyAutostart()
    {
        try { AutoStart.Set(_autostart.Checked); }
        catch (Exception ex)
        {
            Log.Warn("Could not change the startup setting", ex);
            MessageBox.Show(this, "Could not change the startup setting: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        ShowAutostart();
    }

    private void ShowAutostart()
    {
        try { _autostart.Checked = AutoStart.IsEnabled; } catch { }
    }

    private static TabPage Page(string title, Control content)
    {
        var page = new TabPage(title) { Padding = new Padding(10), AutoScroll = true };
        content.Dock = DockStyle.Fill;
        page.Controls.Add(content);
        return page;
    }

    private static FlowLayoutPanel Column() => new()
    {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoSize = true,
        AutoScroll = false,
    };

    private static Label Heading(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
        Margin = new Padding(3, 10, 3, 3),
    };

    private static Label Note(string text) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(540, 0), ForeColor = SystemColors.GrayText,
        Margin = new Padding(3, 0, 3, 6),
    };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        foreach (var c in controls)
        {
            if (c is Label l) { l.AutoSize = true; l.Anchor = AnchorStyles.Left; l.Padding = new Padding(0, 6, 0, 0); }
            row.Controls.Add(c);
        }
        return row;
    }

    // ------------------------------------------------------------------ pages

    private Control BuildConnection()
    {
        var col = Column();
        col.Controls.Add(Heading("Immich server"));
        col.Controls.Add(Note("The address you open Immich at, for example http://192.168.1.10:2283"));
        col.Controls.Add(_server);

        col.Controls.Add(Heading("API key"));
        var show = new CheckBox { Text = "Show", AutoSize = true };
        show.CheckedChanged += (_, _) => _apiKey.UseSystemPasswordChar = !show.Checked;
        col.Controls.Add(Row(_apiKey, show));
        var open = new LinkLabel { Text = "Create an API key in Immich", AutoSize = true };
        open.LinkClicked += (_, _) => OpenApiKeyPage();
        col.Controls.Add(open);
        col.Controls.Add(Note(
            "In Immich: Account Settings > API Keys > New API Key. Give it a name like \"Windows\" and tick these permissions " +
            "(or choose All):\n\n" + RequiredPermissions.Replace(", ", "\n") +
            "\n\nThe key is stored encrypted for your Windows account only."));

        var test = new Button { Text = "Test connection", AutoSize = true };
        test.Click += async (_, _) => await TestAsync(test);
        col.Controls.Add(test);
        col.Controls.Add(_testResult);
        return col;
    }

    private Control BuildFolder()
    {
        var col = Column();
        col.Controls.Add(Heading("Folder location"));
        var browse = new Button { Text = "Browse...", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Pick an empty folder for your Immich library", UseDescriptionForTitle = true, ShowNewFolderButton = true };
            if (dlg.ShowDialog(this) == DialogResult.OK) _folder.Text = dlg.SelectedPath;
        };
        col.Controls.Add(Row(_folder, browse));
        col.Controls.Add(Note("Must be an empty or new folder on a local NTFS drive. It also appears in the Explorer sidebar as \"Immich\"."));

        col.Controls.Add(Heading("Mount the folder"));
        col.Controls.Add(_mountStart);
        col.Controls.Add(_mountReachable);
        col.Controls.Add(_mountManual);

        col.Controls.Add(Heading("Check for new photos"));
        col.Controls.Add(_quick);
        col.Controls.Add(Row(new Label { Text = "Also re-check every open folder every" }, _refresh, new Label { Text = "minutes" }));
        col.Controls.Add(Note("Only folders you have opened are checked. Albums other people add to show up on the regular re-check. \"Refresh now\" in the tray menu checks right away."));

        col.Controls.Add(Heading("Disk space"));
        col.Controls.Add(Row(_freeUnused, _freeDays, new Label { Text = "days" }));
        col.Controls.Add(Row(_limit, _limitGb, new Label { Text = "GB" }));
        col.Controls.Add(Note("Opening a photo saves a copy on this PC. Freed photos stay in the folder and download again when opened. Files marked \"Always keep on this device\" are never freed."));
        return col;
    }

    private Control BuildLayout()
    {
        var col = Column();
        col.Controls.Add(Heading("Folders to show"));
        col.Controls.Add(Note("Each one is a top-level folder inside Immich. A photo can show up in several of them."));
        col.Controls.Add(Row(_recent, _recentCount, new Label { Text = "photos" }));
        col.Controls.Add(_timeline);
        col.Controls.Add(_albums);
        col.Controls.Add(_sharedAlbums);
        _albums.CheckedChanged += (_, _) => _sharedAlbums.Enabled = _albums.Checked;
        col.Controls.Add(_people);
        col.Controls.Add(_favorites);
        col.Controls.Add(_all);
        col.Controls.Add(Note("All Photos lists your whole library in one folder. With tens of thousands of photos, Explorer is slow to open it, and the first open can take a minute or two."));
        return col;
    }

    private Control BuildContent()
    {
        var col = Column();
        col.Controls.Add(Heading("What to include"));
        col.Controls.Add(_videos);
        col.Controls.Add(_archived);
        col.Controls.Add(_partners);
        col.Controls.Add(_live);
        col.Controls.Add(Note("Albums always show everything in the album, including archived photos and photos other people added."));

        col.Controls.Add(Heading("File names"));
        col.Controls.Add(_nameOriginal);
        col.Controls.Add(_nameDate);
        col.Controls.Add(Note("If two files in a folder would have the same name, the later one gets \" (2)\" added."));
        return col;
    }

    private Control BuildGeneral()
    {
        var col = Column();
        col.Controls.Add(Heading("Startup"));
        col.Controls.Add(_autostart);
        col.Controls.Add(Note("Same switch as Start with Windows in the tray menu. Changes take effect right away."));

        col.Controls.Add(Heading("Updates"));
        col.Controls.Add(Note($"You have version {AppInfo.Version}. Updates come from GitHub releases and only install when you ask."));
        var check = new Button { Text = "Check for updates", AutoSize = true };
        check.Click += async (_, _) => await CheckUpdatesAsync(check);
        _updateNow.Click += async (_, _) => await _app.UpdateNowAsync(this);
        col.Controls.Add(Row(check, _updateNow));
        col.Controls.Add(_updateStatus);
        if (_app.Updater.LastFound is { } found)
        {
            _updateNow.Enabled = true;
            _updateStatus.Text = $"Version {found.Version} is available.";
        }

        col.Controls.Add(Heading("Help"));
        var gh = new LinkLabel { Text = "Project page on GitHub", AutoSize = true };
        gh.LinkClicked += (_, _) => Open($"https://github.com/{AppInfo.GitHubOwner}/{AppInfo.GitHubRepo}");
        var logs = new LinkLabel { Text = "Open the log folder", AutoSize = true };
        logs.LinkClicked += (_, _) => { Directory.CreateDirectory(AppInfo.LogDir); Open(AppInfo.LogDir); };
        col.Controls.Add(gh);
        col.Controls.Add(logs);
        return col;
    }

    // ------------------------------------------------------------------ values

    private void LoadValues()
    {
        _server.Text = _settings.ServerUrl;
        _apiKey.Text = _settings.ApiKey;
        _folder.Text = _settings.MountFolder;
        _mountStart.Checked = _settings.MountMode == MountMode.AppStart;
        _mountReachable.Checked = _settings.MountMode == MountMode.WhenReachable;
        _mountManual.Checked = _settings.MountMode == MountMode.Manual;
        _refresh.Value = Math.Clamp(_settings.RefreshMinutes, 1, 1440);
        _quick.Checked = _settings.QuickChecks;
        _freeUnused.Checked = _settings.FreeUnusedEnabled;
        _freeDays.Value = Math.Clamp(_settings.FreeUnusedDays, 1, 3650);
        _limit.Checked = _settings.CacheLimitEnabled;
        _limitGb.Value = Math.Clamp(_settings.CacheLimitGb, 1, 100000);

        var v = _settings.Views;
        _recent.Checked = v.Recent;
        _recentCount.Value = Math.Clamp(v.RecentCount, 1, 10000);
        _timeline.Checked = v.Timeline;
        _albums.Checked = v.Albums;
        _sharedAlbums.Checked = v.IncludeSharedAlbums;
        _sharedAlbums.Enabled = v.Albums;
        _people.Checked = v.People;
        _favorites.Checked = v.Favorites;
        _all.Checked = v.AllPhotos;

        _videos.Checked = _settings.IncludeVideos;
        _archived.Checked = _settings.IncludeArchived;
        _partners.Checked = _settings.IncludePartnerPhotos;
        _live.Checked = _settings.ShowLivePhotoVideos;
        _nameOriginal.Checked = _settings.Naming == FileNaming.Original;
        _nameDate.Checked = _settings.Naming == FileNaming.DateTaken;

        ShowAutostart();
    }

    private AppSettings ReadValues()
    {
        var s = _settings.Clone();
        s.ServerUrl = AppSettings.NormalizeUrl(_server.Text);
        s.ApiKey = _apiKey.Text.Trim();
        s.MountFolder = string.IsNullOrWhiteSpace(_folder.Text) ? AppInfo.DefaultMountFolder : _folder.Text.Trim();
        s.MountMode = _mountReachable.Checked ? MountMode.WhenReachable : _mountManual.Checked ? MountMode.Manual : MountMode.AppStart;
        s.RefreshMinutes = (int)_refresh.Value;
        s.QuickChecks = _quick.Checked;
        s.FreeUnusedEnabled = _freeUnused.Checked;
        s.FreeUnusedDays = (int)_freeDays.Value;
        s.CacheLimitEnabled = _limit.Checked;
        s.CacheLimitGb = (int)_limitGb.Value;
        s.Views = new ViewSettings
        {
            Recent = _recent.Checked,
            RecentCount = (int)_recentCount.Value,
            Timeline = _timeline.Checked,
            Albums = _albums.Checked,
            IncludeSharedAlbums = _sharedAlbums.Checked,
            People = _people.Checked,
            Favorites = _favorites.Checked,
            AllPhotos = _all.Checked,
        };
        s.IncludeVideos = _videos.Checked;
        s.IncludeArchived = _archived.Checked;
        s.IncludePartnerPhotos = _partners.Checked;
        s.ShowLivePhotoVideos = _live.Checked;
        s.Naming = _nameDate.Checked ? FileNaming.DateTaken : FileNaming.Original;
        return s;
    }

    private async Task SaveAsync(Button save)
    {
        var updated = ReadValues();
        if (string.IsNullOrWhiteSpace(updated.ServerUrl) || string.IsNullOrWhiteSpace(_apiKey.Text))
        {
            MessageBox.Show(this, "Enter your Immich server address and an API key.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var oldFolder = Path.GetFullPath(_app.CurrentSettings.MountFolder).TrimEnd('\\');
        var newFolder = Path.GetFullPath(updated.MountFolder).TrimEnd('\\');
        if (!string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase) || !_app.CurrentSettings.IsConfigured)
        {
            var registered = CloudFiles.SyncRootRegistrar.GetRegisteredPath();
            var isOurs = registered != null && string.Equals(Path.GetFullPath(registered).TrimEnd('\\'), newFolder, StringComparison.OrdinalIgnoreCase);
            var problem = isOurs ? null : MountManager.CheckFolder(newFolder);
            if (problem != null)
            {
                MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        save.Enabled = false;
        UseWaitCursor = true;
        try
        {
            await _app.ApplySettingsAsync(updated);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
            MessageBox.Show(this, "Saving failed: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            save.Enabled = true;
            UseWaitCursor = false;
        }
    }

    // ------------------------------------------------------------------ actions

    private void OpenApiKeyPage()
    {
        var url = AppSettings.NormalizeUrl(_server.Text);
        if (url.Length == 0)
        {
            MessageBox.Show(this, "Enter the server address first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Open(url + "/user-settings?isOpen=api-keys");
    }

    private async Task TestAsync(Button test)
    {
        test.Enabled = false;
        _testResult.ForeColor = SystemColors.ControlText;
        _testResult.Text = "Testing...";
        try
        {
            _testResult.Text = await ConnectionTester.RunAsync(_server.Text, _apiKey.Text.Trim());
        }
        finally
        {
            test.Enabled = true;
        }
    }

    private async Task CheckUpdatesAsync(Button check)
    {
        check.Enabled = false;
        _updateStatus.Text = "Checking...";
        try
        {
            var found = await _app.Updater.CheckAsync();
            _updateNow.Enabled = found != null;
            _updateStatus.Text = found != null
                ? $"Version {found.Version} is available. Click Update now to install it."
                : $"You have the latest version ({AppInfo.Version}).";
        }
        catch (Exception ex)
        {
            _updateStatus.Text = "Could not check for updates: " + ex.Message;
        }
        finally
        {
            check.Enabled = true;
        }
    }

    internal static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Could not open {target}", ex); }
    }
}

/// <summary>Checks the server address, the key, and each permission the app uses.</summary>
internal static class ConnectionTester
{
    public static async Task<string> RunAsync(string server, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(server)) return "Enter the server address.";
        if (string.IsNullOrWhiteSpace(apiKey)) return "Enter an API key.";

        using var client = new ImmichClient(server, apiKey, timeout: TimeSpan.FromSeconds(20));
        if (!await client.PingAsync())
            return $"Could not reach Immich at {AppSettings.NormalizeUrl(server)}. Check the address and that this PC can reach it.";

        var lines = new List<string>();
        var missing = new List<string>();

        async Task Check(string permission, Func<Task> call)
        {
            try { await call(); }
            catch (ImmichApiException ex) when (ex.Status == 403) { missing.Add(permission); }
        }

        try
        {
            var me = await client.GetMeAsync();
            lines.Add($"Signed in as {me.Name} ({me.Email}).");
        }
        catch (ImmichApiException ex) when (ex.Status == 401)
        {
            return "The server is reachable, but it rejected the API key.";
        }
        catch (ImmichApiException ex) when (ex.Status == 403)
        {
            missing.Add("user.read");
        }

        await Check("server.about", async () =>
        {
            var about = await client.GetAboutAsync();
            lines.Add($"Immich version {about.Version}.");
            var v = Updater.ParseVersion(about.Version);
            if (v != null && v < new Version(3, 2, 0))
                lines.Add("This app needs Immich 3.2 or newer. Please update your server.");
        });
        Asset? sample = null;
        await Check("asset.read", async () =>
        {
            await foreach (var page in client.SearchAsync(new AssetQuery { Limit = 1 }))
            {
                sample = page.FirstOrDefault();
                break;
            }
        });
        if (sample != null)
        {
            await Check("asset.view", async () => await client.GetThumbnailAsync(sample.Id, false));
            await Check("asset.download", async () => await client.GetOriginalSizeAsync(sample.Id));
        }
        await Check("asset.statistics", async () => await client.GetTotalAssetCountAsync(false));
        await Check("album.read", async () => await client.GetAlbumsAsync());
        await Check("person.read", async () => await client.GetPeopleAsync());

        if (missing.Count > 0)
            lines.Add("The key is missing these permissions: " + string.Join(", ", missing) +
                ". Edit the key in Immich and tick them.");
        else
            lines.Add("Everything checks out.");
        return string.Join(Environment.NewLine, lines);
    }
}
