namespace ImmichTray.Immich;

public enum AssetKind { Image, Video, Other }

/// <summary>The parts of an Immich asset this app needs.</summary>
public sealed record Asset
{
    public required string Id { get; init; }
    public string OwnerId { get; init; } = "";
    public string OriginalFileName { get; init; } = "";
    /// <summary>Size of the original file, if Immich knows it.</summary>
    public long? FileSize { get; init; }
    /// <summary>Real moment the photo was taken (UTC).</summary>
    public DateTimeOffset FileCreatedAt { get; init; }
    /// <summary>Wall-clock time where the photo was taken. Kind is Unspecified.</summary>
    public DateTime LocalDateTime { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public AssetKind Kind { get; init; }
    public bool IsFavorite { get; init; }
    public bool IsTrashed { get; init; }
    public string Visibility { get; init; } = "timeline";
    public string? LivePhotoVideoId { get; init; }
    public string? Checksum { get; init; }
}

public sealed record Album(string Id, string Name, string OwnerId, int AssetCount, DateTimeOffset UpdatedAt, DateTimeOffset? LastModifiedAssetTimestamp);

public sealed record Person(string Id, string Name, DateTimeOffset? UpdatedAt);

public sealed record TimeBucket(int Year, int Month, int Count);

public sealed record UserInfo(string Id, string Name, string Email);

public sealed record ServerAbout(string Version);

/// <summary>A filter for the Immich v3 search API.</summary>
public sealed record AssetQuery
{
    public bool IncludeVideos { get; init; } = true;
    public bool IncludeArchived { get; init; }
    public DateTimeOffset? TakenFrom { get; init; }
    public DateTimeOffset? TakenBefore { get; init; }
    public string? AlbumId { get; init; }
    public string? PersonId { get; init; }
    public bool FavoritesOnly { get; init; }
    /// <summary>Stop after this many results (null = all).</summary>
    public int? Limit { get; init; }
}

public sealed class ImmichApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
