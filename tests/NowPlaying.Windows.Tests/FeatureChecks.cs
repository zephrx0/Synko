using System.Diagnostics;
using System.Security.Cryptography;
using NowPlaying.Core;
using NowPlaying.Windows;
using NowPlaying.Windows.SynkoUi;
using NowPlaying.Windows.Updates;

namespace NowPlaying.Windows.Tests;

internal static class FeatureChecks
{
    public static void Run(string folder)
    {
        var previousFolder = SettingsStore.OverrideFolder;
        SettingsStore.OverrideFolder = Path.Combine(folder, "windows-feature-profile");
        Directory.CreateDirectory(SettingsStore.Folder);
        try
        {
            File.WriteAllText(SettingsStore.FilePath, "{\"ApplicationId\":\"123456789012345678\",\"SourceMode\":1,\"ImageKey\":\"keep-this-key\"}");
            var settings = SettingsStore.Load();
            Check(settings.OnboardingDone && settings.SourceMode == SourceMode.AppleMusic && settings.ImageKey == "keep-this-key", "Legacy configuration must keep explicit source and asset key");
            Check(settings.ThemeMode == "system" && settings.AutomaticArtwork && settings.YouTubeThumbnails && settings.AutoCheckUpdates, "New options need safe defaults");
            SettingsStore.Save(settings with { OnboardingDone = false, OnboardingStep = 3, ThemeMode = "dark", ColorSource = "custom", CustomSeed = "#426894", AutoCheckUpdates = false });
            var restored = SettingsStore.Load();
            Check(!restored.OnboardingDone && restored.OnboardingStep == 3 && restored.CustomSeed == "#426894" && !restored.AutoCheckUpdates, "Wizard and appearance preferences must survive restart");
            Console.WriteLine("PASS settings migration, preferences and wizard resume");
            Check(BrowserIdentity.FromMetadata("Браузер", @"C:\Apps\Yandex\YandexBrowser\Application\browser.exe", "Yandex") == "Яндекс Браузер", "Yandex browser.exe must use its own brand");
            Check(BrowserIdentity.FromMetadata("Opera", @"C:\Apps\Opera GX\opera.exe", "Opera GX") == "Opera GX", "Opera GX must not be confused with Opera");

            var now = DateTimeOffset.UtcNow;
            const string thumbnail = "data:image/png;base64,iVBORw0KGgo=";
            var chrome = new MediaSnapshot("chrome.exe", "Google Chrome", MediaSource.Browser, "Video", "Channel", "", true, false,
                TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(4), 1, now, MediaWebsite.YouTube, ThumbnailData: thumbnail);
            var edge = chrome with { AppId = "msedge.exe", SourceName = "Microsoft Edge", IsPlaying = false };
            var apple = chrome with { AppId = "AppleMusic.exe", SourceName = "Apple Music", Source = MediaSource.AppleMusic, IsPlaying = false };
            using (var live = new LiveSnapshot())
            {
                var state = new ServiceState([apple, chrome, edge], chrome, "", null, false);
                var view = live.Create(state, settings with { SourceMode = SourceMode.Automatic });
                Check(view.Media?.Title == "Video" && view.Media.Source == "youtube" && view.Media.Browser == "Google Chrome", "Browser metadata must reach the UI");
                Check(view.Media?.Cover == thumbnail && view.Sources.Count == 3 && view.Sources.Count(s => s.Browser is not null) == 2, "Separate browsers and native thumbnails must be retained");
                Check(live.Create(state, settings with { YouTubeThumbnails = false, SourceMode = SourceMode.Automatic }).Media?.Cover is null, "Disabling thumbnails must remove the image");
                var paused = chrome with { IsPlaying = false };
                Check(live.Create(new([apple, paused, edge], null, "", null, false), settings with { SourceMode = SourceMode.Automatic }).Media?.Playing == false, "Pausing must flatten the existing card");
                Check(live.Create(state, settings with { SourceMode = SourceMode.AppleMusic }).Media is null, "An explicit source filter must apply immediately to the UI");
                var other = chrome with { AppId = "vlc.exe", Source = MediaSource.Other, SourceName = "VLC", Website = MediaWebsite.Unknown };
                var allPlayers = live.Create(new([other], other, "", null, false), settings with { SourceMode = SourceMode.AllPlayers });
                Check(allPlayers.Media?.Source == "player" && allPlayers.Media.SourceLabel == "VLC" && allPlayers.Media.Cover == thumbnail, "All-player mode must keep a generic player's identity and image");
            }
            Console.WriteLine("PASS browser card, independent browser identities, thumbnail toggle and pause");

            var updateFolder = Path.Combine(SettingsStore.Folder, "updates", "test"); Directory.CreateDirectory(updateFolder);
            var installFolder = Path.Combine(SettingsStore.Folder, "test-install"); Directory.CreateDirectory(installFolder);
            var target = Path.Combine(installFolder, "Synko.exe"); var payload = Path.Combine(updateFolder, "Synko.exe");
            File.Copy(Environment.ProcessPath!, payload, overwrite: true);
            File.WriteAllText(target, "previous executable fixture");
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(payload)));
            var version = FileVersionInfo.GetVersionInfo(payload).ProductVersion!.Split('+')[0];
            var job = new UpdateJob(Environment.ProcessId, 0, target, payload, hash, version);
            UpdateInstaller.ApplyFiles(job);
            Check(File.ReadAllText(target + ".previous") == "previous executable fixture", "Updater must preserve the previous executable");
            Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))) == hash, "Updater must install verified bytes");
            File.WriteAllText(target, "keep after failed update");
            ExpectFailure(() => UpdateInstaller.ApplyFiles(job with { Sha256 = new string('0', 64) }));
            Check(File.ReadAllText(target) == "keep after failed update", "Bad checksum must leave the installation untouched");
            ExpectFailure(() => UpdateInstaller.Validate(job with { Payload = Path.Combine(installFolder, "Synko.exe") }));
            ExpectFailure(() => UpdateInstaller.Validate(job with { Version = "99.0.0" }));
            using (var locked = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                ExpectFailure(() => UpdateInstaller.ApplyFiles(job));
            Check(File.ReadAllText(target) == "keep after failed update" && !File.Exists(target + ".next"), "Locked executable must survive and staging must be cleaned up");
            Console.WriteLine("PASS update file replacement, backup, hash/version rejection and locked-file recovery");
        }
        finally { SettingsStore.OverrideFolder = previousFolder; }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void ExpectFailure(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Invalid update was accepted.");
    }
}
