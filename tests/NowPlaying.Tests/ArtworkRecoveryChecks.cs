using System.Net;
using System.Text.Json;
using NowPlaying.Core;

internal static class ArtworkRecoveryChecks
{
    public static async Task RunAsync()
    {
        const string image = "https://cdn-images.dzcdn.net/images/cover/verified/500x500.jpg";
        var media = new MediaSnapshot("Spotify.exe", "Spotify", MediaSource.Spotify, "quand c'est ?", "Stromae", "racine carrée", true, true,
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(180), 1, DateTimeOffset.UtcNow);
        var album = JsonSerializer.SerializeToElement(new { data = new[] { new { title = "racine carrée", artist = new { name = "Stromae" }, cover_big = image } } });
        var match = DeezerArtworkResolver.MatchResults(media, album, true);
        Check(match?.ImageUrl == image && match.AppleMusicUrl is null, "Fallback must use a verified artist/album and never fabricate an Apple link");
        Check(DeezerArtworkResolver.MatchResults(media with { Artist = "Wrong artist" }, album, true) is null, "Same album name from another artist must be rejected");
        Check(DeezerArtworkResolver.MatchResults(media with { Album = "Different album" }, album, true) is null, "Wrong album must be rejected");
        foreach (var url in new[] { "https://attacker.invalid/images/cover/test.jpg", "https://cdn-images.dzcdn.net.attacker.invalid/images/cover/a.jpg", "http://cdn-images.dzcdn.net/images/cover/a.jpg", "https://cdn-images.dzcdn.net/images/artist/a.jpg" })
            Check(DeezerArtworkResolver.CoverUrl(url) is null, "Unsafe catalogue image host/path accepted");

        var requests = 0;
        using var handler = new Handler(_ => { requests++; return new(HttpStatusCode.OK) { Content = new StringContent(album.GetRawText()) }; });
        using var http = new HttpClient(handler);
        var folder = Path.Combine(Path.GetTempPath(), "synko-catalogue-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(folder, "fallback.json");
        try
        {
            var resolver = new DeezerArtworkResolver(http, cache, TimeSpan.Zero);
            Check((await resolver.ResolveAsync(media, default)).Match is not null, "Fallback lookup failed");
            var restarted = new DeezerArtworkResolver(http, cache, TimeSpan.Zero);
            Check((await restarted.ResolveAsync(media with { Title = "Another album track" }, default)).Match?.ImageUrl == image && requests == 1, "Album cache must survive rapid skips and restart");
            await restarted.ResolveAsync(media with { Source = MediaSource.Browser }, default);
            Check(requests == 1, "Music catalogue must not receive browser metadata");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }

        var slow = new Resolver(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Miss(); });
        var fast = new Resolver((_, _) => Task.FromResult(new ArtworkResult(match, "Found", DateTimeOffset.UtcNow.AddDays(1))));
        var recovery = new MusicArtworkResolver(slow, fast, TimeSpan.Zero);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Check((await recovery.ResolveAsync(media, timeout.Token)).Match == match, "A stalled primary must not block the fallback");
        var unavailable = new Resolver((_, _) => throw new HttpRequestException("Temporary failure"));
        Check((await new MusicArtworkResolver(unavailable, fast, TimeSpan.Zero).ResolveAsync(media, default)).Match == match, "Primary errors must allow fallback");
        var missing = await new MusicArtworkResolver(unavailable, new Resolver((_, _) => Task.FromResult(Miss())), TimeSpan.Zero).ResolveAsync(media, default);
        Check(missing.Match is null && missing.ExpiresAt < DateTimeOffset.UtcNow.AddSeconds(20), "Missing artwork must be retried on the same playing track");
        var clock = new Clock();
        var attempts = 0;
        var retry = new Resolver((_, _) => Task.FromResult(++attempts == 1
            ? new ArtworkResult(null, "Temporary failure", clock.GetUtcNow().AddSeconds(2))
            : new ArtworkResult(match, "Found", clock.GetUtcNow().AddDays(1))));
        await using (var coordinator = new ArtworkCoordinator(retry, clock))
        {
            Check(coordinator.Update(media, true)?.Match is null, "First lookup should be pending/missing");
            clock.Advance();
            Check(coordinator.Update(media, true)?.Match == match && attempts == 2, "Artwork must recover without changing or restarting the track");
        }

        var started = new List<string>();
        var debounce = new Resolver((m, _) => { started.Add(m.Title); return Task.FromResult(new ArtworkResult(match, "Found", DateTimeOffset.UtcNow.AddDays(1))); });
        await using (var coordinator = new ArtworkCoordinator(debounce, settleDelay: TimeSpan.FromMilliseconds(40)))
        {
            coordinator.Update(media with { Title = "Skipped A" }, true);
            coordinator.Update(media with { Title = "Skipped B" }, true);
            coordinator.Update(media, true);
            await Task.Delay(100);
            Check(coordinator.Update(media, true)?.Match == match && started.SequenceEqual([media.Title]), "Rapid skips must only search the settled track");
        }
        foreach (var id in new[] { "Helium.ECO5FQJQXN242JAK3P", "Helium.exe", @"C:\Apps\Helium\helium.exe" })
        {
            Check(SourceClassifier.Classify(id) == (MediaSource.Browser, "Helium"), "Helium must participate in browser and automatic source selection");
        }
        Check(BrowserSiteClassifier.FromWindowTitle("Playing video", "(3) Playing video - YouTube - Helium") == MediaWebsite.YouTube, "Helium suffix must not hide YouTube");
        var youtube = media with { AppId = "Helium.test", SourceName = "Helium", Source = MediaSource.Browser, Website = MediaWebsite.YouTube, VideoId = "Au6KnAm-Njc" };
        Check(PresenceBuilder.Build(youtube, "old") is { Type: 3, ActivityName: "видео на YouTube", ImageKey: "https://i.ytimg.com/vi/Au6KnAm-Njc/hqdefault.jpg" }, "Helium must publish Watching YouTube with the actual video thumbnail");
        Console.WriteLine("PASS fallback catalogue identity/host validation, album disk cache, stalled/error recovery, same-track retry, skip debounce and Helium YouTube presence");
    }

    private static ArtworkResult Miss() => new(null, "Unavailable", DateTimeOffset.UtcNow.AddSeconds(10));
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Resolver(Func<MediaSnapshot, CancellationToken, Task<ArtworkResult>> resolve) : IArtworkResolver
    { public Task<ArtworkResult> ResolveAsync(MediaSnapshot media, CancellationToken token) => resolve(media, token); }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance() => _now = _now.AddSeconds(3);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(response(request)); }
}
