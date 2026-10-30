using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ImmichTray.Immich;

public interface IImmichClient
{
    Task<bool> PingAsync(CancellationToken ct = default);
    Task<ServerAbout> GetAboutAsync(CancellationToken ct = default);
    Task<UserInfo> GetMeAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TimeBucket>> GetTimeBucketsAsync(bool includeArchived, bool withPartners, CancellationToken ct = default);
    IAsyncEnumerable<IReadOnlyList<Asset>> SearchAsync(AssetQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Person>> GetPeopleAsync(CancellationToken ct = default);
    Task<Asset> GetAssetAsync(string id, CancellationToken ct = default);
    Task<long> GetTotalAssetCountAsync(bool includeArchived, CancellationToken ct = default);
    /// <summary>
    /// Assets whose updatedAt is after <paramref name="since"/> (uploads, edits, trash and restore all
    /// bump it). With no <paramref name="since"/> it only reads the server's clock.
    /// </summary>
    Task<ChangeBatch> GetChangesSinceAsync(DateTimeOffset? since, CancellationToken ct = default);
    /// <summary>Opens the original file starting at <paramref name="offset"/>. Caller disposes.</summary>
    Task<Stream> OpenOriginalAsync(string id, long offset, long? endExclusive, CancellationToken ct = default);
    Task<long?> GetOriginalSizeAsync(string id, CancellationToken ct = default);
    Task<byte[]> GetThumbnailAsync(string id, bool large, CancellationToken ct = default);
}

/// <summary>Talks to the Immich REST API (v3.2 or newer) with an API key.</summary>
public sealed class ImmichClient : IImmichClient, IDisposable
{
    public const int PageSize = 1000;

    private readonly HttpClient _http;
    private readonly string _base;

    public ImmichClient(string serverUrl, string apiKey, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _base = Settings.AppSettings.NormalizeUrl(serverUrl) + "/api";
        _http = handler != null ? new HttpClient(handler, disposeHandler: false) : new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 16,
        });
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(100);
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ImmichTray", AppInfo.Version));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string BaseUrl => _base;

    public void Dispose() => _http.Dispose();

    // ---------- simple endpoints ----------

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            using var resp = await _http.GetAsync(_base + "/server/ping", cts.Token).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<ServerAbout> GetAboutAsync(CancellationToken ct = default)
    {
        var doc = await GetJsonAsync("/server/about", ct).ConfigureAwait(false);
        return new ServerAbout(Str(doc, "version") ?? "?");
    }

    public async Task<UserInfo> GetMeAsync(CancellationToken ct = default)
    {
        var doc = await GetJsonAsync("/users/me", ct).ConfigureAwait(false);
        return new UserInfo(Str(doc, "id") ?? "", Str(doc, "name") ?? "", Str(doc, "email") ?? "");
    }

    public async Task<IReadOnlyList<TimeBucket>> GetTimeBucketsAsync(bool includeArchived, bool withPartners, CancellationToken ct = default)
    {
        var merged = new Dictionary<(int, int), int>();
        var visibilities = includeArchived ? new[] { "timeline", "archive" } : new[] { "timeline" };
        foreach (var vis in visibilities)
        {
            // Immich rejects withPartners together with the archive visibility.
            var partners = withPartners && vis == "timeline" ? "&withPartners=true" : "";
            var doc = await GetJsonAsync($"/timeline/buckets?visibility={vis}{partners}", ct).ConfigureAwait(false);
            foreach (var b in doc.EnumerateArray())
            {
                var key = Str(b, "timeBucket");
                if (key == null || key.Length < 7) continue;
                if (!int.TryParse(key.AsSpan(0, 4), out var y) || !int.TryParse(key.AsSpan(5, 2), out var m)) continue;
                var count = b.TryGetProperty("count", out var c) ? c.GetInt32() : 0;
                merged[(y, m)] = merged.GetValueOrDefault((y, m)) + count;
            }
        }
        return merged.Select(kv => new TimeBucket(kv.Key.Item1, kv.Key.Item2, kv.Value))
            .OrderByDescending(b => b.Year).ThenByDescending(b => b.Month).ToList();
    }

    public async Task<IReadOnlyList<Album>> GetAlbumsAsync(CancellationToken ct = default)
    {
        var doc = await GetJsonAsync("/albums", ct).ConfigureAwait(false);
        var list = new List<Album>();
        foreach (var a in doc.EnumerateArray())
        {
            var ownerId = "";
            // Immich v3: "First entry is always the album owner."
            if (a.TryGetProperty("albumUsers", out var users) && users.ValueKind == JsonValueKind.Array &&
                users.GetArrayLength() > 0 && users[0].TryGetProperty("user", out var owner))
            {
                ownerId = Str(owner, "id") ?? "";
            }
            if (ownerId == "" && a.TryGetProperty("ownerId", out var oid)) ownerId = oid.GetString() ?? "";
            list.Add(new Album(
                Str(a, "id") ?? "",
                Str(a, "albumName") ?? "Untitled",
                ownerId,
                a.TryGetProperty("assetCount", out var cnt) ? cnt.GetInt32() : 0,
                Date(a, "updatedAt") ?? DateTimeOffset.MinValue,
                Date(a, "lastModifiedAssetTimestamp")));
        }
        return list;
    }

    public async Task<IReadOnlyList<Person>> GetPeopleAsync(CancellationToken ct = default)
    {
        var list = new List<Person>();
        for (var page = 1; page < 100; page++)
        {
            var doc = await GetJsonAsync($"/people?withHidden=false&size=1000&page={page}", ct).ConfigureAwait(false);
            if (doc.TryGetProperty("people", out var people))
            {
                foreach (var p in people.EnumerateArray())
                {
                    var name = Str(p, "name");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (p.TryGetProperty("isHidden", out var h) && h.ValueKind == JsonValueKind.True) continue;
                    list.Add(new Person(Str(p, "id") ?? "", name.Trim(), Date(p, "updatedAt")));
                }
            }
            if (!(doc.TryGetProperty("hasNextPage", out var next) && next.ValueKind == JsonValueKind.True)) break;
        }
        return list;
    }

    public async Task<Asset> GetAssetAsync(string id, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"/assets/{Uri.EscapeDataString(id)}", ct).ConfigureAwait(false);
        return ParseAsset(doc);
    }

    public async Task<long> GetTotalAssetCountAsync(bool includeArchived, CancellationToken ct = default)
    {
        long total = 0;
        foreach (var vis in includeArchived ? new[] { "timeline", "archive" } : new[] { "timeline" })
        {
            var doc = await GetJsonAsync($"/assets/statistics?visibility={vis}&isTrashed=false", ct).ConfigureAwait(false);
            if (doc.TryGetProperty("total", out var t)) total += t.GetInt64();
        }
        return total;
    }

    // ---------- search ----------

    public async IAsyncEnumerable<IReadOnlyList<Asset>> SearchAsync(AssetQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        string? cursor = null;
        var returned = 0;
        while (true)
        {
            var size = PageSize;
            if (query.Limit is int limit) size = Math.Clamp(limit - returned, 1, PageSize);

            var body = new JsonObject
            {
                ["filter"] = BuildFilter(query),
                ["size"] = size,
                ["withExif"] = true,
                ["orderBy"] = new JsonObject { ["field"] = "fileCreatedAt", ["direction"] = "desc" },
            };
            if (cursor != null) body["cursor"] = cursor;

            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            var doc = await SendJsonAsync(HttpMethod.Post, "/search/metadata", content, ct).ConfigureAwait(false);
            var assetsEl = doc.GetProperty("assets");
            var items = new List<Asset>();
            foreach (var a in assetsEl.GetProperty("items").EnumerateArray())
            {
                var asset = ParseAsset(a);
                if (!asset.IsTrashed) items.Add(asset);
            }
            returned += items.Count;
            yield return items;

            cursor = assetsEl.TryGetProperty("nextCursor", out var nc) && nc.ValueKind == JsonValueKind.String ? nc.GetString() : null;
            if (cursor == null) yield break;
            if (query.Limit is int l && returned >= l) yield break;
        }
    }

    public const int ChangePageSize = 500;

    public async Task<ChangeBatch> GetChangesSinceAsync(DateTimeOffset? since, CancellationToken ct = default)
    {
        HttpRequestMessage req;
        if (since is not { } from)
        {
            req = new HttpRequestMessage(HttpMethod.Get, _base + "/server/ping");
        }
        else
        {
            // No visibility or trash filter on purpose: trashed and archived changes matter too.
            var body = new JsonObject
            {
                ["filter"] = new JsonObject { ["updatedAt"] = new JsonObject { ["gt"] = Iso(from) } },
                ["size"] = ChangePageSize,
                ["withExif"] = false,
            };
            req = new HttpRequestMessage(HttpMethod.Post, _base + "/search/metadata")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }

        using (req)
        using (var resp = await SendRawAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
        {
            var serverTime = resp.Headers.Date;
            if (since == null) return new ChangeBatch(Array.Empty<Asset>(), false, serverTime);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var assetsEl = doc.RootElement.GetProperty("assets");
            var items = assetsEl.GetProperty("items").EnumerateArray().Select(ParseAsset).ToList();
            var more = assetsEl.TryGetProperty("nextCursor", out var nc) && nc.ValueKind == JsonValueKind.String;
            return new ChangeBatch(items, more, serverTime);
        }
    }

    internal static JsonObject BuildFilter(AssetQuery q)
    {
        var filter = new JsonObject
        {
            ["visibility"] = new JsonObject { ["in"] = q.IncludeArchived || q.AlbumId != null ? new JsonArray("timeline", "archive") : new JsonArray("timeline") },
            ["type"] = new JsonObject { ["in"] = q.IncludeVideos ? new JsonArray("IMAGE", "VIDEO") : new JsonArray("IMAGE") },
            // v3 search returns trashed assets unless told not to.
            ["trashedAt"] = new JsonObject { ["eq"] = null },
        };
        if (q.TakenFrom != null || q.TakenBefore != null)
        {
            var taken = new JsonObject();
            if (q.TakenFrom is { } from) taken["gte"] = Iso(from);
            if (q.TakenBefore is { } before) taken["lt"] = Iso(before);
            filter["takenAt"] = taken;
        }
        if (q.AlbumId != null) filter["albumIds"] = new JsonObject { ["any"] = new JsonArray(q.AlbumId) };
        if (q.PersonId != null) filter["personIds"] = new JsonObject { ["any"] = new JsonArray(q.PersonId) };
        if (q.FavoritesOnly) filter["isFavorite"] = new JsonObject { ["eq"] = true };
        return filter;
    }

    // ---------- media ----------

    public async Task<Stream> OpenOriginalAsync(string id, long offset, long? endExclusive, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{_base}/assets/{Uri.EscapeDataString(id)}/original");
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        if (offset > 0 || endExclusive != null)
            req.Headers.Range = new RangeHeaderValue(offset, endExclusive is long e ? e - 1 : null);

        var resp = await SendRawAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        if (offset > 0 && resp.StatusCode == HttpStatusCode.OK)
        {
            // Server ignored the Range header: skip ahead ourselves.
            await SkipAsync(stream, offset, ct).ConfigureAwait(false);
        }
        return new ResponseStream(stream, resp);
    }

    public async Task<long?> GetOriginalSizeAsync(string id, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{_base}/assets/{Uri.EscapeDataString(id)}/original");
        req.Headers.Range = new RangeHeaderValue(0, 0);
        using var resp = await SendRawAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.Content.Headers.ContentRange?.Length is long len) return len;
        if (resp.StatusCode == HttpStatusCode.OK && resp.Content.Headers.ContentLength is long cl) return cl;
        return null;
    }

    public async Task<byte[]> GetThumbnailAsync(string id, bool large, CancellationToken ct = default)
    {
        var size = large ? "preview" : "thumbnail";
        var req = new HttpRequestMessage(HttpMethod.Get, $"{_base}/assets/{Uri.EscapeDataString(id)}/thumbnail?size={size}");
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        using var resp = await SendRawAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    // ---------- plumbing ----------

    private Task<JsonElement> GetJsonAsync(string path, CancellationToken ct) => SendJsonAsync(HttpMethod.Get, path, null, ct);

    private async Task<JsonElement> SendJsonAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, _base + path) { Content = content };
        using var resp = await SendRawAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage req, HttpCompletionOption option, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, option, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ImmichApiException(0, "The Immich server did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new ImmichApiException(0, "Could not reach the Immich server: " + ex.Message);
        }

        if (!resp.IsSuccessStatusCode)
        {
            string detail = "";
            try
            {
                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(text);
                detail = Str(doc.RootElement, "message") ?? text;
            }
            catch { }
            var status = (int)resp.StatusCode;
            resp.Dispose();
            var hint = status switch
            {
                401 => "The API key was rejected.",
                403 => "The API key is missing a permission.",
                404 => "Not found (is this an Immich server, version 3.2 or newer?).",
                _ => $"Immich returned HTTP {status}.",
            };
            throw new ImmichApiException(status, string.IsNullOrWhiteSpace(detail) ? hint : $"{hint} {detail}");
        }
        return resp;
    }

    internal static Asset ParseAsset(JsonElement a)
    {
        long? size = null;
        if (a.TryGetProperty("exifInfo", out var exif) && exif.ValueKind == JsonValueKind.Object &&
            exif.TryGetProperty("fileSizeInByte", out var fs) && fs.ValueKind == JsonValueKind.Number)
            size = fs.GetInt64();

        var type = Str(a, "type");
        return new Asset
        {
            Id = Str(a, "id") ?? "",
            OwnerId = Str(a, "ownerId") ?? "",
            OriginalFileName = Str(a, "originalFileName") ?? "",
            FileSize = size,
            FileCreatedAt = Date(a, "fileCreatedAt") ?? DateTimeOffset.UnixEpoch,
            LocalDateTime = LocalDate(a, "localDateTime") ?? (Date(a, "fileCreatedAt") ?? DateTimeOffset.UnixEpoch).UtcDateTime,
            UpdatedAt = Date(a, "updatedAt") ?? DateTimeOffset.UnixEpoch,
            Kind = type == "IMAGE" ? AssetKind.Image : type == "VIDEO" ? AssetKind.Video : AssetKind.Other,
            IsFavorite = a.TryGetProperty("isFavorite", out var fav) && fav.ValueKind == JsonValueKind.True,
            IsTrashed = a.TryGetProperty("isTrashed", out var tr) && tr.ValueKind == JsonValueKind.True,
            Visibility = Str(a, "visibility") ?? "timeline",
            LivePhotoVideoId = Str(a, "livePhotoVideoId"),
            Checksum = Str(a, "checksum"),
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Date(JsonElement e, string name)
    {
        var s = Str(e, name);
        return s != null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }

    /// <summary>Immich sends localDateTime with a Z suffix, but the clock value is local wall time.</summary>
    private static DateTime? LocalDate(JsonElement e, string name)
    {
        var d = Date(e, name);
        return d == null ? null : DateTime.SpecifyKind(d.Value.UtcDateTime, DateTimeKind.Unspecified);
    }

    private static string Iso(DateTimeOffset d) => d.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static async Task SkipAsync(Stream s, long count, CancellationToken ct)
    {
        var buf = new byte[81920];
        while (count > 0)
        {
            var n = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, count)), ct).ConfigureAwait(false);
            if (n == 0) break;
            count -= n;
        }
    }

    /// <summary>Stream that also disposes the HTTP response it came from.</summary>
    private sealed class ResponseStream(Stream inner, HttpResponseMessage resp) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); resp.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
