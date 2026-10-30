using Microsoft.Win32;

namespace ImmichTray;

/// <summary>"Start with Windows" via the per-user Run key (no admin needed).</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ImmichTray";

    private static string Command => $"\"{AppInfo.ExePath}\" --autostart";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Keeps the stored path current if the app moved (e.g. reinstalled elsewhere).</summary>
    public static void RefreshPathIfEnabled()
    {
        try
        {
            if (IsEnabled) Set(true);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not update the startup entry", ex);
        }
    }
}
