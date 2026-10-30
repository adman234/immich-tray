using ImmichTray.CloudFiles;
using ImmichTray.Immich;
using ImmichTray.Settings;

namespace ImmichTray.Tests;

public class ChangeTrackerTests
{
    private static Asset Touch(FakeImmich fake, Asset a, DateTimeOffset at)
    {
        var updated = a with { UpdatedAt = at };
        fake.Assets[fake.Assets.IndexOf(a)] = updated;
        return updated;
    }

    [Fact]
    public async Task FirstCheckOnlyReadsTheClock()
    {
        var fake = new FakeImmich();
        var t = new ChangeTracker();
        Assert.Null(await t.CheckAsync(fake, false, default));
        Assert.Equal(fake.ServerNow.AddSeconds(-10), t.Watermark);
    }

    [Fact]
    public async Task NewPhotoMarksItsMonthAndAllPhotos()
    {
        var fake = new FakeImmich();
        var t = new ChangeTracker();
        await t.CheckAsync(fake, false, default);

        var a = fake.Add("new.jpg", new DateTime(2024, 3, 5, 23, 30, 0));
        Touch(fake, a, fake.ServerNow.AddSeconds(5));

        var changes = await t.CheckAsync(fake, false, default);
        Assert.NotNull(changes);
        Assert.False(changes!.Everything);
        Assert.Equal(1, changes.AssetCount);
        Assert.True(changes.Affects(@"Timeline\2024\03 - March"));
        Assert.True(changes.Affects("All Photos"));
        Assert.True(changes.Affects(@"Albums\Trip"));
        Assert.False(changes.Affects(@"Timeline\2024\04 - April"));
    }

    [Fact]
    public async Task SameChangeIsReportedOnce()
    {
        var fake = new FakeImmich();
        var t = new ChangeTracker();
        await t.CheckAsync(fake, false, default);
        var a = fake.Add("x.jpg", new DateTime(2025, 1, 1));
        a = Touch(fake, a, fake.ServerNow.AddSeconds(5));

        Assert.NotNull(await t.CheckAsync(fake, false, default));
        // The fake repeats items at the watermark, like Immich's millisecond rounding can.
        Assert.Null(await t.CheckAsync(fake, false, default));

        Touch(fake, a, fake.ServerNow.AddSeconds(9)); // edited again
        Assert.NotNull(await t.CheckAsync(fake, false, default));
    }

    [Fact]
    public async Task TooManyChangesMeansEverything()
    {
        var fake = new FakeImmich { ChangeLimit = 2 };
        var t = new ChangeTracker();
        await t.CheckAsync(fake, false, default);
        for (var i = 0; i < 3; i++)
            Touch(fake, fake.Add($"{i}.jpg", new DateTime(2025, 1, 1)), fake.ServerNow.AddSeconds(1 + i));

        var changes = await t.CheckAsync(fake, false, default);
        Assert.True(changes!.Everything);
        Assert.True(changes.Affects(@"People\Someone"));
    }

    [Fact]
    public async Task AlbumChangesAreNoticed()
    {
        var fake = new FakeImmich();
        var album = new Album("al1", "Trip", FakeImmich.Me, 3, fake.ServerNow, null);
        fake.Albums.Add(album);
        var t = new ChangeTracker();
        await t.CheckAsync(fake, true, default);
        Assert.Null(await t.CheckAsync(fake, true, default));

        fake.Albums[0] = album with { AssetCount = 4 };
        var changes = await t.CheckAsync(fake, true, default);
        Assert.NotNull(changes);
        Assert.True(changes!.Affects(@"Albums\Trip"));
        Assert.False(changes.Affects(@"Timeline\2025\01 - January"));
    }

    [Fact]
    public void MergeKeepsBoth()
    {
        var a = new ChangeSet(false, new HashSet<string> { "A" }, false, 1);
        var b = new ChangeSet(false, new HashSet<string> { "B" }, true, 2);
        var m = a.Merge(b);
        Assert.True(m.Affects("A") && m.Affects("B") && m.Affects(@"Albums\X"));
        Assert.Equal(3, m.AssetCount);
    }
}

public class SettingsMigrationTests
{
    [Fact]
    public void OldSettingsGetCleanupTurnedOn()
    {
        // What 0.1.0 wrote: no version, no new fields, limit off.
        var s = SettingsStore.Deserialize("""{"ServerUrl":"http://x","CacheLimitEnabled":false,"CacheLimitGb":20}""")!;
        Assert.Equal(0, s.SettingsVersion);
        Assert.True(SettingsStore.Migrate(s));
        Assert.True(s.CacheLimitEnabled);
        Assert.Equal(10, s.CacheLimitGb);
        Assert.True(s.FreeUnusedEnabled);
        Assert.Equal(30, s.FreeUnusedDays);
        Assert.True(s.QuickChecks);
        Assert.False(SettingsStore.Migrate(s));
    }

    [Fact]
    public void OldSettingsKeepAChosenLimit()
    {
        var s = SettingsStore.Deserialize("""{"CacheLimitEnabled":true,"CacheLimitGb":50}""")!;
        SettingsStore.Migrate(s);
        Assert.Equal(50, s.CacheLimitGb);
    }

    [Fact]
    public void NewChoicesSurviveLaterLoads()
    {
        var s = new AppSettings { SettingsVersion = AppSettings.CurrentSettingsVersion, FreeUnusedEnabled = false, CacheLimitEnabled = false, QuickChecks = false };
        var back = SettingsStore.Deserialize(SettingsStore.Serialize(s))!;
        Assert.False(SettingsStore.Migrate(back));
        Assert.False(back.FreeUnusedEnabled);
        Assert.False(back.CacheLimitEnabled);
        Assert.False(back.QuickChecks);
    }
}
