using NowPlaying.Core;

namespace NowPlaying.Windows.SynkoUi;

internal sealed record UiPreferences(string ApplicationId, int SourceMode, bool AutomaticArtwork, bool YouTubeThumbnails,
    string ImageKey, bool Startup, string ThemeMode, string ColorSource, string? CustomSeed,
    bool OnboardingDone, int OnboardingStep, string Version, bool AutoCheckUpdates);

internal sealed class LiveSnapshot : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly CancellationTokenSource _stop = new();
    private string? _url;
    private string? _cover;
    private Task<string?>? _loading;
    private UiMedia? _previous;

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
            _loading = url is null ? null : PreviewSnapshot.LoadCoverAsync(_http, url, _stop.Token);
        }
        if (_loading?.IsCompletedSuccessfully == true) _cover = _loading.Result;
        if (_loading?.IsFaulted == true) _ = _loading.Exception;
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

    public void Dispose() { _stop.Cancel(); _http.Dispose(); _stop.Dispose(); }
}
