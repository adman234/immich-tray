using System.Text;

namespace ImmichTray.Views;

/// <summary>Makes names safe for Windows and unique within one folder (case-insensitive).</summary>
public sealed class NameAllocator
{
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public const int MaxNameLength = 180;

    /// <summary>Returns a unique, valid name. Duplicates become "name (2).ext", "name (3).ext", ...</summary>
    public string Allocate(string desired, bool isDirectory = false)
    {
        var clean = Sanitize(desired, isDirectory);
        if (_used.Add(clean)) return clean;

        var ext = isDirectory ? "" : Path.GetExtension(clean);
        var stem = clean[..^ext.Length];
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (_used.Add(candidate)) return candidate;
        }
    }

    public static string Sanitize(string name, bool isDirectory = false)
    {
        name ??= "";
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (ch < 32 || ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
                sb.Append('_');
            else
                sb.Append(ch);
        }
        var s = sb.ToString().Trim().TrimEnd('.', ' ');
        if (s.Length == 0) s = isDirectory ? "Untitled" : "file";

        var ext = isDirectory ? "" : Path.GetExtension(s);
        if (ext.Length > 16) ext = "";
        var stem = ext.Length > 0 ? s[..^ext.Length] : s;
        if (stem.Length == 0) stem = "file";
        if (Reserved.Contains(stem) || (stem.Contains('.') && Reserved.Contains(stem[..stem.IndexOf('.')])))
            stem = "_" + stem;
        if (stem.Length + ext.Length > MaxNameLength)
            stem = stem[..(MaxNameLength - ext.Length)].TrimEnd('.', ' ');
        return stem + ext;
    }
}
