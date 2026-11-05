using System.Net;
using NowPlaying.Core;
using NowPlaying.Windows.SynkoUi;

namespace NowPlaying.Windows.Tests;

internal static class ArtworkChecks
{
    public static async Task RunAsync(string folder)
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6t8AAAAASUVORK5CYII=");
        var dataUrl = NativeArtwork.ToDataUrl(png);
        Check(dataUrl?.StartsWith("data:image/png;base64,", StringComparison.Ordinal) == true, "Native MIME must come from image bytes, never a comma-separated Apple Music MIME list");
        using (var input = new MemoryStream(Convert.FromBase64String(dataUrl!.Split(',')[1])))
        using (var image = Image.FromStream(input)) Check(image.Width == 1, "Native data URL must decode to an actual image");
        Check(NativeArtwork.ToDataUrl([0xff, 0xd8, 0xff, 0xe0])?.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal) == true, "JPEG MIME must be canonical");
        Check(NativeArtwork.ToDataUrl([0, 1, 2, 3]) is null, "Unknown native data must allow remote fallback");
        var cache = Path.Combine(folder, "cover-recovery-" + Guid.NewGuid().ToString("N"));
        const string url = "https://is1-ssl.mzstatic.com/test.jpg";
        const string native = "data:image/png;base64,iVBORw0KGgo=";
        var time = new Clock();
        var requests = 0;
        using var handler = new Handler((_, _) =>
        {
            requests++;
            return Task.FromResult(requests == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Picture());
        });
        using var http = new HttpClient(handler);
        var track = new MediaSnapshot("AppleMusic.exe", "Apple Music", MediaSource.AppleMusic, "Song", "Artist", "Album",
            true, true, TimeSpan.Zero, TimeSpan.FromMinutes(3), 1, time.GetUtcNow());
        var state = new ServiceState([track], track, "", null, false, ArtworkUrl: url);
        var settings = new AppSettings();
        using (var live = new LiveSnapshot(http, time, cache))
        {
            live.Create(state, settings);
            live.Create(state, settings); // Observe the failed request.
            Check(requests == 1, "Do not retry on every media poll");
            time.Advance(TimeSpan.FromSeconds(3));
            await WaitCover(live, state, settings);
            Check(requests == 2, "Same URL must recover after a transient image failure");
        }
        using (var restarted = new LiveSnapshot(http, time, cache))
        {
            await WaitCover(restarted, state, settings);
            Check(requests == 2, "Restart must load the image from disk without a network request");
        }
        using (var live = new LiveSnapshot(http, time, cache + "-native"))
        {
            var local = track with { ThumbnailData = native };
            Check(live.Create(state with { Sessions = [local], Selected = local }, settings).Media?.Cover == native, "Native artwork must be available on the first snapshot");
            Check(requests == 2, "Native artwork must not wait for redundant network downloads");
        }
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var delayedHandler = new Handler((request, _) => request.RequestUri!.AbsolutePath == "/old.jpg" ? pending.Task : Task.FromResult(Picture()));
        using var delayedHttp = new HttpClient(delayedHandler);
        using (var live = new LiveSnapshot(delayedHttp, time, cache + "-switch"))
        {
            live.Create(state with { ArtworkUrl = "https://is1-ssl.mzstatic.com/old.jpg" }, settings);
            var second = track with { Title = "Second song", ThumbnailData = native };
            var next = state with { Sessions = [second], Selected = second, ArtworkUrl = "https://is1-ssl.mzstatic.com/new.jpg" };
            Check(live.Create(next, settings).Media?.Cover == native, "Track switch must immediately discard the previous download");
            pending.SetResult(Picture());
            await Task.Yield();
            Check(live.Create(next, settings).Media?.Cover == native, "Late previous cover must never replace the new track");
            Check(live.Create(next, settings with { AutomaticArtwork = false }).Media?.Cover is null, "Disabling artwork must remove local and downloaded images");
        }
        Console.WriteLine("PASS native MIME canonicalization, cover retry, persistent image cache, immediate Windows thumbnail and track-switch cancellation");
    }

    private static HttpResponseMessage Picture()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6t8AAAAASUVORK5CYII=")) };
        response.Content.Headers.ContentType = new("image/png");
        return response;
    }

    private static async Task WaitCover(LiveSnapshot live, ServiceState state, AppSettings settings)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (live.Create(state, settings).Media?.Cover is null) await Task.Delay(10, timeout.Token).ConfigureAwait(false);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
