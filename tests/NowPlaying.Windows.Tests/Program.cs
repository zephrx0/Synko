using NowPlaying.Core;
using NowPlaying.Windows;

namespace NowPlaying.Windows.Tests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var sessions = new MediaMonitor().ReadAsync(timeout.Token).GetAwaiter().GetResult();
            Console.WriteLine($"PASS Windows GSMTC API: {sessions.Count} sessions");

            var now = DateTimeOffset.UtcNow;
            var media = new MediaSnapshot("chrome.exe", "Google Chrome", MediaSource.Browser,
                "Длинное название трека · Music for a quiet evening 🎵", "Исполнитель", "Альбом", true, true,
                TimeSpan.FromSeconds(83), TimeSpan.FromSeconds(245), 1, now);
            var first = PresenceBuilder.Build(media, null);
            Check(!PresenceService.Changed(first, first with { Start = first.Start + 2, End = first.End + 2 }), "Ignore small clock drift");
            Check(PresenceService.Changed(first, first with { Start = first.Start + 20, End = first.End + 20 }), "Publish seek");
            Check(PresenceService.Changed(first, null), "Clear after pause");
            Check(PresenceService.Changed(null, first), "Publish after resume");
            Check(PresenceService.Changed(first, first with { ImageKey = "https://is1-ssl.mzstatic.com/cover.jpg" }), "Publish newly found cover");
            Check(PresenceService.Changed(first, first with { ActivityName = "видео на YouTube" }), "Publish website/name changes even when video metadata is unchanged");
            Check(System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"ApplicationId\":\"123456789012345678\"}")!.AutomaticArtwork,
                "Old settings enable automatic artwork without losing the application ID");
            Check(System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"ApplicationId\":\"123456789012345678\"}")!.YouTubeThumbnails,
                "Old settings enable YouTube thumbnails without changing the application ID");
            Console.WriteLine("PASS publication change detection and seeks");

            var path = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("artifacts/ui-preview.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            FeatureChecks.Run(Path.GetDirectoryName(path)!);
            ArtworkChecks.RunAsync(Path.GetDirectoryName(path)!).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
