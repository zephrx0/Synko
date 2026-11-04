using System.Text.RegularExpressions;

namespace NowPlaying.Core;

public static class BrowserSiteClassifier
{
    private static readonly string[] Separators = [" - ", " – ", " — "];
    private static readonly string[] BrowserNames =
        ["Google Chrome", "Microsoft Edge", "Mozilla Firefox", "Firefox", "Brave", "Opera", "Opera GX", "Vivaldi", "Яндекс Браузер", "Yandex Browser", "Arc"];

    public static MediaWebsite FromWindowTitle(string mediaTitle, string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(mediaTitle) || string.IsNullOrWhiteSpace(windowTitle)) return MediaWebsite.Unknown;
        var title = windowTitle.Trim();
        foreach (var status in new[] { ": воспроизводится аудио", ": воспроизводится звук", ": playing audio", ": audio playing", ", playing audio" })
            if (title.EndsWith(status, StringComparison.OrdinalIgnoreCase)) title = title[..^status.Length].Trim();
        foreach (var browser in BrowserNames)
        foreach (var separator in Separators)
        {
            var suffix = separator + browser;
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                title = title[..^suffix.Length].Trim();
        }
        foreach (var separator in Separators)
        {
            var suffix = separator + "YouTube";
            if (!title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var videoTitle = title[..^suffix.Length].Trim();
            // YouTube can prefix the document title with an unread count.
            // Require the same media title so an unrelated active tab cannot
            // relabel a different site that is playing in the background.
            if (SameTitle(mediaTitle, videoTitle)
                || SameTitle(mediaTitle, Regex.Replace(videoTitle, @"^(?:\(\d+\)\s*)+", "")))
                return MediaWebsite.YouTube;
        }
        return MediaWebsite.Unknown;
    }

    private static bool SameTitle(string first, string second)
        => string.Equals(first.Trim().Normalize(), second.Trim().Normalize(), StringComparison.OrdinalIgnoreCase);
}
