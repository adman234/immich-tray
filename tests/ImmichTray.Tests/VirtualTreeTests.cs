using ImmichTray.Immich;
using ImmichTray.Settings;
using ImmichTray.Views;

namespace ImmichTray.Tests;

public class VirtualTreeTests
{
    private static (VirtualTree Tree, FakeImmich Fake, AppSettings Settings) Make(Action<AppSettings>? configure = null)
    {
        var fake = new FakeImmich();
        var settings = new AppSettings();
        configure?.Invoke(settings);
        return (new VirtualTree(fake, settings, FakeImmich.Me), fake, settings);
    }

    private static string[] Names(IReadOnlyList<TreeEntry> e) => e.Select(x => x.Name).ToArray();

    [Fact]
    public async Task Root_lists_enabled_views_in_order()
    {
        var (tree, _, s) = Make(s => { s.Views.AllPhotos = true; s.Views.People = false; });
        var root = await tree.ListAsync("");
        Assert.Equal(new[] { "Recent", "Timeline", "Albums", "Favorites", "All Photos" }, Names(root));
        Assert.All(root, e => Assert.True(e.IsDirectory));
    }

    [Fact]
    public async Task Disabled_view_lists_nothing()
    {
        var (tree, fake, _) = Make(s => s.Views.Favorites = false);
        fake.Add("a.jpg", new DateTime(2026, 1, 1), favorite: true);
        Assert.Empty(await tree.ListAsync("Favorites"));
    }

    [Fact]
    public async Task Timeline_years_months_and_local_date_grouping()
    {
        var (tree, fake, _) = Make();
        fake.Add("oct.jpg", new DateTime(2026, 10, 5, 12, 0, 0));
        // Local Oct 31 21:00 at UTC-5 is Nov 1 02:00 UTC: it belongs in October.
        fake.Add("boundary.jpg", new DateTime(2026, 10, 31, 21, 0, 0), offsetHours: -5);
        fake.Add("nov.jpg", new DateTime(2026, 11, 2, 8, 0, 0));
        fake.Add("old.jpg", new DateTime(2019, 3, 3));

        Assert.Equal(new[] { "2026", "2019" }, Names(await tree.ListAsync("Timeline")));
        Assert.Equal(new[] { "10 - October", "11 - November" }, Names(await tree.ListAsync(@"Timeline\2026")));
        var oct = await tree.ListAsync(@"Timeline\2026\10 - October");
        Assert.Equal(new[] { "boundary.jpg", "oct.jpg" }, Names(oct));
        Assert.Equal(new[] { "nov.jpg" }, Names(await tree.ListAsync(@"Timeline\2026\11 - November")));
        Assert.Empty(await tree.ListAsync(@"Timeline\2026\13 - Nope"));
    }

    [Fact]
    public async Task Duplicate_names_get_numbered_and_paging_is_followed()
    {
        var (tree, fake, _) = Make();
        for (var i = 0; i < 7; i++) fake.Add("IMG_0001.JPG", new DateTime(2026, 10, 1 + i));
        var entries = await tree.ListAsync(@"Timeline\2026\10 - October");
        Assert.Equal(7, entries.Count);
        Assert.Equal("IMG_0001.JPG", entries[0].Name);
        Assert.Equal("IMG_0001 (2).JPG", entries[1].Name);
        Assert.Equal(7, entries.Select(e => e.Name.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public async Task Partner_photos_filtered_unless_enabled()
    {
        var (tree, fake, settings) = Make();
        fake.Add("mine.jpg", new DateTime(2026, 10, 1));
        fake.Add("theirs.jpg", new DateTime(2026, 10, 2), owner: "partner");
        Assert.Equal(new[] { "mine.jpg" }, Names(await tree.ListAsync("Recent")));
        settings.IncludePartnerPhotos = true;
        Assert.Equal(2, (await tree.ListAsync("Recent")).Count);
    }

    [Fact]
    public async Task Albums_keep_other_owners_and_sanitize_names()
    {
        var (tree, fake, _) = Make();
        var a = fake.Add("a.jpg", new DateTime(2026, 1, 1));
        var b = fake.Add("b.jpg", new DateTime(2026, 1, 2), owner: "friend");
        fake.Albums.Add(new Album("al1", "Trip: Beach/Day", FakeImmich.Me, 2, DateTimeOffset.UnixEpoch, null));
        fake.Albums.Add(new Album("al2", "trip_ beach_day", FakeImmich.Me, 0, DateTimeOffset.UnixEpoch, null));
        fake.AlbumAssets["al1"] = new() { a.Id, b.Id };
        fake.AlbumAssets["al2"] = new();

        var albums = Names(await tree.ListAsync("Albums"));
        Assert.Equal(new[] { "Trip_ Beach_Day", "trip_ beach_day (2)" }, albums);
        var inside = await tree.ListAsync(@"Albums\Trip_ Beach_Day");
        Assert.Equal(new[] { "b.jpg", "a.jpg" }, Names(inside));
    }

    [Fact]
    public async Task Shared_albums_can_be_hidden()
    {
        var (tree, fake, settings) = Make();
        fake.Albums.Add(new Album("mine", "Mine", FakeImmich.Me, 0, DateTimeOffset.UnixEpoch, null));
        fake.Albums.Add(new Album("shared", "Shared", "friend", 0, DateTimeOffset.UnixEpoch, null));
        Assert.Equal(2, (await tree.ListAsync("Albums")).Count);
        settings.Views.IncludeSharedAlbums = false;
        tree.InvalidateCaches();
        Assert.Equal(new[] { "Mine" }, Names(await tree.ListAsync("Albums")));
    }

    [Fact]
    public async Task People_folders_and_contents()
    {
        var (tree, fake, _) = Make();
        var a = fake.Add("a.jpg", new DateTime(2026, 1, 1));
        fake.People.Add(new Person("p1", "Alice", null));
        fake.PersonAssets["p1"] = new() { a.Id };
        Assert.Equal(new[] { "Alice" }, Names(await tree.ListAsync("People")));
        Assert.Equal(new[] { "a.jpg" }, Names(await tree.ListAsync(@"People\Alice")));
        Assert.Empty(await tree.ListAsync(@"People\Bob"));
    }

    [Fact]
    public async Task Live_photo_video_is_added_when_enabled()
    {
        var (tree, fake, settings) = Make();
        var video = fake.Add("IMG_1.MOV", new DateTime(2026, 5, 5), kind: AssetKind.Video, visibility: "hidden", size: 5000);
        fake.Add("IMG_1.HEIC", new DateTime(2026, 5, 5), live: video.Id);
        Assert.Equal(new[] { "IMG_1.HEIC" }, Names(await tree.ListAsync("Recent")));
        settings.ShowLivePhotoVideos = true;
        var entries = await tree.ListAsync("Recent");
        Assert.Equal(new[] { "IMG_1.HEIC", "IMG_1.MOV" }, Names(entries));
        Assert.Equal(5000, entries[1].Size);
        Assert.Equal(video.Id, entries[1].AssetId);
    }

    [Fact]
    public async Task Date_naming_and_missing_size_lookup()
    {
        var (tree, fake, settings) = Make(s => s.Naming = FileNaming.DateTaken);
        fake.Add("IMG_9.HEIC", new DateTime(2026, 10, 30, 14, 22, 5), size: null);
        var e = Assert.Single(await tree.ListAsync("Recent"));
        Assert.Equal("2026-10-30 14.22.05.heic", e.Name);
        Assert.Equal(4242, e.Size); // from GetOriginalSizeAsync
        Assert.Equal("a:" + e.AssetId, e.Identity);
    }

    [Fact]
    public async Task Videos_excluded_when_disabled_and_recent_limit_applies()
    {
        var (tree, fake, settings) = Make(s => { s.IncludeVideos = false; s.Views.RecentCount = 2; });
        fake.Add("v.mp4", new DateTime(2026, 1, 4), kind: AssetKind.Video);
        fake.Add("1.jpg", new DateTime(2026, 1, 3));
        fake.Add("2.jpg", new DateTime(2026, 1, 2));
        fake.Add("3.jpg", new DateTime(2026, 1, 1));
        Assert.Equal(new[] { "1.jpg", "2.jpg" }, Names(await tree.ListAsync("Recent")));
        Assert.Equal(2, fake.Queries.Last().Limit);
    }

    [Fact]
    public async Task Signatures_track_bucket_counts_and_albums()
    {
        var (tree, fake, _) = Make();
        fake.Add("a.jpg", new DateTime(2026, 10, 1));
        var before = await tree.GetSignatureAsync(@"Timeline\2026\10 - October");
        fake.Add("b.jpg", new DateTime(2026, 10, 2));
        tree.InvalidateCaches();
        var after = await tree.GetSignatureAsync(@"Timeline\2026\10 - October");
        Assert.NotEqual(before, after);
        Assert.Null(await tree.GetSignatureAsync("Recent"));
    }

    [Theory]
    [InlineData(1, "01 - January")]
    [InlineData(12, "12 - December")]
    public void Month_folder_names_round_trip(int month, string name)
    {
        Assert.Equal(name, VirtualTree.MonthFolderName(month));
        Assert.True(VirtualTree.TryParseMonthFolder(name, out var m));
        Assert.Equal(month, m);
    }
}
