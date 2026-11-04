using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NowPlaying.Core;

namespace NowPlaying.Windows;

internal sealed class BrowserWebsiteDetector(bool probeAddress = false)
{
    private readonly Dictionary<string, (MediaWebsite Website, string? VideoId, DateTimeOffset Expires)> _known = new(StringComparer.Ordinal);
    private Task<(string TrackKey, BrowserAddressReader.Result Match)>? _addressLookup;
    private DateTimeOffset _nextAddressRead;
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    public IReadOnlyList<MediaSnapshot> Enrich(IReadOnlyList<MediaSnapshot> sessions, bool readAddress = true)
    {
        if (_addressLookup?.IsCompleted == true)
        {
            if (_addressLookup.IsCompletedSuccessfully)
            {
                var result = _addressLookup.Result;
                if (result.Match.Website != MediaWebsite.Unknown)
                    _known[result.TrackKey] = (result.Match.Website, result.Match.VideoId, DateTimeOffset.UtcNow.AddHours(1));
            }
            else _ = _addressLookup.Exception; // Observe an optional accessibility provider failure.
            _addressLookup = null;
        }
        var browsers = sessions.Where(s => s.Source == MediaSource.Browser && s.IsPlaying).ToArray();
        if (browsers.Length == 0) return sessions;
        var now = DateTimeOffset.UtcNow;
        foreach (var expired in _known.Where(item => item.Value.Expires <= now).Select(item => item.Key).ToArray()) _known.Remove(expired);
        var windows = ReadBrowserWindows(browsers);
        return sessions.Select(media =>
        {
            if (media.Source != MediaSource.Browser || !media.IsPlaying) return media;
            var matchedWindow = windows.FirstOrDefault(w => w.Browser == media.SourceName
                && BrowserSiteClassifier.FromWindowTitle(media.Title, w.Title) == MediaWebsite.YouTube);
            var website = matchedWindow.Handle != IntPtr.Zero ? MediaWebsite.YouTube : MediaWebsite.Unknown;
            var videoId = _known.TryGetValue(media.TrackKey, out var cached) ? cached.VideoId : null;
            if (website != MediaWebsite.Unknown)
            {
                if (_known.Count >= 256) _known.Remove(_known.MinBy(item => item.Value.Expires).Key);
                _known[media.TrackKey] = (website, videoId, now.AddHours(1));
            }
            else if (_known.TryGetValue(media.TrackKey, out var known))
            {
                website = known.Website;
                _known[media.TrackKey] = (known.Website, known.VideoId, now.AddHours(1));
            }
            var browserWindow = matchedWindow.Handle != IntPtr.Zero ? matchedWindow.Handle : windows.FirstOrDefault(w => w.Browser == media.SourceName).Handle;
            if (readAddress && browserWindow != IntPtr.Zero && _addressLookup is null && now >= _nextAddressRead && (website == MediaWebsite.Unknown || videoId is null))
            {
                _nextAddressRead = now.AddSeconds(5);
                _addressLookup = Task.Run(() => (media.TrackKey, BrowserAddressReader.Read(browserWindow, media.Title, probeAddress)));
            }
            return media with { Website = website, VideoId = readAddress ? videoId : null };
        }).ToArray();
    }

    public async Task WaitForPendingAsync(CancellationToken cancellationToken)
    {
        if (_addressLookup is null) return;
        try { await _addressLookup.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
        catch (TimeoutException) { }
    }

    internal static string WindowTitle(IntPtr window)
    {
        var title = new StringBuilder(2048);
        return GetWindowText(window, title, title.Capacity) > 0 ? title.ToString() : "";
    }

    private static List<(string Browser, string Title, IntPtr Handle)> ReadBrowserWindows(IReadOnlyList<MediaSnapshot> sessions)
    {
        var windows = new List<(string Browser, string Title, IntPtr Handle)>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            try
            {
                GetWindowThreadProcessId(window, out var pid);
                using var process = Process.GetProcessById((int)pid);
                var source = SourceClassifier.Classify(process.ProcessName + ".exe");
                if (source.Source == MediaSource.Browser) source = (source.Source, BrowserIdentity.ForProcess(process, source.Name));
                if (source.Source != MediaSource.Browser || !sessions.Any(s => s.SourceName == source.Name)) return true;
                var title = WindowTitle(window);
                if (title.Length > 0) windows.Add((source.Name, title, window));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return true;
        }, IntPtr.Zero);
        return windows;
    }
}
