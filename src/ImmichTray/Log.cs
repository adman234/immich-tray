using System.Text;

namespace ImmichTray;

/// <summary>Tiny append-only file logger. Keeps the current file under ~2 MB with one backup.</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;
    private static string? _path;

    public static string FilePath => _path ??= InitPath();

    private static string InitPath()
    {
        Directory.CreateDirectory(AppInfo.LogDir);
        return Path.Combine(AppInfo.LogDir, "immich-tray.log");
    }

    public static void Info(string message) => Write("INFO ", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN ", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').Append(level).Append(' ')
          .Append('[').Append(Environment.CurrentManagedThreadId).Append("] ").Append(message);
        if (ex != null) sb.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
        sb.AppendLine();
        if (ex != null && level == "ERROR") sb.AppendLine(ex.ToString());
        try
        {
            lock (Gate)
            {
                var path = FilePath;
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    var old = path + ".1";
                    File.Delete(old);
                    File.Move(path, old);
                }
                File.AppendAllText(path, sb.ToString());
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
#if DEBUG
        System.Diagnostics.Debug.Write(sb.ToString());
#endif
    }
}
