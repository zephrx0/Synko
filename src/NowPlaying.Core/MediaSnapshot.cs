namespace NowPlaying.Core;

public enum MediaSource { Other, AppleMusic, Browser, Spotify }
public enum SourceMode { Automatic, AppleMusic, Browser, AllPlayers, Spotify }
public enum MediaWebsite { Unknown, YouTube }

public sealed record MediaSnapshot(
    string AppId, string SourceName, MediaSource Source, string Title,
    string Artist, string Album, bool IsPlaying, bool IsCurrent,
    TimeSpan Position, TimeSpan Duration, double PlaybackRate, DateTimeOffset ObservedAt, MediaWebsite Website = MediaWebsite.Unknown,
    string? VideoId = null,
    [property: System.Text.Json.Serialization.JsonIgnore] string? ThumbnailData = null)
{
    public string TrackKey => $"{AppId}\n{Title}\n{Artist}\n{Album}";
}

public static class MediaMetadata
{
    public static MediaSnapshot Normalize(MediaSnapshot media)
    {
        // Apple Music for Windows sometimes sends "Artist — Album" as Artist
        // while leaving AlbumTitle empty. Do not split real artist hyphens.
        if (media.Source != MediaSource.AppleMusic || !string.IsNullOrWhiteSpace(media.Album)) return media;
        foreach (var separator in new[] { " — ", " – " })
        {
            var index = media.Artist.LastIndexOf(separator, StringComparison.Ordinal);
            if (index > 0 && index + separator.Length < media.Artist.Length)
                return media with { Artist = media.Artist[..index].Trim(), Album = media.Artist[(index + separator.Length)..].Trim() };
        }
        return media;
    }
}

public static class SourceClassifier
{
    public static (MediaSource Source, string Name) Classify(string appId)
    {
        if (appId.Equals("Spotify", StringComparison.OrdinalIgnoreCase)
            || appId.Replace('\\', '/').Split('/')[^1].Equals("Spotify.exe", StringComparison.OrdinalIgnoreCase)
            || appId.StartsWith("SpotifyAB.SpotifyMusic_", StringComparison.OrdinalIgnoreCase))
            return (MediaSource.Spotify, "Spotify");
        if (appId.Contains("applemusic", StringComparison.OrdinalIgnoreCase)
            || appId.Contains("itunes", StringComparison.OrdinalIgnoreCase))
            return (MediaSource.AppleMusic, "Apple Music");

        (string Id, string Name)[] browsers =
        [
            ("msedge", "Microsoft Edge"), ("chrome", "Google Chrome"),
            ("chromium", "Chromium"), ("thorium", "Thorium"), ("zen.exe", "Zen"),
            ("floorp", "Floorp"), ("librewolf", "LibreWolf"), ("waterfox", "Waterfox"),
            ("firefox", "Firefox"), ("brave", "Brave"), ("vivaldi", "Vivaldi"),
            ("operagx", "Opera GX"), ("opera", "Opera"), ("arc.exe", "Arc"), ("yandex", "Яндекс Браузер"),
            ("browser.exe", "Браузер")
        ];
        foreach (var browser in browsers)
            if (appId.Contains(browser.Id, StringComparison.OrdinalIgnoreCase))
                return (MediaSource.Browser, browser.Name);
        return (MediaSource.Other, appId);
    }
}

public static class MediaSelector
{
    public static bool Allows(MediaSource source, SourceMode mode) => mode switch
    {
        SourceMode.AppleMusic => source == MediaSource.AppleMusic,
        SourceMode.Browser => source == MediaSource.Browser,
        SourceMode.Spotify => source == MediaSource.Spotify,
        SourceMode.AllPlayers => true,
        _ => source is MediaSource.AppleMusic or MediaSource.Browser or MediaSource.Spotify
    };
    public static MediaSnapshot? Select(IReadOnlyList<MediaSnapshot> sessions, SourceMode mode, string? previousTrack)
    {
        var candidates = sessions.Where(s => s.IsPlaying && !string.IsNullOrWhiteSpace(s.Title))
            .Where(s => Allows(s.Source, mode)).ToArray();

        // Windows' current session follows the system media controls. Keep the old
        // choice if Windows does not identify a current, playing supported session.
        return candidates.FirstOrDefault(s => s.IsCurrent)
            ?? candidates.FirstOrDefault(s => s.TrackKey == previousTrack)
            ?? candidates.FirstOrDefault();
    }
}
