using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImmichTray.Settings;

public enum MountMode
{
    /// <summary>Mount as soon as the app starts.</summary>
    AppStart,
    /// <summary>Wait until the server answers, then mount.</summary>
    WhenReachable,
    /// <summary>Only mount from the tray menu.</summary>
    Manual,
}

public enum FileNaming
{
    /// <summary>Use the file name the photo was uploaded with.</summary>
    Original,
    /// <summary>Name files by the date taken, e.g. 2026-10-30 14.22.05.jpg.</summary>
    DateTaken,
}

/// <summary>Which top-level folders appear in the Immich folder.</summary>
public sealed class ViewSettings
{
    public bool Recent { get; set; } = true;
    public int RecentCount { get; set; } = 100;
    public bool Timeline { get; set; } = true;
    public bool Albums { get; set; } = true;
    public bool IncludeSharedAlbums { get; set; } = true;
    public bool People { get; set; } = true;
    public bool Favorites { get; set; } = true;
    public bool AllPhotos { get; set; } = false;

    public ViewSettings Clone() => (ViewSettings)MemberwiseClone();
}

public sealed class AppSettings
{
    public string ServerUrl { get; set; } = "";

    /// <summary>API key encrypted with Windows DPAPI for the current user, base64.</summary>
    public string? ApiKeyProtected { get; set; }

    public string MountFolder { get; set; } = AppInfo.DefaultMountFolder;
    public MountMode MountMode { get; set; } = MountMode.AppStart;

    /// <summary>For manual mode: whether the user left it mounted last time.</summary>
    public bool ManualMounted { get; set; }

    public int RefreshMinutes { get; set; } = 5;

    public ViewSettings Views { get; set; } = new();

    public bool IncludeVideos { get; set; } = true;
    public bool IncludeArchived { get; set; } = false;
    public bool IncludePartnerPhotos { get; set; } = false;
    public bool ShowLivePhotoVideos { get; set; } = false;
    public FileNaming Naming { get; set; } = FileNaming.Original;

    public bool CacheLimitEnabled { get; set; } = false;
    public int CacheLimitGb { get; set; } = 20;

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrEmpty(ApiKeyProtected);

    [JsonIgnore]
    public string ApiKey
    {
        get => Unprotect(ApiKeyProtected);
        set => ApiKeyProtected = string.IsNullOrEmpty(value) ? null : Protect(value);
    }

    /// <summary>
    /// A fingerprint of everything that changes what files appear and how they are named.
    /// When it changes, every folder is re-checked against the server.
    /// </summary>
    [JsonIgnore]
    public string ContentFingerprint => string.Join("|",
        Views.Recent, Views.RecentCount, Views.Timeline, Views.Albums, Views.IncludeSharedAlbums, Views.People,
        Views.Favorites, Views.AllPhotos, IncludeVideos, IncludeArchived, IncludePartnerPhotos,
        ShowLivePhotoVideos, Naming, NormalizeUrl(ServerUrl));

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.Views = Views.Clone();
        return copy;
    }

    public static string NormalizeUrl(string url)
    {
        url = (url ?? "").Trim().TrimEnd('/');
        if (url.EndsWith("/api", StringComparison.OrdinalIgnoreCase)) url = url[..^4];
        if (url.Length > 0 && !url.Contains("://")) url = "http://" + url;
        return url;
    }

    private static string Protect(string value)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not decrypt the saved API key", ex);
            return "";
        }
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ImmichTray.ApiKey.v1");
}

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FilePath => Path.Combine(AppInfo.SettingsDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options);
                if (loaded != null)
                {
                    loaded.Views ??= new ViewSettings();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read settings, using defaults", ex);
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppInfo.SettingsDir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public static string Serialize(AppSettings s) => JsonSerializer.Serialize(s, Options);
    public static AppSettings? Deserialize(string json) => JsonSerializer.Deserialize<AppSettings>(json, Options);
}
