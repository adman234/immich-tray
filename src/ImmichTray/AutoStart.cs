using Microsoft.Win32;

namespace ImmichTray;

/// <summary>"Start with Windows" via the per-user Run key (no admin needed).</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Where Task Manager and Settings > Apps > Startup record their on/off switch for Run entries.
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "ImmichTray";

    private static string Command => $"\"{AppInfo.ExePath}\" --autostart";

    /// <summary>Raised after the setting changes, so every place that shows it can update.</summary>
    public static event Action? Changed;

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is not string s || s.Length == 0) return false;
            return !DisabledInWindows();
        }
    }

    /// <summary>True when the entry was switched off in Task Manager or Settings (first byte odd).</summary>
    private static bool DisabledInWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return key?.GetValue(ValueName) is byte[] { Length: > 0 } b && (b[0] & 1) == 1;
    }

    public static void Set(bool enabled)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (enabled) key.SetValue(ValueName, Command);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        // Clear Windows' own switch too, otherwise "on" here could still be "off" in Task Manager.
        using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true))
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        Changed?.Invoke();
    }

    /// <summary>Keeps the stored path current if the app moved (e.g. reinstalled elsewhere).</summary>
    public static void RefreshPathIfEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is string s && s.Length > 0 && s != Command)
                key.SetValue(ValueName, Command);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not update the startup entry", ex);
        }
    }
}
