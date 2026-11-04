using System.Text.RegularExpressions;

namespace NowPlaying.Core;

public static class YouTubeVideo
{
    public static bool IsValidId(string? id)
        => id is not null && Regex.IsMatch(id, @"\A[A-Za-z0-9_-]{11}\z", RegexOptions.CultureInvariant);

    public static string? IdFromUrl(string value)
    {
        value = value.Trim();
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0) return null;
        string? id = null;
        if (uri.Host is "youtu.be" or "www.youtu.be")
            id = uri.AbsolutePath.Trim('/');
        else if (uri.Host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com" or "www.youtube-nocookie.com")
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 2 && segments[0] is "shorts" or "live" or "embed") id = segments[1];
            else if (uri.AbsolutePath == "/watch")
            {
                var ids = uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
                    .Where(pair => pair.Length == 2 && pair[0] == "v").Select(pair => Uri.UnescapeDataString(pair[1])).Distinct().ToArray();
                if (ids.Length == 1) id = ids[0];
            }
        }
        return IsValidId(id) ? id : null;
    }

    public static string? ThumbnailUrl(string? id) => IsValidId(id) ? $"https://i.ytimg.com/vi/{id}/hqdefault.jpg" : null;
    public static string? WatchUrl(string? id) => IsValidId(id) ? $"https://www.youtube.com/watch?v={id}" : null;
}
