using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NowPlaying.Core;

namespace NowPlaying.Windows;

internal sealed class BrowserWebsiteDetector(bool probeAddress = false,
    Func<IReadOnlyList<MediaSnapshot>, List<(string Browser, string Title, IntPtr Handle)>>? windowReader = null,
    Func<IntPtr, string, CancellationToken, Task<BrowserAddressReader.Result>>? addressReader = null,
    TimeProvider? timeProvider = null) : IDisposable, IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private bool _disposed;
    private readonly Dictionary<string, (MediaWebsite Website, string? VideoId, DateTimeOffset Expires)> _known = new(StringComparer.Ordinal);
    private Task<(string TrackKey, BrowserAddressReader.Result Match)>? _addressLookup;
    private readonly Dictionary<string, DateTimeOffset> _attempts = new(StringComparer.Ordinal);
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
                {
                    if (_known.Count >= 256 && !_known.ContainsKey(result.TrackKey)) _known.Remove(_known.MinBy(item => item.Value.Expires).Key);
                    _known[result.TrackKey] = (result.Match.Website, result.Match.VideoId, (timeProvider ?? TimeProvider.System).GetUtcNow().AddHours(1));
                }
            }
            else _ = _addressLookup.Exception; // Observe an optional accessibility provider failure.
            _addressLookup = null;
        }
        var browsers = sessions.Where(s => s.Source == MediaSource.Browser && s.IsPlaying).ToArray();
        if (browsers.Length == 0) return sessions;
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        foreach (var expired in _known.Where(item => item.Value.Expires <= now).Select(item => item.Key).ToArray()) _known.Remove(expired);
        var windows = windowReader?.Invoke(browsers) ?? ReadBrowserWindows(browsers);
        var pending = new List<(MediaSnapshot Media, IntPtr Window)>();
        var enriched = sessions.Select(media =>
        {
            if (media.Source != MediaSource.Browser || !media.IsPlaying) return media;
            var matchedWindow = windows.FirstOrDefault(w => w.Browser == media.SourceName
                && BrowserSiteClassifier.FromWindowTitle(media.Title, w.Title) == MediaWebsite.YouTube);
            var website = matchedWindow.Handle != IntPtr.Zero ? MediaWebsite.YouTube : MediaWebsite.Unknown;
            var videoId = _known.TryGetValue(media.TrackKey, out var cached) ? cached.VideoId : null;
            if (website != MediaWebsite.Unknown)
            {
                if (_known.Count >= 256 && !_known.ContainsKey(media.TrackKey)) _known.Remove(_known.MinBy(item => item.Value.Expires).Key);
                _known[media.TrackKey] = (website, videoId, now.AddHours(1));
            }
            else if (_known.TryGetValue(media.TrackKey, out var known))
            {
                website = known.Website;
                _known[media.TrackKey] = (known.Website, known.VideoId, now.AddHours(1));
            }
            var browserWindow = matchedWindow.Handle != IntPtr.Zero ? matchedWindow.Handle : windows.FirstOrDefault(w => w.Browser == media.SourceName).Handle;
            if (readAddress && browserWindow != IntPtr.Zero && (website == MediaWebsite.Unknown || videoId is null)) pending.Add((media, browserWindow));
            return media with { Website = website, VideoId = readAddress ? videoId : null };
        }).ToArray();
        if (_addressLookup is null)
        {
            var next = pending.Where(p => !_attempts.TryGetValue(p.Media.TrackKey, out var last) || now - last >= TimeSpan.FromSeconds(5))
                .OrderByDescending(p => p.Media.IsCurrent).ThenBy(p => _attempts.GetValueOrDefault(p.Media.TrackKey)).FirstOrDefault();
            if (next.Window != IntPtr.Zero)
            {
                if (_attempts.Count >= 256 && !_attempts.ContainsKey(next.Media.TrackKey)) _attempts.Remove(_attempts.MinBy(p => p.Value).Key);
                _attempts[next.Media.TrackKey] = now;
                _addressLookup = ReadAddressAsync(next.Media.TrackKey, next.Window, next.Media.Title);
            }
        }
        return enriched;
    }

    private async Task<(string, BrowserAddressReader.Result)> ReadAddressAsync(string track, IntPtr window, string title)
    {
        var result = addressReader is not null ? await addressReader(window, title, _stop.Token) : probeAddress
            ? await Task.Run(() => BrowserAddressReader.Read(window, title, true), _stop.Token)
            : await BrowserAddressProbe.ReadAsync(window, title, _stop.Token);
        return (track, result);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel(); _stop.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        try { if (_addressLookup is not null) await _addressLookup.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or InvalidOperationException) { }
        finally { _stop.Dispose(); }
    }

    public async Task WaitForPendingAsync(CancellationToken cancellationToken)
    {
        if (_addressLookup is null) return;
        try { await _addressLookup.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
        catch (TimeoutException) { }
    }

    internal static string WindowTitle(IntPtr window)
    {
        var title = new StringBuilder(2048);
        return GetWindowText(window, title, title.Capacity) > 0 ? title.ToString() : "";
    }

    internal static uint WindowProcessId(IntPtr window) { GetWindowThreadProcessId(window, out var id); return id; }

    internal static List<(string Browser, string Title, IntPtr Handle)> ReadBrowserWindows(IReadOnlyList<MediaSnapshot>? sessions = null)
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
                if (source.Source != MediaSource.Browser || (sessions is not null && !sessions.Any(s => s.SourceName == source.Name))) return true;
                var title = WindowTitle(window);
                if (title.Length > 0) windows.Add((source.Name, title, window));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return true;
        }, IntPtr.Zero);
        return windows;
    }
}
