using NowPlaying.Core;
using Windows.Media.Control;
using Windows.Storage.Streams;
using System.Collections.Concurrent;

namespace NowPlaying.Windows;

internal sealed class MediaMonitor(bool probeAddress = false) : IDisposable, IAsyncDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private readonly BrowserWebsiteDetector _websites = new(probeAddress);
    private readonly ConcurrentDictionary<string, string> _thumbnails = new();
    public void Dispose() => _websites.Dispose();
    public ValueTask DisposeAsync() => _websites.DisposeAsync();

    public async Task<IReadOnlyList<MediaSnapshot>> ReadAsync(CancellationToken cancellationToken, bool waitForBrowserAddress = false, bool readYouTubeAddress = true)
    {
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(4), cancellationToken);
            var current = _manager.GetCurrentSession();
            var sessions = _manager.GetSessions();
            var reads = sessions.Select(session => ReadSessionAsync(session, current, cancellationToken));
            var snapshots = await Task.WhenAll(reads);
            var media = snapshots.OfType<MediaSnapshot>().ToArray();
            var enriched = _websites.Enrich(media, readYouTubeAddress);
            if (waitForBrowserAddress && readYouTubeAddress)
            {
                await _websites.WaitForPendingAsync(cancellationToken);
                enriched = _websites.Enrich(media, readYouTubeAddress);
            }
            return enriched;
        }
        catch
        {
            _manager = null;
            throw;
        }
    }

    private async Task<MediaSnapshot?> ReadSessionAsync(GlobalSystemMediaTransportControlsSession session,
        GlobalSystemMediaTransportControlsSession? current, CancellationToken cancellationToken)
    {
        try
        {
            // A vanished or unresponsive player must not stop the other sessions.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var properties = await session.TryGetMediaPropertiesAsync().AsTask(timeout.Token);
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var now = DateTimeOffset.UtcNow;
            var playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var rate = playback.PlaybackRate ?? 1;
            var duration = timeline.EndTime - timeline.StartTime;
            var position = timeline.Position - timeline.StartTime;
            // SMTC reports a sampled position, not necessarily a fresh clock.
            var age = now - timeline.LastUpdatedTime;
            if (playing && age > TimeSpan.Zero && age < TimeSpan.FromDays(1))
                position += TimeSpan.FromSeconds(age.TotalSeconds * rate);
            if (duration > TimeSpan.Zero)
                position = TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, duration.TotalSeconds));
            else position = TimeSpan.Zero;
            var appId = session.SourceAppUserModelId;
            var source = BrowserIdentity.ResolveSession(appId, properties.Title);
            var media = MediaMetadata.Normalize(new(appId, source.Name, source.Source, properties.Title ?? "", properties.Artist ?? "",
                properties.AlbumTitle ?? "", playing, session.Equals(current), position, duration, rate, now));
            _thumbnails.TryGetValue(media.TrackKey, out var picture);
            if (picture is null && properties.Thumbnail is { } thumbnail)
            {
                try
                {
                    using var imageTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    imageTimeout.CancelAfter(TimeSpan.FromMilliseconds(600));
                    using var stream = await thumbnail.OpenReadAsync().AsTask(imageTimeout.Token);
                    if (stream.Size is > 0 and <= 2 * 1024 * 1024)
                    {
                        using var reader = new DataReader(stream);
                        await reader.LoadAsync((uint)stream.Size).AsTask(imageTimeout.Token);
                        var bytes = new byte[stream.Size]; reader.ReadBytes(bytes);
                        picture = NativeArtwork.ToDataUrl(bytes);
                        if (picture is not null)
                        {
                            if (_thumbnails.Count >= 32) _thumbnails.Clear();
                            _thumbnails.TryAdd(media.TrackKey, picture);
                        }
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is IOException or System.Runtime.InteropServices.COMException or OperationCanceledException or ArgumentException) { }
            }
            media = media with { ThumbnailData = picture };
            return media;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
            ex is System.Runtime.InteropServices.COMException or OperationCanceledException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
