using System.Collections.Concurrent;
using System.Globalization;
using ImmichTray.Immich;
using ImmichTray.Settings;

namespace ImmichTray.Views;

/// <summary>One file or folder inside the Immich folder.</summary>
public sealed record TreeEntry(string Name, bool IsDirectory, string? AssetId, long Size, DateTimeOffset Created, DateTimeOffset Modified)
{
    /// <summary>What gets stored inside the placeholder so we can find the asset again later.</summary>
    public string Identity => IsDirectory ? "d" : "a:" + AssetId;

    public static TreeEntry Dir(string name, DateTimeOffset? time = null)
    {
        var t = time ?? FolderTime;
        return new TreeEntry(name, true, null, 0, t, t);
    }

    public static readonly DateTimeOffset FolderTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>
/// Maps paths inside the Immich folder to Immich queries. Paths are relative to the
/// folder root and use backslashes, e.g. "Timeline\2026\10 - October".
/// </summary>
public sealed class VirtualTree
{
    public const string RecentFolder = "Recent";
    public const string TimelineFolder = "Timeline";
    public const string AlbumsFolder = "Albums";
    public const string PeopleFolder = "People";
    public const string FavoritesFolder = "Favorites";
    public const string AllPhotosFolder = "All Photos";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);

    private readonly IImmichClient _client;
    private readonly AppSettings _settings;
    private readonly string _meId;
    private readonly ConcurrentDictionary<string, Asset> _liveVideos = new();

    private (DateTime At, IReadOnlyList<TimeBucket> Value)? _buckets;
    private (DateTime At, IReadOnlyList<Album> Value)? _albums;
    private (DateTime At, IReadOnlyList<Person> Value)? _people;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);

    public VirtualTree(IImmichClient client, AppSettings settings, string meId)
    {
        _client = client;
        _settings = settings;
        _meId = meId;
    }

    public AppSettings Settings => _settings;

    public void InvalidateCaches()
    {
        _buckets = null;
        _albums = null;
        _people = null;
    }

    public static string[] Split(string relPath) =>
        relPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);

    // ------------------------------------------------------------------ listing

    public async Task<IReadOnlyList<TreeEntry>> ListAsync(string relPath, CancellationToken ct = default)
    {
        var parts = Split(relPath);
        if (parts.Length == 0) return RootEntries();

        var v = _settings.Views;
        switch (parts[0])
        {
            case RecentFolder when v.Recent && parts.Length == 1:
                return await AssetsToEntriesAsync(Query() with { Limit = Math.Clamp(v.RecentCount, 1, 10000) }, false, ct);

            case FavoritesFolder when v.Favorites && parts.Length == 1:
                return await AssetsToEntriesAsync(Query() with { FavoritesOnly = true }, false, ct);

            case AllPhotosFolder when v.AllPhotos && parts.Length == 1:
                return await AssetsToEntriesAsync(Query(), false, ct);

            case TimelineFolder when v.Timeline:
                return await ListTimelineAsync(parts, ct);

            case AlbumsFolder when v.Albums:
                return await ListAlbumsAsync(parts, ct);

            case PeopleFolder when v.People:
                return await ListPeopleAsync(parts, ct);
        }
        return Array.Empty<TreeEntry>();
    }

    private IReadOnlyList<TreeEntry> RootEntries()
    {
        var v = _settings.Views;
        var list = new List<TreeEntry>();
        if (v.Recent) list.Add(TreeEntry.Dir(RecentFolder));
        if (v.Timeline) list.Add(TreeEntry.Dir(TimelineFolder));
        if (v.Albums) list.Add(TreeEntry.Dir(AlbumsFolder));
        if (v.People) list.Add(TreeEntry.Dir(PeopleFolder));
        if (v.Favorites) list.Add(TreeEntry.Dir(FavoritesFolder));
        if (v.AllPhotos) list.Add(TreeEntry.Dir(AllPhotosFolder));
        return list;
    }

    private AssetQuery Query() => new()
    {
        IncludeVideos = _settings.IncludeVideos,
        IncludeArchived = _settings.IncludeArchived,
    };

    private async Task<IReadOnlyList<TreeEntry>> ListTimelineAsync(string[] parts, CancellationToken ct)
    {
        var buckets = await GetBucketsAsync(ct);
        if (parts.Length == 1)
        {
            return buckets.Select(b => b.Year).Distinct().OrderByDescending(y => y)
                .Select(y => TreeEntry.Dir(y.ToString(CultureInfo.InvariantCulture), new DateTimeOffset(y, 1, 1, 0, 0, 0, TimeSpan.Zero)))
                .ToList();
        }
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var year)) return Array.Empty<TreeEntry>();
        if (parts.Length == 2)
        {
            return buckets.Where(b => b.Year == year && b.Count > 0).OrderBy(b => b.Month)
                .Select(b => TreeEntry.Dir(MonthFolderName(b.Month), new DateTimeOffset(year, b.Month, 1, 0, 0, 0, TimeSpan.Zero)))
                .ToList();
        }
        if (parts.Length == 3 && TryParseMonthFolder(parts[2], out var month))
        {
            var start = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero);
            // fileCreatedAt is UTC while folders follow local time, so widen by a day and filter below.
            var query = Query() with { TakenFrom = start.AddDays(-1), TakenBefore = start.AddMonths(1).AddDays(1) };
            return await AssetsToEntriesAsync(query, false, ct, a => a.LocalDateTime.Year == year && a.LocalDateTime.Month == month);
        }
        return Array.Empty<TreeEntry>();
    }

    private async Task<IReadOnlyList<TreeEntry>> ListAlbumsAsync(string[] parts, CancellationToken ct)
    {
        var albums = await NamedAlbumsAsync(ct);
        if (parts.Length == 1)
            return albums.Select(a => TreeEntry.Dir(a.Name, a.Album.UpdatedAt)).ToList();
        if (parts.Length == 2)
        {
            var match = albums.FirstOrDefault(a => string.Equals(a.Name, parts[1], StringComparison.OrdinalIgnoreCase));
            if (match.Album == null) return Array.Empty<TreeEntry>();
            return await AssetsToEntriesAsync(Query() with { AlbumId = match.Album.Id, IncludeArchived = true }, true, ct);
        }
        return Array.Empty<TreeEntry>();
    }

    private async Task<IReadOnlyList<TreeEntry>> ListPeopleAsync(string[] parts, CancellationToken ct)
    {
        var people = await NamedPeopleAsync(ct);
        if (parts.Length == 1)
            return people.Select(p => TreeEntry.Dir(p.Name)).ToList();
        if (parts.Length == 2)
        {
            var match = people.FirstOrDefault(p => string.Equals(p.Name, parts[1], StringComparison.OrdinalIgnoreCase));
            if (match.Person == null) return Array.Empty<TreeEntry>();
            return await AssetsToEntriesAsync(Query() with { PersonId = match.Person.Id }, false, ct);
        }
        return Array.Empty<TreeEntry>();
    }

    /// <summary>Runs a search and turns the results into uniquely named file entries.</summary>
    private async Task<IReadOnlyList<TreeEntry>> AssetsToEntriesAsync(AssetQuery query, bool keepOtherOwners, CancellationToken ct, Func<Asset, bool>? keep = null)
    {
        var names = new NameAllocator();
        var entries = new List<TreeEntry>();
        await foreach (var page in _client.SearchAsync(query, ct))
        {
            foreach (var asset in page)
            {
                if (asset.Kind == AssetKind.Other || asset.IsTrashed) continue;
                if (!_settings.IncludeVideos && asset.Kind == AssetKind.Video) continue;
                if (!keepOtherOwners && !_settings.IncludePartnerPhotos && _meId.Length > 0 && asset.OwnerId != _meId) continue;
                if (keep != null && !keep(asset)) continue;

                var size = asset.FileSize ?? await SafeSizeAsync(asset.Id, ct);
                if (size == null) continue;

                var name = names.Allocate(DisplayName(asset));
                entries.Add(new TreeEntry(name, false, asset.Id, size.Value, asset.FileCreatedAt, asset.FileCreatedAt));

                if (_settings.ShowLivePhotoVideos && asset.LivePhotoVideoId is { Length: > 0 } videoId)
                {
                    var video = await GetLiveVideoAsync(videoId, ct);
                    var videoSize = video?.FileSize ?? (video != null ? await SafeSizeAsync(video.Id, ct) : null);
                    if (video != null && videoSize != null)
                    {
                        var videoName = _settings.Naming == FileNaming.Original && video.OriginalFileName.Length > 0
                            ? video.OriginalFileName
                            : Path.ChangeExtension(DisplayName(asset), Path.GetExtension(video.OriginalFileName) is { Length: > 0 } e ? e.ToLowerInvariant() : ".mov");
                        entries.Add(new TreeEntry(names.Allocate(videoName), false, video.Id, videoSize.Value, asset.FileCreatedAt, asset.FileCreatedAt));
                    }
                }
            }
        }
        return entries;
    }

    internal string DisplayName(Asset asset)
    {
        if (_settings.Naming == FileNaming.DateTaken)
        {
            var ext = Path.GetExtension(asset.OriginalFileName);
            ext = string.IsNullOrEmpty(ext) ? (asset.Kind == AssetKind.Video ? ".mp4" : ".jpg") : ext.ToLowerInvariant();
            return asset.LocalDateTime.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture) + ext;
        }
        return string.IsNullOrWhiteSpace(asset.OriginalFileName) ? asset.Id : asset.OriginalFileName;
    }

    private async Task<long?> SafeSizeAsync(string id, CancellationToken ct)
    {
        try { return await _client.GetOriginalSizeAsync(id, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Could not get the size of asset {id}", ex);
            return null;
        }
    }

    private async Task<Asset?> GetLiveVideoAsync(string id, CancellationToken ct)
    {
        if (_liveVideos.TryGetValue(id, out var cached)) return cached;
        try
        {
            var video = await _client.GetAssetAsync(id, ct);
            _liveVideos[id] = video;
            return video;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Could not load live photo video {id}", ex);
            return null;
        }
    }

    // ------------------------------------------------------------------ change detection

    /// <summary>
    /// A cheap value that changes when a folder's contents may have changed.
    /// Null means "no cheap check, always re-list".
    /// </summary>
    public async Task<string?> GetSignatureAsync(string relPath, CancellationToken ct = default)
    {
        var parts = Split(relPath);
        if (parts.Length == 3 && parts[0] == TimelineFolder &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var y) && TryParseMonthFolder(parts[2], out var m))
        {
            var b = (await GetBucketsAsync(ct)).FirstOrDefault(x => x.Year == y && x.Month == m);
            return "b:" + (b?.Count ?? 0);
        }
        if (parts.Length == 2 && parts[0] == AlbumsFolder)
        {
            var match = (await NamedAlbumsAsync(ct)).FirstOrDefault(a => string.Equals(a.Name, parts[1], StringComparison.OrdinalIgnoreCase));
            if (match.Album == null) return "a:none";
            return $"a:{match.Album.Id}:{match.Album.AssetCount}:{match.Album.UpdatedAt.UtcTicks}:{match.Album.LastModifiedAssetTimestamp?.UtcTicks}";
        }
        if (parts.Length == 1 && parts[0] == AllPhotosFolder)
            return "t:" + await _client.GetTotalAssetCountAsync(_settings.IncludeArchived, ct);
        return null;
    }

    // ------------------------------------------------------------------ cached lists

    private async Task<IReadOnlyList<TimeBucket>> GetBucketsAsync(CancellationToken ct)
    {
        if (_buckets is { } c && DateTime.UtcNow - c.At < CacheLifetime) return c.Value;
        await _cacheGate.WaitAsync(ct);
        try
        {
            if (_buckets is { } c2 && DateTime.UtcNow - c2.At < CacheLifetime) return c2.Value;
            var value = await _client.GetTimeBucketsAsync(_settings.IncludeArchived, _settings.IncludePartnerPhotos, ct);
            _buckets = (DateTime.UtcNow, value);
            return value;
        }
        finally { _cacheGate.Release(); }
    }

    private async Task<IReadOnlyList<(string Name, Album Album)>> NamedAlbumsAsync(CancellationToken ct)
    {
        IReadOnlyList<Album> albums;
        if (_albums is { } c && DateTime.UtcNow - c.At < CacheLifetime) albums = c.Value;
        else
        {
            albums = await _client.GetAlbumsAsync(ct);
            _albums = (DateTime.UtcNow, albums);
        }
        var names = new NameAllocator();
        return albums
            .Where(a => _settings.Views.IncludeSharedAlbums || a.OwnerId == _meId || _meId.Length == 0)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => (names.Allocate(a.Name, isDirectory: true), a))
            .ToList();
    }

    private async Task<IReadOnlyList<(string Name, Person Person)>> NamedPeopleAsync(CancellationToken ct)
    {
        IReadOnlyList<Person> people;
        if (_people is { } c && DateTime.UtcNow - c.At < CacheLifetime) people = c.Value;
        else
        {
            people = await _client.GetPeopleAsync(ct);
            _people = (DateTime.UtcNow, people);
        }
        var names = new NameAllocator();
        return people
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id, StringComparer.Ordinal)
            .Select(p => (names.Allocate(p.Name, isDirectory: true), p))
            .ToList();
    }

    // ------------------------------------------------------------------ helpers

    public static string MonthFolderName(int month) =>
        $"{month:00} - {CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month)}";

    public static bool TryParseMonthFolder(string name, out int month)
    {
        month = 0;
        return name.Length >= 2 && int.TryParse(name.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out month)
            && month is >= 1 and <= 12 && name == MonthFolderName(month);
    }
}
