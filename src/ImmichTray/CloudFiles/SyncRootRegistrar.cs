using Microsoft.Win32;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;

namespace ImmichTray.CloudFiles;

/// <summary>
/// Registers the Immich folder as a cloud sync root (this is what adds it to Explorer's
/// sidebar with cloud status icons), and hooks up the thumbnail provider.
/// </summary>
internal static class SyncRootRegistrar
{
    private const string SyncRootManagerKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager";

    /// <summary>Returns the folder our sync root is registered at, or null.</summary>
    public static string? GetRegisteredPath()
    {
        try
        {
            foreach (var root in StorageProviderSyncRootManager.GetCurrentSyncRoots())
            {
                if (string.Equals(root.Id, AppInfo.SyncRootId, StringComparison.OrdinalIgnoreCase))
                    return root.Path?.Path;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not list sync roots", ex);
        }
        return null;
    }

    public static async Task RegisterAsync(string path)
    {
        Directory.CreateDirectory(path);
        var folder = await StorageFolder.GetFolderFromPathAsync(path);
        var info = new StorageProviderSyncRootInfo
        {
            Id = AppInfo.SyncRootId,
            Path = folder,
            DisplayNameResource = "Immich",
            IconResource = AppInfo.ExePath + ",0",
            HydrationPolicy = StorageProviderHydrationPolicy.Progressive,
            HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.AutoDehydrationAllowed,
            PopulationPolicy = StorageProviderPopulationPolicy.Full,
            InSyncPolicy = StorageProviderInSyncPolicy.Default,
            HardlinkPolicy = StorageProviderHardlinkPolicy.None,
            ShowSiblingsAsGroup = false,
            Version = "1.0",
            AllowPinning = true,
            ProtectionMode = StorageProviderProtectionMode.Unknown,
            Context = CryptographicBuffer.ConvertStringToBinary(AppInfo.SyncRootId, BinaryStringEncoding.Utf8),
        };
        StorageProviderSyncRootManager.Register(info);
        Log.Info($"Registered sync root {AppInfo.SyncRootId} at {path}");
        RegisterThumbnailProvider();
    }

    public static void Unregister()
    {
        try
        {
            StorageProviderSyncRootManager.Unregister(AppInfo.SyncRootId);
            Log.Info("Unregistered sync root");
        }
        catch (Exception ex)
        {
            Log.Warn("Unregistering the sync root failed", ex);
        }
    }

    /// <summary>
    /// Points Explorer at our thumbnail provider for files that are not downloaded yet.
    /// The class itself is served out-of-process by this exe (see ThumbnailServer).
    /// </summary>
    public static void RegisterThumbnailProvider()
    {
        var clsid = AppInfo.ThumbnailProviderClsid.ToString("B");
        try
        {
            using (var cls = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{clsid}"))
            {
                cls.SetValue("", "Immich Tray thumbnail provider");
                using var server = cls.CreateSubKey("LocalServer32");
                server.SetValue("", $"\"{AppInfo.ExePath}\"");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not register the thumbnail COM class", ex);
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{SyncRootManagerKey}\{AppInfo.SyncRootId}", writable: true);
            if (key == null)
            {
                Log.Warn("Sync root registry key not found; thumbnails for online-only files will be generic icons");
                return;
            }
            key.SetValue("ThumbnailProvider", clsid, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not register the thumbnail provider for the sync root", ex);
        }
    }

    public static void UnregisterThumbnailClass()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\CLSID\{AppInfo.ThumbnailProviderClsid:B}", throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not remove the thumbnail COM class", ex);
        }
    }
}
