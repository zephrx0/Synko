using System.Security.Principal;
using System.Text.Json;

namespace NowPlaying.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] is "--diagnose" or "--diagnose-artwork" or "--diagnose-address")
            return DiagnoseAsync(args[1], args[0] == "--diagnose-artwork", args[0] == "--diagnose-address").GetAwaiter().GetResult();
        if (args.Length == 2 && args[0] == "--apply-update") return Updates.UpdateInstaller.Run(args[1]);
        ApplicationConfiguration.Initialize();
        if (args.Length == 2 && args[0] == "--preview-ui") return SynkoUi.PreviewRunner.Run(Path.GetFullPath(args[1]));
        if (args.Length == 2 && args[0] == "--smoke-ui") return SynkoUi.SmokeRunner.Run(Path.GetFullPath(args[1]));
        var suffix = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        // Synko ships as a separate app beside legacy Now Playing installs. Give
        // it a distinct activation channel so starting Synko cannot reveal the
        // legacy settings form instead of the new Synko window.
        using var instance = new Mutex(true, @"Local\Synko.Instance." + suffix, out var firstInstance);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Synko.Show." + suffix);
        if (!firstInstance) { showEvent.Set(); return 0; }
        try
        {
            try { StartupRegistration.Migrate(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Directory.CreateDirectory(SettingsStore.Folder);
                File.AppendAllText(Path.Combine(SettingsStore.Folder, "synko-error.log"), ex.Message + Environment.NewLine);
            }
            using var context = new TrayApplication(SettingsStore.Load(), showEvent, args.Contains("--background"));
            Application.Run(context);
            return 0;
        }
        finally { instance.ReleaseMutex(); }
    }

    private static async Task<int> DiagnoseAsync(string path, bool includeArtwork, bool probeAddress = false)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(includeArtwork ? 45 : 10));
            var settings = SettingsStore.Load();
            var sessions = await new MediaMonitor(probeAddress).ReadAsync(timeout.Token, waitForBrowserAddress: true, readYouTubeAddress: settings.YouTubeThumbnails);
            NowPlaying.Core.ArtworkResult? artwork = null;
            if (includeArtwork && NowPlaying.Core.MediaSelector.Select(sessions, NowPlaying.Core.SourceMode.AppleMusic, null) is { } media)
            {
                using var http = new HttpClient();
                artwork = await new NowPlaying.Core.AppleArtworkResolver(http).ResolveAsync(media, timeout.Token);
            }
            var selected = NowPlaying.Core.MediaSelector.Select(sessions, settings.SourceMode, null);
            var activity = selected is null ? null : NowPlaying.Core.PresenceBuilder.Build(selected, settings.ImageKey,
                selected.Source == NowPlaying.Core.MediaSource.AppleMusic ? artwork?.Match : null).ToActivity();
            var automatic = NowPlaying.Core.MediaSelector.Select(sessions, NowPlaying.Core.SourceMode.Automatic, null);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { success = true, sessions, artwork, activity,
                sourceMode = settings.SourceMode, automaticTitle = automatic?.Title, browser = BrowserAddressReader.LastDiagnostic,
                thumbnails = sessions.Select(s => new { s.SourceName, available = s.ThumbnailData is not null }) }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { success = false, error = ex.ToString() }));
            return 1;
        }
    }
}
