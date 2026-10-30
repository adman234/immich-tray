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

    /// <summary>Roaming settings folder (settings.json).</summary>
    public static string SettingsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProviderName);

    /// <summary>Local data folder (logs, sync state, update downloads).</summary>
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProviderName);

    public static string LogDir => Path.Combine(DataDir, "logs");

    public static string DefaultMountFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Immich");

    public static string UserSid => WindowsIdentity.GetCurrent().User?.Value ?? "S-0";

    /// <summary>Sync root id in the format Windows expects: provider!SID!account.</summary>
    public static string SyncRootId => $"{ProviderName}!{UserSid}!default";

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
