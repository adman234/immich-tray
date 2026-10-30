using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImmichTray.CloudFiles;
using ImmichTray.Immich;

namespace ImmichTray.Thumbnails;

[ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IClassFactory
{
    [PreserveSig] int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);
    [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
}

[ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetParent(out IShellItem ppsi);
    [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
    [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
}

[ComImport, Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithItem
{
    [PreserveSig] int Initialize(IShellItem psi, uint grfMode);
}

[ComImport, Guid("e357fccd-a995-4576-b01f-234630154e96"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IThumbnailProvider
{
    [PreserveSig] int GetThumbnail(uint cx, out IntPtr phbmp, out int pdwAlpha);
}

/// <summary>Explorer asks this for thumbnails of files that are not downloaded yet.</summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class ThumbnailProvider : IInitializeWithItem, IThumbnailProvider
{
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int WTSAT_RGB = 1;

    private readonly Func<IImmichClient?> _client;
    private string? _path;

    public ThumbnailProvider(Func<IImmichClient?> client) => _client = client;

    public int Initialize(IShellItem psi, uint grfMode)
    {
        try
        {
            if (psi.GetDisplayName(SIGDN_FILESYSPATH, out var p) >= 0 && p != IntPtr.Zero)
            {
                _path = Marshal.PtrToStringUni(p);
                Marshal.FreeCoTaskMem(p);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Log.Warn("Thumbnail Initialize failed", ex);
            return E_FAIL;
        }
    }

    public int GetThumbnail(uint cx, out IntPtr phbmp, out int pdwAlpha)
    {
        phbmp = IntPtr.Zero;
        pdwAlpha = WTSAT_RGB;
        try
        {
            if (_path == null || _client() is not { } client) return E_FAIL;
            var assetId = Placeholders.AssetIdFromIdentity(Placeholders.ReadIdentity(_path));
            if (assetId == null) return E_FAIL;

            var large = cx > 256;
            BitmapSource? image = null;
            try
            {
                image = Decode(client.GetThumbnailAsync(assetId, large).GetAwaiter().GetResult());
            }
            catch (NotSupportedException) when (!large)
            {
                // The small thumbnail is usually WebP. If Windows can't decode WebP, use the JPEG preview.
            }
            image ??= Decode(client.GetThumbnailAsync(assetId, true).GetAwaiter().GetResult());
            phbmp = ToHBitmap(image, (int)cx);
            return phbmp == IntPtr.Zero ? E_FAIL : 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"Thumbnail for {_path} failed", ex);
            return E_FAIL;
        }
    }

    private static BitmapSource Decode(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        try
        {
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames[0];
        }
        catch (Exception ex) when (ex is FileFormatException or NotSupportedException or COMException)
        {
            throw new NotSupportedException("Image format not supported", ex);
        }
    }

    private static IntPtr ToHBitmap(BitmapSource source, int maxSize)
    {
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest > maxSize && maxSize > 0)
        {
            var scale = (double)maxSize / longest;
            source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        }
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        bgra.CopyPixels(pixels, stride, 0);

        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        var hbmp = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        if (hbmp == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        return hbmp;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class ThumbnailClassFactory(Func<IImmichClient?> client) : IClassFactory
{
    private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);

    public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;
        if (pUnkOuter != IntPtr.Zero) return CLASS_E_NOAGGREGATION;
        var unk = Marshal.GetIUnknownForObject(new ThumbnailProvider(client));
        try { return Marshal.QueryInterface(unk, in riid, out ppvObject); }
        finally { Marshal.Release(unk); }
    }

    public int LockServer(bool fLock) => 0;
}

/// <summary>Publishes the thumbnail provider class so Explorer can create it in this process.</summary>
internal sealed class ThumbnailServer : IDisposable
{
    private const uint CLSCTX_LOCAL_SERVER = 0x4;
    private const uint REGCLS_MULTIPLEUSE = 1;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new();
    private uint _cookie;

    public ThumbnailServer(Func<IImmichClient?> client, bool enabled = true)
    {
        // A dedicated multi-threaded COM thread, so thumbnail calls never wait on the UI thread.
        _thread = new Thread(() =>
        {
            try
            {
                if (!enabled) { _stop.Wait(); return; }
                var clsid = AppInfo.ThumbnailProviderClsid;
                var hr = CoRegisterClassObject(ref clsid, new ThumbnailClassFactory(client), CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out _cookie);
                if (hr < 0) Log.Warn($"CoRegisterClassObject returned 0x{hr:X8}");
                else Log.Info("Thumbnail provider ready");
                _stop.Wait();
                if (_cookie != 0) CoRevokeClassObject(_cookie);
            }
            catch (Exception ex)
            {
                Log.Error("Thumbnail server failed", ex);
            }
        })
        { IsBackground = true, Name = "ThumbnailServer" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(ref Guid rclsid, [MarshalAs(UnmanagedType.IUnknown)] object pUnk, uint dwClsContext, uint flags, out uint lpdwRegister);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint dwRegister);
}
