using System.Text.Json;
using ImmichTray.CloudFiles;
using ImmichTray.Immich;
using ImmichTray.Settings;
using ImmichTray.Updates;
using ImmichTray.Views;

namespace ImmichTray.Tests;

public class NameAllocatorTests
{
    [Theory]
    [InlineData("a:b*c?.jpg", "a_b_c_.jpg")]
    [InlineData("CON.jpg", "_CON.jpg")]
    [InlineData("nul", "_nul")]
    [InlineData("trailing. ", "trailing")]
    [InlineData("", "file")]
    [InlineData("tab\there.png", "tab_here.png")]
    public void Sanitizes(string input, string expected) => Assert.Equal(expected, NameAllocator.Sanitize(input));

    [Fact]
    public void Long_names_are_trimmed_keeping_extension()
    {
        var name = NameAllocator.Sanitize(new string('x', 400) + ".jpeg");
        Assert.True(name.Length <= NameAllocator.MaxNameLength);
        Assert.EndsWith(".jpeg", name);
    }

    [Fact]
    public void Duplicates_are_case_insensitive()
    {
        var n = new NameAllocator();
        Assert.Equal("IMG.JPG", n.Allocate("IMG.JPG"));
        Assert.Equal("img (2).jpg", n.Allocate("img.jpg"));
        Assert.Equal("IMG (3).JPG", n.Allocate("IMG.JPG"));
        Assert.Equal("Album", n.Allocate("Album", true));
        Assert.Equal("album (2)", n.Allocate("album", true));
    }
}

public class UpdaterTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("v2.0.0-beta.1", "2.0.0")]
    [InlineData("0.1.0+abc", "0.1.0")]
    public void Parses_versions(string tag, string expected) => Assert.Equal(Version.Parse(expected), Updater.ParseVersion(tag));

    [Fact]
    public void Rejects_garbage() => Assert.Null(Updater.ParseVersion("latest"));

    [Fact]
    public void Finds_checksum_lines()
    {
        var hash = new string('a', 64);
        var sums = $"{new string('b', 64)}  other.exe\n{hash}  ImmichTray-Setup-1.0.0.exe\n";
        Assert.Equal(hash, Updater.FindChecksum(sums, "ImmichTray-Setup-1.0.0.exe"));
        Assert.Null(Updater.FindChecksum(sums, "missing.exe"));
    }

    [Fact]
    public void Parses_release_json()
    {
        var json = """
        {"tag_name":"v1.4.0","html_url":"https://x","assets":[
          {"name":"SHA256SUMS.txt","browser_download_url":"https://x/sums"},
          {"name":"ImmichTray-Setup-1.4.0.exe","browser_download_url":"https://x/setup"}]}
        """;
        using var doc = JsonDocument.Parse(json);
        var r = Updater.ParseRelease(doc.RootElement)!;
        Assert.Equal(new Version(1, 4, 0), r.Version);
        Assert.Equal("https://x/setup", r.InstallerUrl);
        Assert.Equal("https://x/sums", r.ChecksumsUrl);
    }
}

public class ClientTests
{
    [Fact]
    public void Filter_excludes_trash_and_sets_visibility()
    {
        var f = ImmichClient.BuildFilter(new AssetQuery { IncludeVideos = false, FavoritesOnly = true });
        var json = f.ToJsonString();
        Assert.Contains("\"trashedAt\":{\"eq\":null}", json);
        Assert.Contains("\"visibility\":{\"in\":[\"timeline\"]}", json);
        Assert.Contains("\"type\":{\"in\":[\"IMAGE\"]}", json);
        Assert.Contains("\"isFavorite\":{\"eq\":true}", json);
    }

    [Fact]
    public void Album_filter_includes_archive()
    {
        var json = ImmichClient.BuildFilter(new AssetQuery { AlbumId = "x" }).ToJsonString();
        Assert.Contains("\"albumIds\":{\"any\":[\"x\"]}", json);
        Assert.Contains("\"archive\"", json);
    }

    [Fact]
    public void Parses_asset_local_time_as_wall_clock()
    {
        var json = """
        {"id":"1","ownerId":"o","originalFileName":"a.jpg","type":"IMAGE","fileCreatedAt":"2026-11-01T02:00:00.000Z",
         "localDateTime":"2026-10-31T21:00:00.000Z","isFavorite":true,"isTrashed":false,"visibility":"timeline",
         "exifInfo":{"fileSizeInByte":1234}}
        """;
        using var doc = JsonDocument.Parse(json);
        var a = ImmichClient.ParseAsset(doc.RootElement);
        Assert.Equal(new DateTime(2026, 10, 31, 21, 0, 0), a.LocalDateTime);
        Assert.Equal(DateTimeKind.Unspecified, a.LocalDateTime.Kind);
        Assert.Equal(1234, a.FileSize);
        Assert.True(a.IsFavorite);
    }

    [Theory]
    [InlineData("192.168.1.10:2283", "http://192.168.1.10:2283")]
    [InlineData("https://photos.example.com/api/", "https://photos.example.com")]
    [InlineData(" http://x/ ", "http://x")]
    public void Normalizes_urls(string input, string expected) => Assert.Equal(expected, AppSettings.NormalizeUrl(input));
}

public class SettingsTests
{
    [Fact]
    public void Round_trips_and_protects_key()
    {
        var s = new AppSettings { ServerUrl = "http://x", ApiKey = "secret-key", MountMode = MountMode.Manual, Naming = FileNaming.DateTaken };
        s.Views.AllPhotos = true;
        var json = SettingsStore.Serialize(s);
        Assert.DoesNotContain("secret-key", json);
        Assert.Contains("\"Manual\"", json);
        var back = SettingsStore.Deserialize(json)!;
        Assert.Equal("secret-key", back.ApiKey);
        Assert.True(back.Views.AllPhotos);
        Assert.Equal(s.ContentFingerprint, back.ContentFingerprint);
    }

    [Fact]
    public void Fingerprint_changes_with_content_settings_only()
    {
        var s = new AppSettings();
        var f = s.ContentFingerprint;
        s.RefreshMinutes = 99;
        Assert.Equal(f, s.ContentFingerprint);
        s.IncludeArchived = true;
        Assert.NotEqual(f, s.ContentFingerprint);
    }

    [Fact]
    public void Align_up_to_4k()
    {
        Assert.Equal(0, SyncProvider.AlignUp(0));
        Assert.Equal(4096, SyncProvider.AlignUp(1));
        Assert.Equal(4096, SyncProvider.AlignUp(4096));
        Assert.Equal(8192, SyncProvider.AlignUp(4097));
    }
}

/// <summary>Talks to the real GitHub release. Runs only when IMMICHTRAY_UPDATE_TEST=1.</summary>
public class LiveUpdateTests
{
    [Fact]
    public async Task Latest_release_downloads_and_verifies()
    {
        if (Environment.GetEnvironmentVariable("IMMICHTRAY_UPDATE_TEST") != "1") return;
        var updater = new Updater();
        var release = await updater.CheckAsync();
        Assert.NotNull(release); // test builds are version 0.0.0, so any release is newer
        var path = await updater.DownloadVerifiedAsync(release!, null);
        Assert.True(new FileInfo(path).Length > 10_000_000);
        File.Delete(path);
    }
}
