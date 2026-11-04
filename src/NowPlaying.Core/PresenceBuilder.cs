using System.Text;

namespace NowPlaying.Core;

public sealed record Presence(string Details, string State, int Type, long? Start, long? End, string? ImageKey, string ImageText, string? AppleMusicUrl = null,
    string? ActivityName = null, string? YouTubeUrl = null)
{
    public object ToActivity()
    {
        var activity = new Dictionary<string, object>
        {
            ["type"] = Type,
            ["details"] = Details,
            ["state"] = State,
            ["instance"] = false
        };
        if (!string.IsNullOrWhiteSpace(ActivityName)) activity["name"] = ActivityName;
        if (Start.HasValue)
        {
            var timestamps = new Dictionary<string, long> { ["start"] = Start.Value };
            if (End.HasValue) timestamps["end"] = End.Value;
            activity["timestamps"] = timestamps;
        }
        if (!string.IsNullOrWhiteSpace(ImageKey))
            activity["assets"] = new { large_image = ImageKey, large_text = ImageText };
        if (!string.IsNullOrWhiteSpace(YouTubeUrl))
            activity["buttons"] = new[] { new { label = "Открыть на YouTube", url = YouTubeUrl } };
        else if (!string.IsNullOrWhiteSpace(AppleMusicUrl))
            activity["buttons"] = new[] { new { label = "Открыть в Apple Music", url = AppleMusicUrl } };
        return activity;
    }
}

public static class PresenceBuilder
{
    public static Presence Build(MediaSnapshot media, string? imageKey, ArtworkMatch? artwork = null)
    {
        long? start = null;
        long? end = null;
        // Discord's timeline advances at normal speed; omit it for live media or
        // altered playback speed rather than display an incorrect countdown.
        if (media.Duration > TimeSpan.Zero && Math.Abs(media.PlaybackRate - 1) < 0.01)
        {
            var position = Math.Clamp(media.Position.TotalSeconds, 0, media.Duration.TotalSeconds);
            start = media.ObservedAt.AddSeconds(-position).ToUnixTimeSeconds();
            end = media.ObservedAt.AddSeconds(media.Duration.TotalSeconds - position).ToUnixTimeSeconds();
        }
        var state = string.IsNullOrWhiteSpace(media.Artist) ? media.SourceName : media.Artist;
        if (!string.IsNullOrWhiteSpace(media.Album)) state += " · " + media.Album;
        // A browser session does not reliably identify audio versus video. A
        // neutral Watching activity avoids claiming every browser tab is music.
        var name = media.Source switch
        {
            MediaSource.AppleMusic => "Apple Music",
            MediaSource.Browser when media.Website == MediaWebsite.YouTube => "видео на YouTube",
            MediaSource.Browser => "видео в браузере",
            _ => media.SourceName
        };
        var youtubeImage = media.Source == MediaSource.Browser && media.Website == MediaWebsite.YouTube
            ? YouTubeVideo.ThumbnailUrl(media.VideoId) : null;
        return new(Clip(media.Title), Clip(state), media.Source == MediaSource.Browser ? 3 : 2,
            start, end, youtubeImage ?? artwork?.ImageUrl ?? (string.IsNullOrWhiteSpace(imageKey) ? null : imageKey.Trim()),
            Clip(youtubeImage is not null ? media.Title : artwork is null ? media.SourceName : artwork.Album),
            artwork?.AppleMusicUrl, Clip(name), youtubeImage is not null ? YouTubeVideo.WatchUrl(media.VideoId) : null);
    }

    // Discord caps details/state at 128 UTF-8 bytes. Iterate Unicode scalars so
    // Cyrillic, emoji and surrogate pairs are never split by truncation.
    public static string Clip(string text, int maxBytes = 128)
    {
        var result = new StringBuilder();
        var size = 0;
        foreach (var rune in text.Trim().EnumerateRunes())
        {
            if (size + rune.Utf8SequenceLength > maxBytes) break;
            result.Append(rune.ToString());
            size += rune.Utf8SequenceLength;
        }
        return result.ToString();
    }
}
