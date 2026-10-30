using ImmichTray.Immich;
using ImmichTray.Views;

namespace ImmichTray.CloudFiles;

/// <summary>What a quick check found. <see cref="Everything"/> means too much changed to say where.</summary>
internal sealed record ChangeSet(bool Everything, IReadOnlySet<string> Folders, bool Albums, int AssetCount, bool AlbumListChanged = false)
{
    /// <summary>True if this folder (relative to the Immich folder) should be listed again.</summary>
    public bool Affects(string rel) =>
        Everything || Folders.Contains(rel) ||
        (Albums && rel.StartsWith(VirtualTree.AlbumsFolder + "\\", StringComparison.OrdinalIgnoreCase));

    public ChangeSet Merge(ChangeSet? other)
    {
        if (other == null) return this;
        var folders = new HashSet<string>(Folders, StringComparer.OrdinalIgnoreCase);
        folders.UnionWith(other.Folders);
        return new ChangeSet(Everything || other.Everything, folders, Albums || other.Albums, AssetCount + other.AssetCount,
            AlbumListChanged || other.AlbumListChanged);
    }
}

/// <summary>
/// Asks Immich "what changed since last time?" with one small search (assets by updatedAt),
/// plus the album list. Uploads, edits, favorites, trash and restore all bump an asset's
/// updatedAt, so this catches them without the all-access key the live socket needs.
/// </summary>
internal sealed class ChangeTracker
{
    // Start a little in the past so nothing slips between the last full refresh and the first check.
    private static readonly TimeSpan StartSlack = TimeSpan.FromSeconds(10);

    private DateTimeOffset? _watermark;
    // Immich stores microseconds but sends milliseconds, so an asset can match "after X" again.
    // Remember what was already reported at the edge of the watermark.
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private string? _albumSignature;

    public DateTimeOffset? Watermark => _watermark;

    public void Reset()
    {
        _watermark = null;
        _seen.Clear();
        _albumSignature = null;
    }

    /// <summary>Returns what changed since the previous call, or null if nothing did.</summary>
    public async Task<ChangeSet?> CheckAsync(IImmichClient client, bool watchAlbums, CancellationToken ct)
    {
        var albumsChanged = false;
        if (watchAlbums)
        {
            var albums = await client.GetAlbumsAsync(ct);
            var signature = string.Join(";", albums.OrderBy(a => a.Id, StringComparer.Ordinal)
                .Select(a => $"{a.Id}:{a.Name}:{a.AssetCount}:{a.UpdatedAt.UtcTicks}:{a.LastModifiedAssetTimestamp?.UtcTicks}"));
            albumsChanged = _albumSignature != null && signature != _albumSignature;
            _albumSignature = signature;
        }
        else
        {
            _albumSignature = null;
        }

        if (_watermark is not { } since)
        {
            var clock = await client.GetChangesSinceAsync(null, ct);
            _watermark = (clock.ServerTime ?? DateTimeOffset.UtcNow) - StartSlack;
            return null;
        }

        var batch = await client.GetChangesSinceAsync(since, ct);
        var fresh = batch.Assets.Where(a => !(_seen.TryGetValue(a.Id, out var t) && t == a.UpdatedAt)).ToList();
        foreach (var a in fresh) _seen[a.Id] = a.UpdatedAt;

        if (batch.More)
        {
            // Results come back in date-taken order, so the newest change isn't known. Start over from now.
            _watermark = (batch.ServerTime ?? DateTimeOffset.UtcNow) - StartSlack;
        }
        else if (batch.Assets.Count > 0)
        {
            var newest = batch.Assets.Max(a => a.UpdatedAt);
            if (newest > since) _watermark = newest;
        }
        var keepAfter = _watermark!.Value - TimeSpan.FromSeconds(1);
        foreach (var old in _seen.Where(kv => kv.Value < keepAfter).Select(kv => kv.Key).ToList())
            _seen.Remove(old);

        if (fresh.Count == 0 && !batch.More && !albumsChanged) return null;

        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in fresh) folders.Add(VirtualTree.MonthFolderPath(a.LocalDateTime));
        if (fresh.Count > 0) folders.Add(VirtualTree.AllPhotosFolder);
        // Trashing or restoring a photo doesn't always change an album's count or date, so look at albums too.
        return new ChangeSet(batch.More, folders, albumsChanged || fresh.Count > 0, fresh.Count, albumsChanged);
    }
}
