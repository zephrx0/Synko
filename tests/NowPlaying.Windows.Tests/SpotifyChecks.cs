using System.Text.Json;
using NowPlaying.Core;
using NowPlaying.Windows;
using NowPlaying.Windows.SynkoUi;

namespace NowPlaying.Windows.Tests;

internal static class SpotifyChecks
{
    public static void Run()
    {
        var original = SettingsStore.OverrideFolder;
        var folder = Path.Combine(Path.GetTempPath(), "synko-spotify-" + Guid.NewGuid().ToString("N"));
        SettingsStore.OverrideFolder = folder;
        try
        {
            SettingsStore.Save(new AppSettings { SourceMode = SourceMode.Spotify, ImageKey = "keep-apple-key", OnboardingDone = true });
            var settings = SettingsStore.Load();
            Check(settings.SourceMode == SourceMode.Spotify && settings.ImageKey == "keep-apple-key", "Spotify selection must persist without rewriting existing settings");
            var legacy = JsonSerializer.Deserialize<AppSettings>("{\"SourceMode\":3}")!;
            Check(legacy.SourceMode == SourceMode.AllPlayers, "Existing all-player preference must retain its enum value");
            const string cover = "data:image/png;base64,iVBORw0KGgo=";
            var now = DateTimeOffset.UtcNow;
            var spotify = new MediaSnapshot("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify", MediaSource.Spotify,
                "Spotify test track", "Test artist", "Test album", true, true, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(3), 1, now, ThumbnailData: cover);
            var apple = spotify with { Source = MediaSource.AppleMusic, SourceName = "Apple Music", AppId = "AppleMusic.exe", IsPlaying = false };
            var chrome = spotify with { Source = MediaSource.Browser, SourceName = "Google Chrome", AppId = "chrome.exe", IsPlaying = false };
            var state = new ServiceState([apple, spotify, chrome], spotify, "", null, false);
            using var live = new LiveSnapshot();
            var snapshot = live.Create(state, settings);
            Check(snapshot.Media is { Source: "spotify", SourceLabel: "Spotify", Cover: cover, Browser: null }, "Native Spotify identity and immediate artwork must reach the UI");
            Check(snapshot.Sources.Any(s => s.Id == "spotify" && s.Name == "Spotify" && s.Playing), "Spotify needs its own status label");
            Check(snapshot.Sources.Count == 3, "Other installed sources must remain visible");
            Check(live.Create(state, settings with { AutomaticArtwork = false }).Media?.Cover is null, "Artwork switch must cover Spotify too");
            Check(live.Create(state, settings with { SourceMode = SourceMode.AppleMusic }).Media is null, "Explicit Apple Music filter must not display Spotify");
            var presence = PresenceBuilder.Build(spotify, settings.ImageKey);
            Check(PresenceService.Changed(presence, presence with { SpotifyUrl = "https://open.spotify.com/search/other" }), "Spotify link changes must update Discord");
            Console.WriteLine("PASS Spotify settings migration, independent source filter, native artwork, status label and Discord change detection (simulated media)");
        }
        finally
        {
            SettingsStore.OverrideFolder = original;
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
