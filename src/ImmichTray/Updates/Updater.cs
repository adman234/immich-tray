using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ImmichTray.Updates;

public sealed record ReleaseInfo(string Tag, Version Version, string InstallerName, string InstallerUrl, string? ChecksumsUrl, string PageUrl);

/// <summary>Checks GitHub Releases for a newer installer and runs it. Only acts when asked.</summary>
internal sealed class Updater
{
    private static readonly HttpClient Http = CreateClient();

    public ReleaseInfo? LastFound { get; private set; }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ImmichTray", AppInfo.Version));
        return c;
    }

    public static Version CurrentVersion => ParseVersion(AppInfo.Version) ?? new Version(0, 0, 0);

    /// <summary>Returns the latest release if it is newer than this build, otherwise null.</summary>
    public async Task<ReleaseInfo?> CheckAsync(CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{AppInfo.GitHubOwner}/{AppInfo.GitHubRepo}/releases/latest";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var resp = await Http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("No releases have been published yet.");
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var release = ParseRelease(doc.RootElement);
        LastFound = release != null && release.Version > CurrentVersion ? release : null;
        return LastFound;
    }

    internal static ReleaseInfo? ParseRelease(JsonElement root)
    {
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseVersion(tag);
        if (version == null) return null;
        string? installerName = null, installerUrl = null, sumsUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var dl = asset.GetProperty("browser_download_url").GetString();
            if (name.StartsWith("ImmichTray-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                installerName = name;
                installerUrl = dl;
            }
            else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
            {
                sumsUrl = dl;
            }
        }
        if (installerName == null || installerUrl == null) return null;
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        return new ReleaseInfo(tag, version, installerName, installerUrl, sumsUrl, page);
    }

    /// <summary>Accepts "v1.2.3", "1.2.3", "1.2.3-beta"; ignores anything after a dash or plus.</summary>
    public static Version? ParseVersion(string text)
    {
        text = text.Trim().TrimStart('v', 'V');
        var cut = text.IndexOfAny(['-', '+']);
        if (cut >= 0) text = text[..cut];
        if (!Version.TryParse(text, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>Downloads the installer, checks its SHA-256, starts it silently. Caller then exits.</summary>
    public async Task<string> DownloadAndStartAsync(ReleaseInfo release, IProgress<int>? progress, CancellationToken ct = default)
    {
        var target = await DownloadVerifiedAsync(release, progress, ct);
        Log.Info($"Starting installer {target}");
        Process.Start(new ProcessStartInfo(target, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS")
        {
            UseShellExecute = true,
        });
        return target;
    }

    /// <summary>Downloads the installer and checks it against the release's SHA256SUMS.txt.</summary>
    public async Task<string> DownloadVerifiedAsync(ReleaseInfo release, IProgress<int>? progress, CancellationToken ct = default)
    {
        var dir = Path.Combine(AppInfo.DataDir, "updates");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir, "*.exe"))
        {
            try { File.Delete(old); } catch { }
        }
        var target = Path.Combine(dir, release.InstallerName);

        using (var resp = await Http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(target);
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((int)(done * 100 / total));
            }
        }

        if (release.ChecksumsUrl != null)
        {
            var sums = await Http.GetStringAsync(release.ChecksumsUrl, ct);
            var expected = FindChecksum(sums, release.InstallerName)
                ?? throw new InvalidOperationException("The release has no checksum for the installer.");
            string actual;
            await using (var fs = File.OpenRead(target))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(target);
                throw new InvalidOperationException("The downloaded installer did not match its checksum, so it was deleted.");
            }
        }
        else
        {
            File.Delete(target);
            throw new InvalidOperationException("The release has no SHA256SUMS.txt, so the installer cannot be verified.");
        }
        return target;
    }

    internal static string? FindChecksum(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split((char[])[' ', '\t', '*'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[^1].Equals(fileName, StringComparison.OrdinalIgnoreCase) && parts[0].Length == 64)
                return parts[0];
        }
        return null;
    }
}
