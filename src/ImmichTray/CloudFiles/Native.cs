using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ImmichTray.CloudFiles;

/// <summary>
/// Hand-written bindings for the parts of the Windows Cloud Files API (cldapi.dll) this app uses.
/// Struct layouts follow cfapi.h for 64-bit Windows.
/// </summary>
internal static unsafe class Native
{
    // ---- callback types (CF_CALLBACK_TYPE)
    public const int CF_CALLBACK_TYPE_FETCH_DATA = 0;
    public const int CF_CALLBACK_TYPE_CANCEL_FETCH_DATA = 2;
    public const int CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS = 3;
    public const int CF_CALLBACK_TYPE_CANCEL_FETCH_PLACEHOLDERS = 4;
    public const int CF_CALLBACK_TYPE_NOTIFY_DELETE = 9;
    public const int CF_CALLBACK_TYPE_NOTIFY_RENAME = 11;
    public const int CF_CALLBACK_TYPE_NONE = unchecked((int)0xFFFFFFFF);

    // ---- operation types (CF_OPERATION_TYPE)
    public const int CF_OPERATION_TYPE_TRANSFER_DATA = 0;
    public const int CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS = 4;
    public const int CF_OPERATION_TYPE_ACK_DELETE = 6;
    public const int CF_OPERATION_TYPE_ACK_RENAME = 7;

    // ---- flags
    public const uint CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO = 0x2;
    public const uint CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH = 0x4;

    public const uint CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION = 0x1;
    public const uint CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC = 0x2;

    public const uint CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_DISABLE_ON_DEMAND_POPULATION = 0x2;

    public const uint CF_UPDATE_FLAG_MARK_IN_SYNC = 0x2;
    public const uint CF_UPDATE_FLAG_DEHYDRATE = 0x4;
    public const uint CF_UPDATE_FLAG_DISABLE_ON_DEMAND_POPULATION = 0x10;

    public const uint CF_OPEN_FILE_FLAG_EXCLUSIVE = 0x1;
    public const uint CF_OPEN_FILE_FLAG_WRITE_ACCESS = 0x2;

    public const int CF_PLACEHOLDER_INFO_BASIC = 0;
    public const int CF_PLACEHOLDER_INFO_STANDARD = 1;

    public const int CF_PIN_STATE_UNSPECIFIED = 0;
    public const int CF_PIN_STATE_PINNED = 1;
    public const int CF_PIN_STATE_UNPINNED = 2;

    public const uint CF_PLACEHOLDER_STATE_PLACEHOLDER = 0x1;
    public const uint CF_PLACEHOLDER_STATE_PARTIAL = 0x10;
    public const uint CF_PLACEHOLDER_STATE_INVALID = 0xFFFFFFFF;

    // ---- NTSTATUS
    public const int STATUS_SUCCESS = 0;
    public const int STATUS_UNSUCCESSFUL = unchecked((int)0xC0000001);
    public const int STATUS_ACCESS_DENIED = unchecked((int)0xC0000022);

    // ---- file attributes
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    public const uint FILE_ATTRIBUTE_PINNED = 0x00080000;
    public const uint FILE_ATTRIBUTE_UNPINNED = 0x00100000;
    public const uint FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x00040000;
    public const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000;

    [StructLayout(LayoutKind.Sequential)]
    public struct CF_CALLBACK_INFO
    {
        public uint StructSize;
        public long ConnectionKey;
        public IntPtr CallbackContext;
        public IntPtr VolumeGuidName;
        public IntPtr VolumeDosName;
        public uint VolumeSerialNumber;
        public long SyncRootFileId;
        public IntPtr SyncRootIdentity;
        public uint SyncRootIdentityLength;
        public long FileId;
        public long FileSize;
        public IntPtr FileIdentity;
        public uint FileIdentityLength;
        public IntPtr NormalizedPath;
        public long TransferKey;
        public byte PriorityHint;
        public IntPtr CorrelationVector;
        public IntPtr ProcessInfo;
        public long RequestKey;
    }

    /// <summary>CF_CALLBACK_PARAMETERS is a union; fields are read by offset from its start.</summary>
    public static class CallbackParams
    {
        // The union starts at offset 8 (after ULONG ParamSize and padding).
        public const int FetchData_RequiredFileOffset = 16;
        public const int FetchData_RequiredLength = 24;
        public const int FetchData_OptionalFileOffset = 32;
        public const int FetchData_OptionalLength = 40;
        public const int Rename_TargetPath = 16;
        public const int Delete_Flags = 8;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CF_OPERATION_INFO
    {
        public uint StructSize;
        public int Type;
        public long ConnectionKey;
        public long TransferKey;
        public IntPtr CorrelationVector;
        public IntPtr SyncStatus;
        public long RequestKey;
    }

    /// <summary>CF_OPERATION_PARAMETERS union. Field names are prefixed by the member they belong to.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 48)]
    public struct CF_OPERATION_PARAMETERS
    {
        [FieldOffset(0)] public uint ParamSize;
        [FieldOffset(8)] public uint Flags;
        [FieldOffset(12)] public int CompletionStatus;

        // TransferData
        [FieldOffset(16)] public IntPtr TD_Buffer;
        [FieldOffset(24)] public long TD_Offset;
        [FieldOffset(32)] public long TD_Length;

        // TransferPlaceholders
        [FieldOffset(16)] public long TP_PlaceholderTotalCount;
        [FieldOffset(24)] public IntPtr TP_PlaceholderArray;
        [FieldOffset(32)] public uint TP_PlaceholderCount;
        [FieldOffset(36)] public uint TP_EntriesProcessed;

        public const uint SizeTransferData = 40;
        public const uint SizeTransferPlaceholders = 40;
        public const uint SizeAck = 16;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_BASIC_INFO
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CF_FS_METADATA
    {
        public FILE_BASIC_INFO BasicInfo;
        public long FileSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CF_PLACEHOLDER_CREATE_INFO
    {
        public IntPtr RelativeFileName;
        public CF_FS_METADATA FsMetadata;
        public IntPtr FileIdentity;
        public uint FileIdentityLength;
        public uint Flags;
        public int Result;
        public long CreateUsn;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CF_CALLBACK_REGISTRATION
    {
        public int Type;
        public IntPtr Callback;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_ATTRIBUTE_TAG_INFO
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    private const string Cld = "cldapi.dll";

    [DllImport(Cld, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int CfConnectSyncRoot(string syncRootPath, CF_CALLBACK_REGISTRATION* callbackTable, IntPtr callbackContext, uint connectFlags, out long connectionKey);

    [DllImport(Cld, ExactSpelling = true)]
    public static extern int CfDisconnectSyncRoot(long connectionKey);

    [DllImport(Cld, ExactSpelling = true)]
    public static extern int CfExecute(CF_OPERATION_INFO* opInfo, CF_OPERATION_PARAMETERS* opParams);

    [DllImport(Cld, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int CfCreatePlaceholders(string baseDirectoryPath, CF_PLACEHOLDER_CREATE_INFO* placeholderArray, uint placeholderCount, uint createFlags, out uint entriesProcessed);

    [DllImport(Cld, ExactSpelling = true)]
    public static extern int CfUpdatePlaceholder(IntPtr fileHandle, CF_FS_METADATA* fsMetadata, void* fileIdentity, uint fileIdentityLength,
        IntPtr dehydrateRangeArray, uint dehydrateRangeCount, uint updateFlags, IntPtr updateUsn, IntPtr overlapped);

    [DllImport(Cld, ExactSpelling = true)]
    public static extern int CfGetPlaceholderInfo(SafeFileHandle fileHandle, int infoClass, void* infoBuffer, uint infoBufferLength, out uint returnedLength);

    [DllImport(Cld, ExactSpelling = true)]
    public static extern int CfSetPinState(SafeFileHandle fileHandle, int pinState, uint pinFlags, IntPtr overlapped);

    [DllImport(Cld, ExactSpelling = true)]
    public static extern int CfHydratePlaceholder(SafeFileHandle fileHandle, long startingOffset, long length, uint hydrateFlags, IntPtr overlapped);

    [DllImport(Cld, ExactSpelling = true)]
    public static extern int CfDehydratePlaceholder(IntPtr fileHandle, long startingOffset, long length, uint dehydrateFlags, IntPtr overlapped);

    [DllImport(Cld, ExactSpelling = true)]
    public static extern int CfReportProviderProgress(long connectionKey, long transferKey, long providerProgressTotal, long providerProgressCompleted);

    [DllImport(Cld, ExactSpelling = true)]
    public static extern uint CfGetPlaceholderStateFromAttributeTag(uint fileAttributes, uint reparseTag);

    [DllImport(Cld, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int CfUnregisterSyncRoot(string syncRootPath);

    [DllImport(Cld, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int CfOpenFileWithOplock(string filePath, uint flags, out IntPtr protectedHandle);

    [DllImport(Cld, ExactSpelling = true)]
    public static extern IntPtr CfGetWin32HandleFromProtectedHandle(IntPtr protectedHandle);

    [DllImport(Cld, ExactSpelling = true)]
    public static extern void CfCloseHandle(IntPtr fileHandle);

    // ---- ntdll

    public const sbyte PHCM_EXPOSE_PLACEHOLDERS = 2;

    /// <summary>
    /// Without this, Windows may hide that files are placeholders from this process,
    /// and the app could not tell its own files from files the user added.
    /// </summary>
    [DllImport("ntdll.dll", ExactSpelling = true)]
    public static extern sbyte RtlSetProcessPlaceholderCompatibilityMode(sbyte mode);

    // ---- kernel32

    public const uint FILE_READ_ATTRIBUTES = 0x80;
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_ALL = 0x7;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    public const int FileAttributeTagInfo = 9;

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, void* info, uint size);

    /// <summary>Opens a file or directory just to read metadata. Never triggers a download.</summary>
    public static SafeFileHandle OpenForAttributes(string path, bool read = false) =>
        CreateFileW(path, read ? GENERIC_READ : FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);

    public static long ToFileTime(DateTimeOffset t)
    {
        try { return t.UtcDateTime.ToFileTimeUtc(); }
        catch (ArgumentOutOfRangeException) { return 0; }
    }

    public static void ThrowIfFailed(int hr, string what)
    {
        if (hr < 0) throw new System.ComponentModel.Win32Exception(hr, $"{what} failed (0x{hr:X8})");
    }
}
