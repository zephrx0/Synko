using System.Net;
using NowPlaying.Core;

namespace NowPlaying.Windows.SynkoUi;

internal sealed record UiMedia(string Key, string Title, string Artist, string Album, string Source, string SourceLabel,
    bool Playing, double Position, double Duration, double Rate, long ObservedAt, string? Cover, string? Browser = null);
internal sealed record UiSource(string Id, string Name, bool Playing, string? Browser = null);
internal sealed record UiSnapshot(bool Enabled, UiMedia? Media, IReadOnlyList<UiSource> Sources, object? Preferences = null, Updates.UiUpdateState? Update = null);

internal static class PreviewSnapshot
{
    public static async Task<UiSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var settings = SettingsStore.Load();
        using var monitor = new MediaMonitor();
        var sessions = await monitor.ReadAsync(cancellationToken, waitForBrowserAddress: true, readYouTubeAddress: settings.YouTubeThumbnails);
        var media = MediaSelector.Select(sessions, settings.SourceMode, null)
            ?? sessions.FirstOrDefault(s => MediaSelector.Allows(s.Source, settings.SourceMode) && !string.IsNullOrWhiteSpace(s.Title));
        using var http = new HttpClient();
        string? imageUrl = null;
        if (media?.Source is MediaSource.AppleMusic or MediaSource.Spotify && settings.AutomaticArtwork)
            imageUrl = (await new MusicArtworkResolver(new AppleArtworkResolver(http, cacheFile: Path.Combine(SettingsStore.Folder, "artwork", "catalog.json")),
                new DeezerArtworkResolver(http, Path.Combine(SettingsStore.Folder, "artwork", "deezer.json")))
                .ResolveAsync(media, cancellationToken)).Match?.ImageUrl;
        else if (media?.Website == MediaWebsite.YouTube && settings.YouTubeThumbnails)
            imageUrl = YouTubeVideo.ThumbnailUrl(media.VideoId);
        var showArtwork = media?.Source is MediaSource.AppleMusic or MediaSource.Spotify ? settings.AutomaticArtwork : settings.YouTubeThumbnails;
        var cover = showArtwork ? media?.ThumbnailData ?? (imageUrl is null ? null : await new CoverImageLoader(http,
            Path.Combine(SettingsStore.Folder, "artwork", "images")).LoadAsync(imageUrl, cancellationToken)) : null;
        var sources = sessions.Where(s => s.Source is MediaSource.AppleMusic or MediaSource.Browser or MediaSource.Spotify)
            .GroupBy(s => s.Source == MediaSource.AppleMusic ? "apple" : s.Source == MediaSource.Spotify ? "spotify" : "browser:" + s.SourceName)
            .Select(group => new UiSource(group.Key, ShortBrowserName(group.First().SourceName), group.Any(s => s.IsPlaying), group.First().Source == MediaSource.Browser ? group.First().SourceName : null))
            .ToArray();
        UiMedia? selected = media is null ? null : new(media.TrackKey, media.Title, media.Artist, media.Album,
            media.Source == MediaSource.AppleMusic ? "apple" : media.Source == MediaSource.Spotify ? "spotify" : media.Website == MediaWebsite.YouTube ? "youtube" : "browser",
            media.Source == MediaSource.AppleMusic ? "Apple Music" : media.Source == MediaSource.Spotify ? "Spotify" : media.Website == MediaWebsite.YouTube ? "YouTube" : "Браузер",
            media.IsPlaying, media.Position.TotalSeconds, media.Duration.TotalSeconds, media.PlaybackRate,
            media.ObservedAt.ToUnixTimeMilliseconds(), cover, media.Source == MediaSource.Browser ? media.SourceName : null);
        return new(settings.Enabled, selected, sources);
    }

    private static string ShortBrowserName(string name) => name switch
    { "Google Chrome" => "Chrome", "Microsoft Edge" => "Edge", _ => name };

    internal static async Task<string?> LoadCoverAsync(HttpClient http, string? value, CancellationToken cancellationToken)
    {
        if (!IsCoverUrl(value)) return null;
        var uri = new Uri(value!);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType is not { } type || !type.StartsWith("image/", StringComparison.Ordinal)) return null;
            await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, timeout.Token);
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            return "data:" + type + ";base64," + Convert.ToBase64String(bytes);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException) { return null; }
    }

    internal static bool IsCoverUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https"
        && (uri.Host == "i.ytimg.com" || uri.Host.EndsWith(".mzstatic.com", StringComparison.OrdinalIgnoreCase) || DeezerArtworkResolver.CoverUrl(value!) is not null);
}
