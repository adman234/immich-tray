using System.Runtime.InteropServices;
using System.Text;
using ImmichTray.Views;
using static ImmichTray.CloudFiles.Native;

namespace ImmichTray.CloudFiles;

/// <summary>A block of native CF_PLACEHOLDER_CREATE_INFO entries plus the strings they point to.</summary>
internal sealed unsafe class PlaceholderBatch : IDisposable
{
    private readonly List<IntPtr> _allocations = new();
    public CF_PLACEHOLDER_CREATE_INFO* Array { get; }
    public int Count { get; }

    public PlaceholderBatch(IReadOnlyList<TreeEntry> entries, int start, int count)
    {
        Count = count;
        Array = (CF_PLACEHOLDER_CREATE_INFO*)NativeMemory.AllocZeroed((nuint)Math.Max(1, count), (nuint)sizeof(CF_PLACEHOLDER_CREATE_INFO));
        for (var i = 0; i < count; i++)
        {
            var e = entries[start + i];
            var name = Marshal.StringToHGlobalUni(e.Name);
            _allocations.Add(name);
            var idBytes = Encoding.UTF8.GetBytes(e.Identity);
            var id = Marshal.AllocHGlobal(idBytes.Length);
            Marshal.Copy(idBytes, 0, id, idBytes.Length);
            _allocations.Add(id);

            ref var info = ref Array[i];
            info.RelativeFileName = name;
            info.FileIdentity = id;
            info.FileIdentityLength = (uint)idBytes.Length;
            info.FsMetadata = Placeholders.Metadata(e);
            info.Flags = CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC;
            // Files never need populating. Folders are filled in when first opened.
            if (!e.IsDirectory) info.Flags |= CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION;
        }
    }

    public void Dispose()
    {
        foreach (var p in _allocations) Marshal.FreeHGlobal(p);
        _allocations.Clear();
        NativeMemory.Free(Array);
    }
}

/// <summary>Helpers for creating, inspecting and changing placeholder files.</summary>
internal static unsafe class Placeholders
{
    public const string AssetPrefix = "a:";

    public static CF_FS_METADATA Metadata(TreeEntry e)
    {
        var created = ToFileTime(e.Created);
        var modified = ToFileTime(e.Modified);
        return new CF_FS_METADATA
        {
            BasicInfo = new FILE_BASIC_INFO
            {
                CreationTime = created,
                LastWriteTime = modified,
                ChangeTime = modified,
                LastAccessTime = modified,
                FileAttributes = e.IsDirectory ? FILE_ATTRIBUTE_DIRECTORY : 0x80 /* NORMAL */,
            },
            FileSize = e.IsDirectory ? 0 : e.Size,
        };
    }

    /// <summary>Creates placeholders directly (used outside callbacks). Existing names are skipped.</summary>
    public static int Create(string directory, IReadOnlyList<TreeEntry> entries)
    {
        var created = 0;
        const int chunk = 500;
        for (var start = 0; start < entries.Count; start += chunk)
        {
            var count = Math.Min(chunk, entries.Count - start);
            using var batch = new PlaceholderBatch(entries, start, count);
            var hr = CfCreatePlaceholders(directory, batch.Array, (uint)count, 0, out _);
            for (var i = 0; i < count; i++)
            {
                if (batch.Array[i].Result >= 0) created++;
                else if (batch.Array[i].Result != unchecked((int)0x800700B7)) // already exists
                    Log.Warn($"Could not create placeholder {entries[start + i].Name} in {directory}: 0x{batch.Array[i].Result:X8}");
            }
            if (hr < 0 && hr != unchecked((int)0x800700B7))
                Log.Warn($"CfCreatePlaceholders in {directory} returned 0x{hr:X8}");
        }
        return created;
    }

    /// <summary>Reads the identity stored in a placeholder ("a:&lt;asset id&gt;" or "d"), or null.</summary>
    public static string? ReadIdentity(string path)
    {
        using var h = OpenForAttributes(path);
        if (h.IsInvalid) return null;
        const int size = 4096 + 64;
        var buf = stackalloc byte[size];
        var hr = CfGetPlaceholderInfo(h, CF_PLACEHOLDER_INFO_BASIC, buf, size, out _);
        if (hr < 0) return null;
        // CF_PLACEHOLDER_BASIC_INFO: PinState(4) InSyncState(4) FileId(8) SyncRootFileId(8) FileIdentityLength(4) FileIdentity[]
        var len = *(uint*)(buf + 24);
        if (len == 0 || len > 4096) return null;
        return Encoding.UTF8.GetString(buf + 28, (int)len);
    }

    public static string? AssetIdFromIdentity(string? identity) =>
        identity != null && identity.StartsWith(AssetPrefix, StringComparison.Ordinal) ? identity[AssetPrefix.Length..] : null;

    /// <summary>Gets attributes and the reparse tag without touching file contents.</summary>
    public static bool TryGetAttributeTag(string path, out uint attributes, out uint reparseTag)
    {
        attributes = 0; reparseTag = 0;
        using var h = OpenForAttributes(path);
        if (h.IsInvalid) return false;
        FILE_ATTRIBUTE_TAG_INFO info;
        if (!GetFileInformationByHandleEx(h, FileAttributeTagInfo, &info, (uint)sizeof(FILE_ATTRIBUTE_TAG_INFO))) return false;
        attributes = info.FileAttributes;
        reparseTag = info.ReparseTag;
        return true;
    }

    public static uint GetState(string path) =>
        TryGetAttributeTag(path, out var a, out var t) ? CfGetPlaceholderStateFromAttributeTag(a, t) : CF_PLACEHOLDER_STATE_INVALID;

    public static bool IsPlaceholder(string path)
    {
        var s = GetState(path);
        return s != CF_PLACEHOLDER_STATE_INVALID && (s & CF_PLACEHOLDER_STATE_PLACEHOLDER) != 0;
    }

    /// <summary>True for a folder that has been filled in (its contents exist on disk).</summary>
    public static bool IsPopulatedDirectory(string path)
    {
        var s = GetState(path);
        return s != CF_PLACEHOLDER_STATE_INVALID && (s & CF_PLACEHOLDER_STATE_PLACEHOLDER) != 0 && (s & CF_PLACEHOLDER_STATE_PARTIAL) == 0;
    }

    /// <summary>Changes a placeholder's size/dates/identity. With dehydrate, downloaded data is discarded.</summary>
    public static bool Update(string path, TreeEntry e, bool dehydrate)
    {
        if (CfOpenFileWithOplock(path, CF_OPEN_FILE_FLAG_WRITE_ACCESS, out var protectedHandle) < 0) return false;
        try
        {
            var handle = CfGetWin32HandleFromProtectedHandle(protectedHandle);
            var meta = Metadata(e);
            var idBytes = Encoding.UTF8.GetBytes(e.Identity);
            var flags = CF_UPDATE_FLAG_MARK_IN_SYNC | (dehydrate ? CF_UPDATE_FLAG_DEHYDRATE : 0);
            fixed (byte* id = idBytes)
            {
                var hr = CfUpdatePlaceholder(handle, &meta, id, (uint)idBytes.Length, IntPtr.Zero, 0, flags, IntPtr.Zero, IntPtr.Zero);
                if (hr < 0)
                {
                    Log.Warn($"CfUpdatePlaceholder {path} returned 0x{hr:X8}");
                    return false;
                }
            }
            return true;
        }
        finally
        {
            CfCloseHandle(protectedHandle);
        }
    }

    /// <summary>Frees the local copy of a downloaded file. Returns false if it is in use.</summary>
    public static bool Dehydrate(string path)
    {
        if (CfOpenFileWithOplock(path, CF_OPEN_FILE_FLAG_WRITE_ACCESS | CF_OPEN_FILE_FLAG_EXCLUSIVE, out var protectedHandle) < 0) return false;
        try
        {
            var handle = CfGetWin32HandleFromProtectedHandle(protectedHandle);
            return CfDehydratePlaceholder(handle, 0, -1, 0, IntPtr.Zero) >= 0;
        }
        finally
        {
            CfCloseHandle(protectedHandle);
        }
    }

    /// <summary>Downloads the whole file (blocks until done; runs the FETCH_DATA callback).</summary>
    public static int Hydrate(string path)
    {
        using var h = OpenForAttributes(path, read: true);
        if (h.IsInvalid) return Marshal.GetHRForLastWin32Error();
        return CfHydratePlaceholder(h, 0, -1, 0, IntPtr.Zero);
    }

    public static void ClearPin(string path)
    {
        using var h = CreateFileW(path, FILE_READ_ATTRIBUTES | 0x100 /* FILE_WRITE_ATTRIBUTES */, FILE_SHARE_ALL, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (!h.IsInvalid) CfSetPinState(h, CF_PIN_STATE_UNSPECIFIED, 0, IntPtr.Zero);
    }

    public static bool IsHydrated(uint attributes) => (attributes & FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS) == 0;
}
