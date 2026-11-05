using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NowPlaying.Core;

public sealed record ArtworkMatch(string ImageUrl, string Album, string? AppleMusicUrl);
public sealed record ArtworkResult(ArtworkMatch? Match, string Status, DateTimeOffset ExpiresAt);

public interface IArtworkResolver
{
    Task<ArtworkResult> ResolveAsync(MediaSnapshot media, CancellationToken cancellationToken);
}

public sealed class AppleArtworkResolver(HttpClient http, TimeProvider? timeProvider = null, TimeSpan? requestInterval = null,
    string? cacheFile = null) : IArtworkResolver
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _interval = requestInterval ?? TimeSpan.FromSeconds(3.2);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ArtworkResult> _cache = new(StringComparer.Ordinal);
    private DateTimeOffset _nextRequest;
    private DateTimeOffset _rateLimitUntil;
    private bool _loaded;

    public async Task<ArtworkResult> ResolveAsync(MediaSnapshot raw, CancellationToken cancellationToken)
    {
        var media = MediaMetadata.Normalize(raw);
        if (media.Source is not (MediaSource.AppleMusic or MediaSource.Spotify))
            return new(null, "Автообложки доступны для Apple Music и Spotify", _time.GetUtcNow().AddMinutes(15));
        var key = $"{Normalize(media.Title)}\n{Normalize(media.Artist)}\n{Normalize(media.Album)}";
        await _gate.WaitAsync(cancellationToken);
        try
        {
            LoadCache();
            if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > _time.GetUtcNow()) return cached;
            var albumKey = string.IsNullOrWhiteSpace(media.Album) ? null : $"album\n{Normalize(media.Artist)}\n{NormalizeAlbum(media.Album)}";
            if (albumKey is not null && _cache.TryGetValue(albumKey, out cached) && cached.ExpiresAt > _time.GetUtcNow())
                return cached;
            if (_rateLimitUntil > _time.GetUtcNow())
                return new(null, "Обложка: лимит запросов Apple; повторю позже", _rateLimitUntil);
            ArtworkResult result;
            try
            {
                result = await FindAsync(media, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
            {
                result = new(null, "Обложка: каталог Apple временно недоступен; повторю поиск", _time.GetUtcNow().AddSeconds(10));
            }
            if (_cache.Count >= 256)
            {
                var oldest = _cache.MinBy(item => item.Value.ExpiresAt).Key;
                _cache.Remove(oldest);
            }
            _cache[key] = result;
            if (result.Match is { } match)
            {
                // Reuse only the same artist AND album. A song URL from the
                // first track must not become the button for another song.
                if (albumKey is not null && NormalizeAlbum(match.Album) == NormalizeAlbum(media.Album))
                    _cache[albumKey] = result with { Match = match with { AppleMusicUrl = null } };
                SaveCache();
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<ArtworkResult> FindAsync(MediaSnapshot media, CancellationToken cancellationToken)
    {
        if (media.Source is not (MediaSource.AppleMusic or MediaSource.Spotify) || string.IsNullOrWhiteSpace(media.Title) || string.IsNullOrWhiteSpace(media.Artist))
            return new(null, "Обложка: нет данных для поиска", _time.GetUtcNow().AddMinutes(15));

        // Album search also works when a particular song is absent from the
        // public store but another song from the same album is still indexed.
        var terms = new[] { media.Album, media.Title }.Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(term => media.Artist + " " + term).Distinct().ToArray();
        var countries = CultureInfo.CurrentCulture.Name.EndsWith("-RU", StringComparison.OrdinalIgnoreCase)
            || ContainsCyrillic(media.Artist) ? new[] { "ru", "us" } : new[] { "us", "ru" };
        var failed = false;
        foreach (var country in countries)
        foreach (var term in terms)
        {
            var wait = _nextRequest - _time.GetUtcNow();
            if (wait > TimeSpan.Zero) await Task.Delay(wait, _time, cancellationToken);
            _nextRequest = _time.GetUtcNow() + _interval;
            var uri = new Uri("https://itunes.apple.com/search?media=music&entity=song&limit=50&country="
                + country + "&term=" + Uri.EscapeDataString(term));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retry = response.Headers.RetryAfter;
                    _rateLimitUntil = retry?.Date ?? _time.GetUtcNow() + (retry?.Delta ?? TimeSpan.FromSeconds(30));
                    if (_rateLimitUntil < _time.GetUtcNow().AddSeconds(3)) _rateLimitUntil = _time.GetUtcNow().AddSeconds(3);
                    return new(null, "Обложка: лимит запросов Apple; повторю позже", _rateLimitUntil);
                }
                response.EnsureSuccessStatusCode();
                await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, timeout.Token);
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
                var match = MatchResults(media, json.RootElement);
                if (match is not null)
                    return new(match, "Обложка альбома найдена в Apple Music", _time.GetUtcNow().AddDays(7));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
            {
                // Try the next query/storefront instead of suppressing the
                // entire track for minutes after one transient response.
                failed = true;
            }
        }
        return new(null, failed ? "Обложка: каталог Apple временно недоступен; повторю поиск"
            : "Обложка не найдена в каталоге Apple", _time.GetUtcNow().AddSeconds(failed ? 10 : 60));
    }

    private void LoadCache()
    {
        if (_loaded) return;
        _loaded = true;
        if (cacheFile is null) return;
        try
        {
            if (!File.Exists(cacheFile) || new FileInfo(cacheFile).Length > 1024 * 1024) return;
            var entries = JsonSerializer.Deserialize<Dictionary<string, ArtworkResult>>(File.ReadAllText(cacheFile));
            if (entries is null) return;
            foreach (var entry in entries.Where(e => e.Value is not null && e.Value.ExpiresAt > _time.GetUtcNow()
                && e.Value.Match is { } match && ArtworkUrl(match.ImageUrl) is not null).Take(256))
                _cache[entry.Key] = entry.Value with { Match = entry.Value.Match! with { AppleMusicUrl = StoreUrl(entry.Value.Match.AppleMusicUrl ?? "") } };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }

    private void SaveCache()
    {
        while (_cache.Count > 256) _cache.Remove(_cache.MinBy(item => item.Value.ExpiresAt).Key);
        if (cacheFile is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cacheFile))!);
            var entries = _cache.Where(e => e.Value.Match is not null && e.Value.ExpiresAt > _time.GetUtcNow())
                .OrderByDescending(e => e.Value.ExpiresAt).Take(256).ToDictionary();
            File.WriteAllText(cacheFile + ".tmp", JsonSerializer.Serialize(entries));
            File.Move(cacheFile + ".tmp", cacheFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    public static ArtworkMatch? MatchResults(MediaSnapshot raw, JsonElement response)
    {
        var media = MediaMetadata.Normalize(raw);
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;
        var title = Normalize(media.Title);
        var album = NormalizeAlbum(media.Album);
        var artist = Normalize(media.Artist);
        if (title.Length == 0 || artist.Length == 0) return null;
        ArtworkMatch? best = null;
        var bestScore = -1;
        foreach (var candidate in results.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.Object) continue;
            var candidateArtist = Normalize(String(candidate, "artistName"));
            // Accept a shared named artist from a collaboration, never just the
            // same title or a partial substring such as "Art" versus "Artist".
            if (!ArtistsMatch(media.Artist, String(candidate, "artistName"))) continue;
            var candidateTitle = Normalize(String(candidate, "trackName"));
            var candidateAlbum = String(candidate, "collectionName");
            var sameAlbum = album.Length > 0 && NormalizeAlbum(candidateAlbum) == album;
            var sameTitle = candidateTitle == title;
            if (!sameAlbum && !sameTitle) continue;
            if (sameTitle && !sameAlbum && media.Duration > TimeSpan.Zero
                && candidate.TryGetProperty("trackTimeMillis", out var duration) && duration.TryGetDouble(out var milliseconds)
                && Math.Abs(milliseconds / 1000 - media.Duration.TotalSeconds) > 8) continue;
            var imageUrl = ArtworkUrl(String(candidate, "artworkUrl100"));
            if (imageUrl is null) continue;
            var score = (sameTitle ? 4 : 0) + (sameAlbum ? 8 : 0) + (candidateArtist == artist ? 2 : 0);
            if (score <= bestScore) continue;
            bestScore = score;
            var link = String(candidate, sameTitle ? "trackViewUrl" : "collectionViewUrl");
            best = new(imageUrl, candidateAlbum, StoreUrl(link));
        }
        return best;
    }

    private static string String(JsonElement value, string key)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? "" : "";

    public static string Normalize(string value)
    {
        var result = new StringBuilder();
        var space = false;
        foreach (var rune in value.ToLowerInvariant().Replace('ё', 'е').Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                if (space && result.Length > 0) result.Append(' ');
                result.Append(rune.ToString());
                space = false;
            }
            else space = true;
        }
        return result.ToString();
    }

    private static string NormalizeAlbum(string value)
        => Normalize(Regex.Replace(value, @"\s[-–—]\s(?:Single|EP)$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    private static bool ArtistsMatch(string expected, string actual)
    {
        var normalized = Normalize(expected);
        if (normalized.Length > 0 && normalized == Normalize(actual)) return true;
        const string separator = @"\s*(?:&|,|;|\bfeat\.?\s|\bfeaturing\s|\bft\.\s)\s*";
        var first = Regex.Split(expected, separator, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(Normalize).Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);
        return Regex.Split(actual, separator, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(Normalize).Any(s => s.Length > 0 && first.Contains(s));
    }

    private static string? ArtworkUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !(uri.Host == "mzstatic.com" || uri.Host.EndsWith(".mzstatic.com", StringComparison.OrdinalIgnoreCase))) return null;
        return Regex.Replace(uri.AbsoluteUri, @"/100x100bb(?=\.)", "/512x512bb", RegexOptions.CultureInvariant);
    }

    private static string? StoreUrl(string value)
    {
        value = value.Replace("\\u0026", "&", StringComparison.Ordinal);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https"
            && uri.Host is "music.apple.com" or "itunes.apple.com" ? uri.AbsoluteUri : null;
    }

    private static bool ContainsCyrillic(string text) => text.Any(c => c is >= '\u0400' and <= '\u04ff');
}
