using System.Runtime.CompilerServices;
using ImmichTray.Immich;

namespace ImmichTray.Tests;

/// <summary>In-memory IImmichClient that applies AssetQuery roughly the way the server does.</summary>
internal sealed class FakeImmich : IImmichClient
{
    public const string Me = "me";
    public List<Asset> Assets { get; } = new();
    public List<Album> Albums { get; } = new();
    public Dictionary<string, HashSet<string>> AlbumAssets { get; } = new();
    public List<Person> People { get; } = new();
    public Dictionary<string, HashSet<string>> PersonAssets { get; } = new();
    public List<AssetQuery> Queries { get; } = new();
    public int PageSize { get; set; } = 3;

    public Asset Add(string name, DateTime localTime, int offsetHours = 0, string owner = Me, AssetKind kind = AssetKind.Image,
        bool favorite = false, string visibility = "timeline", long? size = 1000, string? live = null, string? id = null)
    {
        var a = new Asset
        {
            Id = id ?? Guid.NewGuid().ToString(),
            OwnerId = owner,
            OriginalFileName = name,
            FileSize = size,
            LocalDateTime = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified),
            FileCreatedAt = new DateTimeOffset(localTime, TimeSpan.Zero).AddHours(-offsetHours),
            Kind = kind,
            IsFavorite = favorite,
            Visibility = visibility,
            LivePhotoVideoId = live,
        };
        Assets.Add(a);
        return a;
    }

    public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(true);
    public Task<ServerAbout> GetAboutAsync(CancellationToken ct = default) => Task.FromResult(new ServerAbout("v3.3.5"));
    public Task<UserInfo> GetMeAsync(CancellationToken ct = default) => Task.FromResult(new UserInfo(Me, "Me", "me@example.com"));

    public Task<IReadOnlyList<TimeBucket>> GetTimeBucketsAsync(bool includeArchived, bool withPartners, CancellationToken ct = default)
    {
        var buckets = Assets
            .Where(a => a.Visibility == "timeline" || (includeArchived && a.Visibility == "archive"))
            .Where(a => withPartners || a.OwnerId == Me)
            .GroupBy(a => (a.LocalDateTime.Year, a.LocalDateTime.Month))
            .Select(g => new TimeBucket(g.Key.Year, g.Key.Month, g.Count()))
            .ToList();
        return Task.FromResult<IReadOnlyList<TimeBucket>>(buckets);
    }

    public async IAsyncEnumerable<IReadOnlyList<Asset>> SearchAsync(AssetQuery q, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Queries.Add(q);
        var vis = q.IncludeArchived || q.AlbumId != null ? new[] { "timeline", "archive" } : new[] { "timeline" };
        var results = Assets
            .Where(a => vis.Contains(a.Visibility))
            .Where(a => q.IncludeVideos || a.Kind == AssetKind.Image)
            .Where(a => q.TakenFrom == null || a.FileCreatedAt >= q.TakenFrom)
            .Where(a => q.TakenBefore == null || a.FileCreatedAt < q.TakenBefore)
            .Where(a => q.AlbumId == null || AlbumAssets[q.AlbumId].Contains(a.Id))
            .Where(a => q.PersonId == null || PersonAssets[q.PersonId].Contains(a.Id))
            .Where(a => !q.FavoritesOnly || a.IsFavorite)
            .OrderByDescending(a => a.FileCreatedAt)
            .ToList();
        if (q.Limit is int limit) results = results.Take(limit).ToList();
        for (var i = 0; i < results.Count; i += PageSize)
        {
            await Task.Yield();
            yield return results.Skip(i).Take(PageSize).ToList();
        }
    }

    public Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Album>>(Albums);
    public Task<IReadOnlyList<Person>> GetPeopleAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Person>>(People);
    public Task<Asset> GetAssetAsync(string id, CancellationToken ct = default) => Task.FromResult(Assets.First(a => a.Id == id));
    public Task<long> GetTotalAssetCountAsync(bool includeArchived, CancellationToken ct = default) => Task.FromResult((long)Assets.Count);
    public DateTimeOffset ServerNow { get; set; } = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    /// <summary>Like Immich: "after" compares at full precision but times come back cut to milliseconds, so edge items repeat.</summary>
    public int ChangeLimit { get; set; } = 500;
    public Task<ChangeBatch> GetChangesSinceAsync(DateTimeOffset? since, CancellationToken ct = default)
    {
        if (since == null) return Task.FromResult(new ChangeBatch(Array.Empty<Asset>(), false, ServerNow));
        var hits = Assets.Where(a => a.UpdatedAt >= since.Value).ToList();
        return Task.FromResult(new ChangeBatch(hits.Take(ChangeLimit).ToList(), hits.Count > ChangeLimit, ServerNow));
    }
    public Task<Stream> OpenOriginalAsync(string id, long offset, long? endExclusive, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<long?> GetOriginalSizeAsync(string id, CancellationToken ct = default) => Task.FromResult<long?>(4242);
    public Task<byte[]> GetThumbnailAsync(string id, bool large, CancellationToken ct = default) => throw new NotSupportedException();
}
