using System.Reflection;
using System.Security.Principal;

namespace ImmichTray;

/// <summary>Constants and well-known paths.</summary>
internal static class AppInfo
{
    public const string ProductName = "Immich Tray";
    public const string ProviderName = "ImmichTray";
    public const string GitHubOwner = "adman234";
    public const string GitHubRepo = "immich-tray";

    /// <summary>CLSID of the out-of-process thumbnail provider registered for the sync root.</summary>
    public static readonly Guid ThumbnailProviderClsid = new("6f3f4a5e-2b7c-4c1e-9a43-1d6c0e8b7a21");

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ImmichTray.exe");

    /// <summary>
    /// For development only: IMMICHTRAY_PROFILE=name runs a separate copy with its own settings,
    /// data, Explorer folder and single-instance lock, so it can be tested next to an installed copy.
    /// It never touches the installed copy's startup entry or thumbnail provider.
    /// </summary>
    public static string? Profile { get; } = ReadProfile();

    public static bool IsTestProfile => Profile != null;

    /// <summary>"" normally, "-name" for a test profile. Added to folder and lock names.</summary>
    public static string ProfileSuffix => Profile == null ? "" : "-" + Profile;

    private static string? ReadProfile()
    {
        var raw = Environment.GetEnvironmentVariable("IMMICHTRAY_PROFILE");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var clean = new string(raw.Trim().Where(char.IsAsciiLetterOrDigit).ToArray());
        return clean.Length == 0 || clean.Equals("default", StringComparison.OrdinalIgnoreCase) ? null : clean;
    }

    /// <summary>Roaming settings folder (settings.json).</summary>
    public static string SettingsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProviderName + ProfileSuffix);

    /// <summary>Local data folder (logs, sync state, update downloads).</summary>
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProviderName + ProfileSuffix);

    public static string LogDir => Path.Combine(DataDir, "logs");

    public static string DefaultMountFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Immich");

    public static string UserSid => WindowsIdentity.GetCurrent().User?.Value ?? "S-0";

    /// <summary>Sync root id in the format Windows expects: provider!SID!account.</summary>
    public static string SyncRootId => $"{ProviderName}!{UserSid}!{Profile ?? "default"}";

    public static string Version
    {
        get
        {
            var info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
    }
}
