using System.Text.Json;

namespace NowPlaying.Core;

// Public catalogue metadata only. No account API and no image uploads.
public sealed class DeezerArtworkResolver(HttpClient http, string? cacheFile = null, TimeSpan? requestInterval = null) : IArtworkResolver
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ArtworkResult> _cache = new(StringComparer.Ordinal);
    private DateTimeOffset _nextRequest;
    private DateTimeOffset _retryAfter;
    private bool _loaded;

    public async Task<ArtworkResult> ResolveAsync(MediaSnapshot raw, CancellationToken cancellationToken)
    {
        var media = MediaMetadata.Normalize(raw);
        if (media.Source is not (MediaSource.AppleMusic or MediaSource.Spotify) || string.IsNullOrWhiteSpace(media.Artist)) return Miss();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            LoadCache();
            var key = AppleArtworkResolver.Normalize(media.Artist) + "\n" + AppleArtworkResolver.Normalize(media.Title) + "\n" + AppleArtworkResolver.Normalize(media.Album);
            var albumKey = "album\n" + AppleArtworkResolver.Normalize(media.Artist) + "\n" + AppleArtworkResolver.Normalize(media.Album);
            if (_cache.TryGetValue(key, out var hit) && hit.ExpiresAt > DateTimeOffset.UtcNow) return hit;
            if (!string.IsNullOrWhiteSpace(media.Album) && _cache.TryGetValue(albumKey, out hit) && hit.ExpiresAt > DateTimeOffset.UtcNow) return hit;
            if (_retryAfter > DateTimeOffset.UtcNow) return new(null, "Каталог временно недоступен", _retryAfter);
            var queries = new List<(string Path, string Term)>();
            if (!string.IsNullOrWhiteSpace(media.Album)) queries.Add(("/album", media.Artist + " " + media.Album));
            queries.Add(("", media.Artist + " " + media.Title));
            foreach (var (path, term) in queries)
            {
                var wait = _nextRequest - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
                _nextRequest = DateTimeOffset.UtcNow + (requestInterval ?? TimeSpan.FromSeconds(1));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                using var response = await http.GetAsync("https://api.deezer.com/search" + path + "?limit=25&q=" + Uri.EscapeDataString(term), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if ((int)response.StatusCode == 429)
                {
                    _retryAfter = response.Headers.RetryAfter?.Date ?? DateTimeOffset.UtcNow + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(15));
                    return new(null, "Каталог временно недоступен", _retryAfter);
                }
                response.EnsureSuccessStatusCode();
                await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, timeout.Token);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out _))
                {
                    _retryAfter = DateTimeOffset.UtcNow.AddSeconds(15);
                    return new(null, "Каталог временно недоступен", _retryAfter);
                }
                var match = MatchResults(media, json.RootElement, path == "/album");
                if (match is null) continue;
                var result = new ArtworkResult(match, "Обложка альбома найдена", DateTimeOffset.UtcNow.AddDays(7));
                _cache[key] = result;
                if (!string.IsNullOrWhiteSpace(media.Album) && AppleArtworkResolver.Normalize(media.Album) == AppleArtworkResolver.Normalize(match.Album)) _cache[albumKey] = result;
                SaveCache();
                return result;
            }
            var miss = Miss();
            _cache[key] = miss;
            TrimCache();
            return miss;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
        { return new(null, "Каталог временно недоступен; повторю поиск", DateTimeOffset.UtcNow.AddSeconds(10)); }
        finally { _gate.Release(); }
    }

    public static ArtworkMatch? MatchResults(MediaSnapshot media, JsonElement response, bool albums)
    {
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("data", out var entries) || entries.ValueKind != JsonValueKind.Array) return null;
        var candidates = new List<object>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("artist", out var artist) || artist.ValueKind != JsonValueKind.Object) continue;
            var album = albums ? entry : entry.TryGetProperty("album", out var record) ? record : default;
            if (album.ValueKind != JsonValueKind.Object) continue;
            double duration = !albums && entry.TryGetProperty("duration", out var time) && time.ValueKind == JsonValueKind.Number && time.TryGetDouble(out var seconds) ? seconds * 1000 : 0;
            candidates.Add(new { artistName = Text(artist, "name"), trackName = albums ? "" : Text(entry, "title"), collectionName = Text(album, "title"), artworkUrl100 = Text(album, "cover_big"), trackTimeMillis = duration });
        }
        return AppleArtworkResolver.MatchResults(media, JsonSerializer.SerializeToElement(new { results = candidates }), CoverUrl, _ => null);
    }

    public static string? CoverUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0
        && (uri.Host == "cdn-images.dzcdn.net" || uri.Host == "e-cdn-images.dzcdn.net") && uri.AbsolutePath.StartsWith("/images/cover/", StringComparison.Ordinal) ? uri.AbsoluteUri : null;
    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" : "";
    private static ArtworkResult Miss() => new(null, "Обложка пока не найдена", DateTimeOffset.UtcNow.AddSeconds(15));
    private void TrimCache() { while (_cache.Count > 128) _cache.Remove(_cache.MinBy(p => p.Value.ExpiresAt).Key); }

    private void LoadCache()
    {
        if (_loaded) return;
        _loaded = true;
        if (cacheFile is null) return;
        try
        {
            if (!File.Exists(cacheFile) || new FileInfo(cacheFile).Length > 512 * 1024) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, ArtworkResult>>(File.ReadAllText(cacheFile));
            if (data is null) return;
            foreach (var entry in data.Where(p => p.Value is { Match: { } match } && CoverUrl(match.ImageUrl) is not null && p.Value.ExpiresAt > DateTimeOffset.UtcNow).Take(128))
                _cache[entry.Key] = entry.Value with { Match = entry.Value.Match! with { AppleMusicUrl = null } };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }

    private void SaveCache()
    {
        TrimCache();
        if (cacheFile is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cacheFile))!);
            File.WriteAllText(cacheFile + ".tmp", JsonSerializer.Serialize(_cache.Where(p => p.Value.Match is not null && p.Value.ExpiresAt > DateTimeOffset.UtcNow).ToDictionary()));
            File.Move(cacheFile + ".tmp", cacheFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
}
