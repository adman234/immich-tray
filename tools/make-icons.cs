// Generates the app icons. Run from the repo root:  dotnet run tools/make-icons.cs
#:property TargetFramework=net10.0-windows
#:property UseWindowsForms=true
#:property PublishAot=false
#:property PublishTrimmed=false

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

var outDir = Path.Combine("src", "ImmichTray", "Assets");
Directory.CreateDirectory(outDir);
WriteIco(Path.Combine(outDir, "immich-tray.ico"), false);
WriteIco(Path.Combine(outDir, "immich-tray-offline.ico"), true);
Console.WriteLine("Icons written to " + outDir);

static void WriteIco(string path, bool offline)
{
    int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
    var images = sizes.Select(s => Render(s, offline)).ToList();
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    w.Write((short)0); w.Write((short)1); w.Write((short)images.Count);
    var offset = 6 + 16 * images.Count;
    for (var i = 0; i < images.Count; i++)
    {
        var s = sizes[i];
        w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
        w.Write((byte)0); w.Write((byte)0);
        w.Write((short)1); w.Write((short)32);
        w.Write(images[i].Length); w.Write(offset);
        offset += images[i].Length;
    }
    foreach (var img in images) w.Write(img);
}

static byte[] Render(int size, bool offline)
{
    using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp))
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        float s = size;
        var pad = s * 0.04f;
        var tile = new RectangleF(pad, pad, s - 2 * pad, s - 2 * pad);
        var top = offline ? Color.FromArgb(150, 156, 166) : Color.FromArgb(66, 133, 244);
        var bottom = offline ? Color.FromArgb(104, 110, 120) : Color.FromArgb(32, 84, 196);
        using (var path = Rounded(tile, s * 0.22f))
        using (var brush = new LinearGradientBrush(tile, top, bottom, 90f))
            g.FillPath(brush, path);

        // Photo frame
        var frame = new RectangleF(s * 0.2f, s * 0.26f, s * 0.6f, s * 0.48f);
        using (var framePath = Rounded(frame, s * 0.07f))
        using (var white = new SolidBrush(Color.White))
            g.FillPath(white, framePath);

        // Mountains and sun inside the frame
        var inner = new RectangleF(frame.X + s * 0.05f, frame.Y + s * 0.05f, frame.Width - s * 0.1f, frame.Height - s * 0.1f);
        using var clip = Rounded(inner, s * 0.035f);
        g.SetClip(clip);
        using (var sky = new SolidBrush(offline ? Color.FromArgb(214, 218, 224) : Color.FromArgb(205, 225, 255)))
            g.FillRectangle(sky, inner);
        using (var sun = new SolidBrush(offline ? Color.FromArgb(170, 174, 182) : Color.FromArgb(255, 193, 7)))
            g.FillEllipse(sun, inner.Right - inner.Width * 0.36f, inner.Y + inner.Height * 0.12f, inner.Width * 0.22f, inner.Width * 0.22f);
        using (var hill = new SolidBrush(offline ? Color.FromArgb(120, 126, 136) : Color.FromArgb(52, 168, 83)))
        {
            g.FillPolygon(hill, new[]
            {
                new PointF(inner.X - 1, inner.Bottom + 1),
                new PointF(inner.X + inner.Width * 0.38f, inner.Y + inner.Height * 0.38f),
                new PointF(inner.X + inner.Width * 0.62f, inner.Y + inner.Height * 0.7f),
                new PointF(inner.X + inner.Width * 0.78f, inner.Y + inner.Height * 0.55f),
                new PointF(inner.Right + 1, inner.Y + inner.Height * 0.8f),
                new PointF(inner.Right + 1, inner.Bottom + 1),
            });
        }
        g.ResetClip();
    }
    return size >= 128 ? Png(bmp) : Dib(bmp);
}

static byte[] Png(Bitmap bmp)
{
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

// Classic icon bitmap: BITMAPINFOHEADER (double height), bottom-up BGRA rows, then a blank AND mask.
static byte[] Dib(Bitmap bmp)
{
    int n = bmp.Width;
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write(40); w.Write(n); w.Write(n * 2); w.Write((short)1); w.Write((short)32);
    w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
    for (var y = n - 1; y >= 0; y--)
        for (var x = 0; x < n; x++)
        {
            var c = bmp.GetPixel(x, y);
            w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
        }
    var maskRow = ((n + 31) / 32) * 4;
    w.Write(new byte[maskRow * n]);
    return ms.ToArray();
}

static GraphicsPath Rounded(RectangleF r, float radius)
{
    var d = radius * 2;
    var p = new GraphicsPath();
    p.AddArc(r.X, r.Y, d, d, 180, 90);
    p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
}
