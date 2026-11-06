using NowPlaying.Core;

namespace NowPlaying.Windows;

internal sealed record ServiceState(IReadOnlyList<MediaSnapshot> Sessions, MediaSnapshot? Selected,
    string DiscordStatus, string? MediaError, bool Published, string? ArtworkStatus = null, string? ArtworkUrl = null);

internal sealed class PresenceService : IAsyncDisposable
{
    private readonly MediaMonitor _media = new();
    private readonly DiscordRpcClient _discord = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly HttpClient _http = new();
    private readonly ArtworkCoordinator _artwork;
    private AppSettings _settings;
    private Task? _loop;
    public event Action<ServiceState>? Updated;

    public PresenceService(AppSettings settings)
    {
        _settings = settings;
        _artwork = new(new MusicArtworkResolver(
            new AppleArtworkResolver(_http, cacheFile: Path.Combine(SettingsStore.Folder, "artwork", "catalog.json")),
            new DeezerArtworkResolver(_http, Path.Combine(SettingsStore.Folder, "artwork", "deezer.json"))), settleDelay: TimeSpan.FromMilliseconds(350));
    }
    public void Configure(AppSettings settings) => Volatile.Write(ref _settings, settings);
    public void Start() => _loop ??= Task.Run(RunAsync);

    private async Task RunAsync()
    {
        string? previousTrack = null;
        Presence? published = null;
        var hasPublished = false;
        var nextConnect = DateTimeOffset.MinValue;
        var lastPublish = DateTimeOffset.MinValue;
        var status = "Ожидание Discord";
        var imageRetry = new PublicImageRetry();
        var diagnostics = new PlaybackDiagnostics();
        string? activeId = null;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var settings = Volatile.Read(ref _settings);
                IReadOnlyList<MediaSnapshot> sessions = [];
                string? mediaError = null;
                try { sessions = await _media.ReadAsync(_stop.Token, readYouTubeAddress: settings.YouTubeThumbnails); }
                catch (Exception ex) when (!_stop.IsCancellationRequested)
                { mediaError = "Windows Media API: " + ex.Message; }

                var selected = MediaSelector.Select(sessions, settings.SourceMode, previousTrack);
                previousTrack = selected?.TrackKey;
                var artwork = _artwork.Update(selected, settings.AutomaticArtwork);
                var desired = settings.Enabled && selected is not null && mediaError is null
                    ? PresenceBuilder.Build(settings.YouTubeThumbnails ? selected : selected with { VideoId = null }, settings.ImageKey, artwork?.Match) : null;
                var artworkStatus = selected?.Website == MediaWebsite.YouTube && settings.YouTubeThumbnails
                    ? selected.VideoId is not null ? "Превью текущего видео YouTube" : "Для превью откройте вкладку с воспроизводимым видео YouTube"
                    : artwork?.Status;
                var artworkUrl = selected?.Website == MediaWebsite.YouTube && settings.YouTubeThumbnails
                    ? YouTubeVideo.ThumbnailUrl(selected.VideoId) : artwork?.Match?.ImageUrl;
                // Media/UI must not wait for Discord's connection or RPC ACK.
                Updated?.Invoke(new(sessions, selected, status, mediaError, hasPublished && _discord.IsConnected, artworkStatus, artworkUrl));
                var previousStatus = status;
                var previouslyPublished = hasPublished && _discord.IsConnected;
                try
                {
                    if (activeId != settings.ApplicationId)
                    {
                        if (_discord.IsConnected && hasPublished) await _discord.SetActivityAsync(null, _stop.Token);
                        await _discord.DisconnectAsync();
                        activeId = settings.ApplicationId;
                        hasPublished = false;
                        published = null;
                        nextConnect = DateTimeOffset.MinValue;
                        imageRetry.Reset();
                    }
                    if (!DiscordRpcClient.IsValidApplicationId(settings.ApplicationId))
                        status = "Нужен Discord Application ID — укажите его ниже";
                    else if (!settings.Enabled && !_discord.IsConnected)
                        status = "Публикация выключена";
                    else
                    {
                        if (!_discord.IsConnected && DateTimeOffset.UtcNow >= nextConnect)
                        {
                            await _discord.ConnectAsync(settings.ApplicationId, _stop.Token);
                            hasPublished = false;
                            published = null;
                            imageRetry.Reset();
                        }
                        if (_discord.IsConnected)
                        {
                            // Send track changes at most once per 5 seconds. Clear
                            // immediately on pause/disable. Timestamp drift >3s
                            // indicates a seek and requires a new presence.
                            if ((Changed(published, desired) || imageRetry.Due(desired, DateTimeOffset.UtcNow))
                                && (desired is null || DateTimeOffset.UtcNow - lastPublish >= TimeSpan.FromSeconds(5)))
                            {
                                await _discord.SetActivityAsync(desired, _stop.Token);
                                imageRetry.Observe(desired, _discord.LastImageAcknowledged, DateTimeOffset.UtcNow);
                                published = desired;
                                hasPublished = desired is not null;
                                lastPublish = DateTimeOffset.UtcNow;
                            }
                            status = !settings.Enabled ? "Публикация выключена"
                                : hasPublished ? "Discord подключён · активность опубликована"
                                : "Discord подключён · ожидание воспроизведения";
                        }
                    }
                }
                catch (Exception ex) when (!_stop.IsCancellationRequested)
                {
                    status = ex.Message;
                    hasPublished = false;
                    published = null;
                    await _discord.DisconnectAsync();
                    nextConnect = DateTimeOffset.UtcNow.AddSeconds(8);
                }
                if (status != previousStatus || previouslyPublished != (hasPublished && _discord.IsConnected))
                    Updated?.Invoke(new(sessions, selected, status, mediaError, hasPublished && _discord.IsConnected, artworkStatus, artworkUrl));
                diagnostics.Write(selected, desired, published, _discord.IsConnected, _discord.LastImageAcknowledged);
                await Task.Delay(TimeSpan.FromMilliseconds(750), _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            if (_discord.IsConnected)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await _discord.SetActivityAsync(null, timeout.Token); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException) { }
            }
            await _discord.DisposeAsync();
            _media.Dispose();
            await _artwork.DisposeAsync();
            _http.Dispose();
        }
    }

    internal static bool Changed(Presence? old, Presence? next)
    {
        if (old is null || next is null) return old != next;
        return old.Details != next.Details || old.State != next.State || old.Type != next.Type
            || old.ActivityName != next.ActivityName
            || old.ImageKey != next.ImageKey || old.ImageText != next.ImageText
            || old.AppleMusicUrl != next.AppleMusicUrl
            || old.YouTubeUrl != next.YouTubeUrl
            || old.SpotifyUrl != next.SpotifyUrl
            || old.Start.HasValue != next.Start.HasValue || old.End.HasValue != next.End.HasValue
            || Math.Abs((old.Start ?? 0) - (next.Start ?? 0)) > 3
            || Math.Abs((old.End ?? 0) - (next.End ?? 0)) > 3;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null) await _loop;
        _stop.Dispose();
    }
}
