using NowPlaying.Core;

namespace NowPlaying.Windows.SynkoUi;

internal sealed record UiPreferences(string ApplicationId, int SourceMode, bool AutomaticArtwork, bool YouTubeThumbnails,
    string ImageKey, bool Startup, string ThemeMode, string ColorSource, string? CustomSeed,
    bool OnboardingDone, int OnboardingStep, string Version, bool AutoCheckUpdates);

internal sealed class LiveSnapshot : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly CoverImageLoader _images;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _imageCancellation;
    private string? _url;
    private string? _cover;
    private Task<string?>? _loading;
    private UiMedia? _previous;
    private DateTimeOffset _retryAfter;
    private int _failures;
    public event Action? Changed;

    internal LiveSnapshot(HttpClient? http = null, TimeProvider? timeProvider = null, string? cacheDirectory = null)
    {
        _http = http ?? new HttpClient();
        _ownsHttp = http is null;
        _time = timeProvider ?? TimeProvider.System;
        _images = new(_http, cacheDirectory ?? Path.Combine(SettingsStore.Folder, "artwork", "images"));
    }

    public UiSnapshot Create(ServiceState state, AppSettings settings)
    {
        var selected = state.Selected is { } active && MediaSelector.Allows(active.Source, settings.SourceMode) ? active
            : MediaSelector.Select(state.Sessions, settings.SourceMode, _previous?.Key)
                ?? state.Sessions.FirstOrDefault(s => !s.IsPlaying && MediaSelector.Allows(s.Source, settings.SourceMode) && s.TrackKey == _previous?.Key && !string.IsNullOrWhiteSpace(s.Title));
        var url = selected?.TrackKey == state.Selected?.TrackKey ? state.ArtworkUrl : selected?.TrackKey == _previous?.Key ? _url : null;
        var showArtwork = selected?.Source switch { MediaSource.AppleMusic => settings.AutomaticArtwork, MediaSource.Browser => settings.YouTubeThumbnails, _ => true };
        if (!showArtwork) url = null;
        if (selected is null) url = null;
        if (url != _url)
        {
            _url = url;
            _cover = null;
            _imageCancellation?.Cancel();
            _imageCancellation?.Dispose();
            _imageCancellation = null;
            _loading = null;
            _failures = 0;
            _retryAfter = DateTimeOffset.MinValue;
        }
        if (_loading?.IsCompleted == true)
        {
            if (_loading.IsCompletedSuccessfully) _cover = _loading.Result;
            if (_loading.IsFaulted) _ = _loading.Exception;
            if (_cover is null) _retryAfter = _time.GetUtcNow().AddSeconds(Math.Min(30, 2 << Math.Min(_failures++, 4)));
            _loading = null;
        }
        // Native SMTC bytes are ready before catalogue/network work finishes.
        // Only download when those bytes are unavailable, and retry failed
        // downloads even when the URL/track has not changed.
        if (url is not null && _cover is null && selected?.ThumbnailData is null && _loading is null && _time.GetUtcNow() >= _retryAfter)
        {
            _imageCancellation?.Dispose();
            _imageCancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _loading = _images.LoadAsync(url, _imageCancellation.Token);
            _ = ObserveAsync(_loading);
        }
        _previous = selected is null ? null : new(selected.TrackKey, selected.Title, selected.Artist, selected.Album,
            selected.Source == MediaSource.AppleMusic ? "apple" : selected.Source == MediaSource.Other ? "player" : selected.Website == MediaWebsite.YouTube ? "youtube" : "browser",
            selected.Source == MediaSource.AppleMusic ? "Apple Music" : selected.Source == MediaSource.Other ? selected.SourceName : selected.Website == MediaWebsite.YouTube ? "YouTube" : "Браузер",
            selected.IsPlaying, selected.Position.TotalSeconds, selected.Duration.TotalSeconds, selected.PlaybackRate,
            selected.ObservedAt.ToUnixTimeMilliseconds(), _cover ?? (showArtwork ? selected.ThumbnailData : null), selected.Source == MediaSource.Browser ? selected.SourceName : null);
        var sources = state.Sessions.Where(s => s.Source is MediaSource.AppleMusic or MediaSource.Browser || settings.SourceMode == SourceMode.AllPlayers)
            .GroupBy(s => s.Source == MediaSource.AppleMusic ? "apple" : (s.Source == MediaSource.Other ? "player:" : "browser:") + s.SourceName)
            .Select(g => new UiSource(g.Key, g.First().SourceName switch
            { "Google Chrome" => "Chrome", "Microsoft Edge" => "Edge", var name => name }, g.Any(s => s.IsPlaying), g.First().Source == MediaSource.Browser ? g.First().SourceName : null)).ToArray();
        return new(settings.Enabled, _previous, sources, Preferences(settings));
    }

    public static UiPreferences Preferences(AppSettings settings) => new(settings.ApplicationId, (int)settings.SourceMode,
        settings.AutomaticArtwork, settings.YouTubeThumbnails, settings.ImageKey, StartupRegistration.IsEnabled(),
        settings.ThemeMode, settings.ColorSource, settings.CustomSeed, settings.OnboardingDone, settings.OnboardingStep,
        Application.ProductVersion.Split('+')[0], settings.AutoCheckUpdates);

    private async Task ObserveAsync(Task<string?> loading)
    {
        try { await loading.ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException) { }
        if (!_stop.IsCancellationRequested && ReferenceEquals(_loading, loading)) Changed?.Invoke();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _imageCancellation?.Cancel();
        _imageCancellation?.Dispose();
        if (_ownsHttp) _http.Dispose();
        _stop.Dispose();
    }
}
