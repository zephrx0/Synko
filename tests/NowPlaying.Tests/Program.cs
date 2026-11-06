using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using NowPlaying.Core;

var passed = 0;
var failed = 0;
await Run("Artwork recovery and Helium regression coverage", ArtworkRecoveryChecks.RunAsync);
await Run("Spotify desktop and Store IDs are recognised without confusing a browser", () =>
{
    foreach (var id in new[] { "Spotify.exe", "SPOTIFY.EXE", @"C:\Users\Test\AppData\Roaming\Spotify\Spotify.exe", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" })
    {
        var source = SourceClassifier.Classify(id);
        Equal(MediaSource.Spotify, source.Source);
        Equal("Spotify", source.Name);
    }
    Equal(MediaSource.Browser, SourceClassifier.Classify(@"C:\Apps\SpotifyTools\chrome.exe").Source);
    Equal(MediaSource.Other, SourceClassifier.Classify("NotSpotifyHelper.exe").Source);
    Equal(MediaSource.Other, SourceClassifier.Classify("NotSpotify.exe").Source);
    Equal(3, (int)SourceMode.AllPlayers);
    Equal(4, (int)SourceMode.Spotify);
    return Task.CompletedTask;
});
await Run("Auto and Spotify modes select playing Spotify and clear it on pause", () =>
{
    var spotify = Media("Spotify.exe", MediaSource.Spotify, current: false) with { SourceName = "Spotify" };
    var apple = Media("AppleMusic.exe", MediaSource.AppleMusic) with { IsPlaying = false };
    var browser = Media("chrome.exe", MediaSource.Browser) with { IsPlaying = false };
    Equal(spotify, MediaSelector.Select([apple, browser, spotify], SourceMode.Automatic, null));
    Equal(spotify, MediaSelector.Select([apple with { IsPlaying = true }, spotify], SourceMode.Spotify, null));
    Equal<MediaSnapshot?>(null, MediaSelector.Select([spotify with { IsPlaying = false }], SourceMode.Spotify, spotify.TrackKey));
    Check(!MediaSelector.Allows(MediaSource.Spotify, SourceMode.AppleMusic), "Apple Music mode remains independent");
    Check(!MediaSelector.Allows(MediaSource.Spotify, SourceMode.Browser), "Spotify desktop is not a browser");
    return Task.CompletedTask;
});
await Run("Spotify sends Listening Spotify, its own fallback logo and a Spotify search button", () =>
{
    var spotify = Media("Spotify.exe", MediaSource.Spotify) with { SourceName = "Spotify", Title = "Song & / Трек?", Artist = "Artist" };
    var fallback = PresenceBuilder.Build(spotify, "apple-music-only");
    Equal(2, fallback.Type);
    Equal("Spotify", fallback.ActivityName);
    Equal(PresenceBuilder.SpotifyImage, fallback.ImageKey);
    var art = new ArtworkMatch("https://is1-ssl.mzstatic.com/cover.jpg", "Album", "https://music.apple.com/album/test/123");
    var presence = PresenceBuilder.Build(spotify, "apple-music-only", art);
    using var json = JsonDocument.Parse(JsonSerializer.Serialize(presence.ToActivity()));
    Equal("Spotify", json.RootElement.GetProperty("name").GetString());
    Equal(2, json.RootElement.GetProperty("type").GetInt32());
    Equal(art.ImageUrl, json.RootElement.GetProperty("assets").GetProperty("large_image").GetString());
    Equal<string?>(null, presence.AppleMusicUrl);
    var button = json.RootElement.GetProperty("buttons")[0];
    Equal("Найти в Spotify", button.GetProperty("label").GetString());
    var url = new Uri(button.GetProperty("url").GetString()!);
    Equal("open.spotify.com", url.Host);
    Equal("/search/Artist Song & / Трек?", Uri.UnescapeDataString(url.AbsolutePath));
    return Task.CompletedTask;
});
await Run("Spotify shares verified album artwork cache with Apple Music without searching browser metadata", async () =>
{
    var requests = 0;
    using var handler = new FakeHttpHandler(_ =>
    {
        requests++;
        return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { results = new[] { Catalog("Song", "Artist", "Album") } })) };
    });
    using var http = new HttpClient(handler);
    var resolver = new AppleArtworkResolver(http, requestInterval: TimeSpan.Zero);
    var spotify = Media("Spotify.exe", MediaSource.Spotify) with { SourceName = "Spotify" };
    var cover = await resolver.ResolveAsync(spotify, default);
    Check(cover.Match is not null, "Spotify needs a public artwork URL for Discord");
    var apple = await resolver.ResolveAsync(spotify with { Source = MediaSource.AppleMusic, AppId = "AppleMusic.exe" }, default);
    Equal(cover.Match, apple.Match);
    Equal(1, requests);
    await resolver.ResolveAsync(spotify with { Source = MediaSource.Browser }, default);
    Equal(1, requests);
});
await Run("Switching from Spotify cancels the previous artwork and disabling removes it", async () =>
{
    var resolver = new DelayedArtworkResolver();
    await using var coordinator = new ArtworkCoordinator(resolver);
    var spotify = Media("Spotify.exe", MediaSource.Spotify);
    var apple = Media("AppleMusic.exe", MediaSource.AppleMusic) with { Title = "Another song" };
    coordinator.Update(spotify, true);
    coordinator.Update(apple, true);
    resolver.Complete(spotify, "spotify-old");
    Check(coordinator.Update(apple, true)?.Match is null, "Late Spotify artwork must not replace Apple Music");
    resolver.Complete(apple, "apple-new");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    while (coordinator.Update(apple, true)?.Match is null) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
    Equal<ArtworkResult?>(null, coordinator.Update(spotify, false));
});
await Run("Apple Music and browser identification", () =>
{
    Equal(MediaSource.AppleMusic, SourceClassifier.Classify("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App").Source);
    Equal(MediaSource.Browser, SourceClassifier.Classify("Chrome.exe").Source);
    Equal(MediaSource.Browser, SourceClassifier.Classify("Microsoft.MSEdge.Stable_8wekyb3d8bbwe!App").Source);
    Equal(MediaSource.Spotify, SourceClassifier.Classify("Spotify.exe").Source);
    return Task.CompletedTask;
});
await Run("Selection skips paused sources and respects filters", () =>
{
    var apple = Media("AppleInc.AppleMusicWin!App", MediaSource.AppleMusic, current: false);
    var browser = Media("chrome.exe", MediaSource.Browser, current: true);
    Equal(browser, MediaSelector.Select([apple, browser], SourceMode.Automatic, apple.TrackKey));
    Equal(apple, MediaSelector.Select([apple, browser], SourceMode.AppleMusic, null));
    Equal<MediaSnapshot?>(null, MediaSelector.Select([browser with { IsPlaying = false }], SourceMode.Browser, null));
    Equal<MediaSnapshot?>(null, MediaSelector.Select([Media("Other", MediaSource.Other)], SourceMode.Automatic, null));
    Equal(browser, MediaSelector.Select([apple, browser with { IsCurrent = false }], SourceMode.Browser, null) is { } chosen
        ? chosen with { IsCurrent = true } : null);
    return Task.CompletedTask;
});
await Run("Presence timeline, live media and playback speed", () =>
{
    var media = Media("Apple", MediaSource.AppleMusic);
    var presence = PresenceBuilder.Build(media, "");
    Equal(2, presence.Type);
    Equal(media.ObservedAt.AddSeconds(-30).ToUnixTimeSeconds(), presence.Start);
    Equal(media.ObservedAt.AddSeconds(150).ToUnixTimeSeconds(), presence.End);
    Equal<long?>(null, PresenceBuilder.Build(media with { Duration = TimeSpan.Zero }, null).Start);
    Equal<long?>(null, PresenceBuilder.Build(media with { PlaybackRate = 1.5 }, null).Start);
    Equal(3, PresenceBuilder.Build(media with { Source = MediaSource.Browser }, null).Type);
    var payload = JsonSerializer.Serialize(presence.ToActivity());
    using var json = JsonDocument.Parse(payload);
    Check(!json.RootElement.TryGetProperty("assets", out _), "Empty assets must be omitted");
    return Task.CompletedTask;
});
await Run("Unicode payload limit preserves emoji and Cyrillic", () =>
{
    var clipped = PresenceBuilder.Clip(string.Concat(Enumerable.Repeat("🎵Музыка", 80)));
    Check(Encoding.UTF8.GetByteCount(clipped) <= 128, "UTF8 limit");
    Check(!clipped.Contains('\uFFFD'), "No replacement characters");
    Check(Encoding.UTF8.GetByteCount(PresenceBuilder.Clip(new string('я', 100))) == 128, "Cyrillic clipping");
    Check(!DiscordRpcClient.IsValidApplicationId("1234"), "Short ID");
    Check(!DiscordRpcClient.IsValidApplicationId("１２３４５６７８９０１２３４５６７８"), "Non-ASCII ID");
    Check(!DiscordRpcClient.IsValidApplicationId("99999999999999999999"), "Overflow ID");
    return Task.CompletedTask;
});
await Run("RPC framing handles fragmented header and UTF8", async () =>
{
    using var output = new MemoryStream();
    await RpcFrame.WriteJsonAsync(output, 1, new { text = "Привет 🎵" }, default);
    using var input = new FragmentedStream(output.ToArray());
    var frame = await RpcFrame.ReadAsync(input, default);
    Equal(1, frame.Opcode);
    using var json = JsonDocument.Parse(frame.Payload);
    Equal("Привет 🎵", json.RootElement.GetProperty("text").GetString());
});
await Run("RPC framing rejects malicious sizes and truncated packets", async () =>
{
    var header = new byte[8];
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), -1);
    await Throws<InvalidDataException>(() => RpcFrame.ReadAsync(new MemoryStream(header), default));
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), RpcFrame.MaxPayloadBytes + 1);
    await Throws<InvalidDataException>(() => RpcFrame.ReadAsync(new MemoryStream(header), default));
    await Throws<EndOfStreamException>(() => RpcFrame.ReadAsync(new MemoryStream(new byte[3]), default));
});
await Run("Discord handshake, PING/PONG, activity ACK and clear", async () =>
{
    await WithPipe(async (server, token) =>
    {
        await Handshake(server, token);
        await RpcFrame.WriteAsync(server, 3, Encoding.UTF8.GetBytes("heartbeat"), token);
        var first = await RpcFrame.ReadAsync(server, token);
        var second = await RpcFrame.ReadAsync(server, token);
        // READY allows the caller to publish while the reader answers PING.
        // Both frame orders are valid; neither operation may be lost.
        var pong = first.Opcode == 4 ? first : second;
        var set = first.Opcode == 1 ? first : second;
        Equal(4, pong.Opcode);
        Equal("heartbeat", Encoding.UTF8.GetString(pong.Payload));
        Equal(1, set.Opcode);
        using var message = JsonDocument.Parse(set.Payload);
        Equal("SET_ACTIVITY", message.RootElement.GetProperty("cmd").GetString());
        Equal("Song", message.RootElement.GetProperty("args").GetProperty("activity").GetProperty("details").GetString());
        Equal("Apple Music", message.RootElement.GetProperty("args").GetProperty("activity").GetProperty("name").GetString());
        Equal(Environment.ProcessId, message.RootElement.GetProperty("args").GetProperty("pid").GetInt32());
        await Ack(server, message.RootElement.GetProperty("nonce").GetString()!, token);
        var clear = await RpcFrame.ReadAsync(server, token);
        using var cleared = JsonDocument.Parse(clear.Payload);
        Equal(JsonValueKind.Null, cleared.RootElement.GetProperty("args").GetProperty("activity").ValueKind);
        await Ack(server, cleared.RootElement.GetProperty("nonce").GetString()!, token);
        // Stay open until the client's normal disconnect.
        var buffer = new byte[1];
        Equal(0, await server.ReadAsync(buffer, token));
    }, async (client, prefix, token) =>
    {
        await client.ConnectAsync("123456789012345678", token, prefix);
        Check(client.IsConnected, "Connected after READY");
        await client.SetActivityAsync(PresenceBuilder.Build(Media("Apple", MediaSource.AppleMusic), null), token);
        await client.SetActivityAsync(null, token);
        await client.DisconnectAsync();
        Check(!client.IsConnected, "Disconnected");
    });
});
await Run("Discord image omission is detected and retried without changing the video", async () =>
{
    var video = Media("firefox.exe", MediaSource.Browser) with { Website = MediaWebsite.YouTube, VideoId = "Au6KnAm-Njc" };
    var presence = PresenceBuilder.Build(video, null);
    await WithPipe(async (server, token) =>
    {
        await Handshake(server, token);
        for (var i = 0; i < 2; i++)
        {
            using var request = JsonDocument.Parse((await RpcFrame.ReadAsync(server, token)).Payload);
            var data = new Dictionary<string, object> { ["name"] = "видео на YouTube", ["type"] = 3 };
            if (i == 1) data["assets"] = new { large_image = "mp:external/verified/https/i.ytimg.com/vi/Au6KnAm-Njc/hqdefault.jpg" };
            await RpcFrame.WriteJsonAsync(server, 1, new { cmd = "SET_ACTIVITY", nonce = request.RootElement.GetProperty("nonce").GetString(), data }, token);
        }
        Equal(0, await server.ReadAsync(new byte[1], token));
    }, async (client, prefix, token) =>
    {
        await client.ConnectAsync("123456789012345678", token, prefix);
        await client.SetActivityAsync(presence, token);
        Equal<bool?>(false, client.LastImageAcknowledged);
        var now = DateTimeOffset.UtcNow;
        var retry = new PublicImageRetry(); retry.Observe(presence, client.LastImageAcknowledged, now);
        Check(!retry.Due(presence, now.AddSeconds(9)) && retry.Due(presence, now.AddSeconds(11)), "Retry must be rate-limited and not require a media change");
        Check(!retry.Due(presence with { ImageKey = "https://i.ytimg.com/vi/OtherVid123/hqdefault.jpg" }, now.AddMinutes(1)), "Do not retry the old video's artwork on a new video");
        await client.SetActivityAsync(presence, token);
        Equal<bool?>(true, client.LastImageAcknowledged);
        retry.Observe(presence, client.LastImageAcknowledged, now);
        Check(!retry.Due(presence, now.AddHours(1)), "Accepted images must not be resent forever");
        retry.Observe(presence, false, now); retry.Observe(null, null, now);
        Check(!retry.Due(presence, now.AddHours(1)), "Paused playback clears any image retry");
        await client.DisconnectAsync();
    });
});
await Run("Discord ERROR is surfaced instead of claiming success", async () =>
{
    await WithPipe(async (server, token) =>
    {
        await Handshake(server, token);
        using var request = JsonDocument.Parse((await RpcFrame.ReadAsync(server, token)).Payload);
        await RpcFrame.WriteJsonAsync(server, 1, new
        {
            cmd = "SET_ACTIVITY", evt = "ERROR", nonce = request.RootElement.GetProperty("nonce").GetString(),
            data = new { code = 4000, message = "Invalid activity" }
        }, token);
        Equal(0, await server.ReadAsync(new byte[1], token));
    }, async (client, prefix, token) =>
    {
        await client.ConnectAsync("123456789012345678", token, prefix);
        var ex = await Throws<IOException>(() => client.SetActivityAsync(PresenceBuilder.Build(Media("Apple", MediaSource.AppleMusic), null), token));
        Check(ex.Message.Contains("Invalid activity"), "Server error must reach caller");
        await client.DisconnectAsync();
    });
});
await Run("Discord rejects invalid application at handshake", async () =>
{
    await WithPipe(async (server, token) =>
    {
        await RpcFrame.ReadAsync(server, token);
        await RpcFrame.WriteJsonAsync(server, 2, new { code = 4000, message = "Invalid client" }, token);
    }, async (client, prefix, token) =>
    {
        await Throws<IOException>(() => client.ConnectAsync("123456789012345678", token, prefix));
        Check(!client.IsConnected, "Invalid application must not become ready");
    });
});
await Run("Discord reconnects the same client after a closed pipe", async () =>
{
    await using var client = new DiscordRpcClient();
    for (var attempt = 0; attempt < 2; attempt++)
    {
        var prefix = "nowplaying-test-" + Guid.NewGuid().ToString("N") + "-";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(prefix + "0", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(cts.Token);
            await Handshake(server, cts.Token);
            using var request = JsonDocument.Parse((await RpcFrame.ReadAsync(server, cts.Token)).Payload);
            await Ack(server, request.RootElement.GetProperty("nonce").GetString()!, cts.Token);
            Equal(0, await server.ReadAsync(new byte[1], cts.Token));
        });
        await client.ConnectAsync("123456789012345678", cts.Token, prefix);
        await client.SetActivityAsync(PresenceBuilder.Build(Media("Apple", MediaSource.AppleMusic), null), cts.Token);
        await client.DisconnectAsync();
        await serverTask;
    }
});

await Run("Apple metadata separates artist and album without touching browser metadata", () =>
{
    var raw = Media("AppleMusic.exe", MediaSource.AppleMusic) with { Artist = "гнилаялирика & aikko — ценные бумажки", Album = "" };
    var parsed = MediaMetadata.Normalize(raw);
    Equal("гнилаялирика & aikko", parsed.Artist);
    Equal("ценные бумажки", parsed.Album);
    Equal(raw with { Source = MediaSource.Browser }, MediaMetadata.Normalize(raw with { Source = MediaSource.Browser }));
    var hyphenated = raw with { Artist = "A-ha", Album = "" };
    Equal(hyphenated, MediaMetadata.Normalize(hyphenated));
    return Task.CompletedTask;
});
await Run("Cover matching uses album and artist when the song is missing from the public store", () =>
{
    var raw = Media("AppleMusic.exe", MediaSource.AppleMusic) with
    { Title = "соленые звезды", Artist = "гнилаялирика & aikko — ценные бумажки", Album = "", Duration = TimeSpan.FromSeconds(204) };
    using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { results = new[]
    {
        Catalog("соленые звезды", "Другой артист", "Другой альбом"),
        Catalog("убей во мне боль", "гнилаялирика", "ценные бумажки")
    } }));
    var cover = AppleArtworkResolver.MatchResults(raw, json.RootElement);
    Check(cover is not null, "Matching album must supply the cover");
    Equal("ценные бумажки", cover!.Album);
    Equal("https://music.apple.com/ru/album/test/123", cover.AppleMusicUrl);
    Check(cover.ImageUrl.EndsWith("/512x512bb.jpg"), "High resolution artwork");
    return Task.CompletedTask;
});
await Run("Cover matching rejects wrong artist, wrong version, malformed responses and unsafe image hosts", () =>
{
    var media = Media("AppleMusic.exe", MediaSource.AppleMusic) with { Title = "Солёные звёзды", Artist = "Artist", Album = "" };
    foreach (var result in new[]
    {
        Catalog("Соленые звезды", "Wrong Artist", "Album"),
        Catalog("Соленые звезды (Remix)", "Artist", "Album"),
        Catalog("Соленые звезды", "Artist", "Album", "https://attacker.invalid/a.jpg"),
        Catalog("Соленые звезды", "Artist", "Album", milliseconds: 900000)
    })
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { results = new[] { result } }));
        Equal<ArtworkMatch?>(null, AppleArtworkResolver.MatchResults(media, json.RootElement));
    }
    using var malformed = JsonDocument.Parse("[]");
    Equal<ArtworkMatch?>(null, AppleArtworkResolver.MatchResults(media, malformed.RootElement));
    using var correct = JsonDocument.Parse(JsonSerializer.Serialize(new { results = new[] { Catalog("соленые звезды", "Artist", "Album") } }));
    Check(AppleArtworkResolver.MatchResults(media, correct.RootElement) is not null, "Normalize ё and case");
    return Task.CompletedTask;
});
await Run("Artwork URL overrides fallback icon and links to Apple Music", () =>
{
    var cover = new ArtworkMatch("https://is1-ssl.mzstatic.com/cover.jpg", "Album", "https://music.apple.com/us/album/123");
    var presence = PresenceBuilder.Build(Media("AppleMusic.exe", MediaSource.AppleMusic), "fallback", cover);
    using var json = JsonDocument.Parse(JsonSerializer.Serialize(presence.ToActivity()));
    Equal(cover.ImageUrl, json.RootElement.GetProperty("assets").GetProperty("large_image").GetString());
    Equal(cover.AppleMusicUrl, json.RootElement.GetProperty("buttons")[0].GetProperty("url").GetString());
    Equal("fallback", PresenceBuilder.Build(Media("AppleMusic.exe", MediaSource.AppleMusic), "fallback").ImageKey);
    return Task.CompletedTask;
});
await Run("Artwork lookup caches hits and never searches browser metadata", async () =>
{
    var requests = 0;
    using var handler = new FakeHttpHandler(request =>
    {
        requests++;
        Check(request.RequestUri!.Host == "itunes.apple.com", "Apple search only");
        return new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { results = new[] { Catalog("Song", "Artist", "Album") } }))
        };
    });
    using var http = new HttpClient(handler);
    var resolver = new AppleArtworkResolver(http, requestInterval: TimeSpan.Zero);
    var media = Media("AppleMusic.exe", MediaSource.AppleMusic);
    Check((await resolver.ResolveAsync(media, default)).Match is not null, "Matched cover");
    await resolver.ResolveAsync(media, default);
    Equal(1, requests);
    Equal<ArtworkMatch?>(null, (await resolver.ResolveAsync(media with { Source = MediaSource.Browser }, default)).Match);
    Equal(1, requests);
});
await Run("Artwork lookup backs off on rate limit and network failure", async () =>
{
    foreach (var status in new[] { System.Net.HttpStatusCode.TooManyRequests, System.Net.HttpStatusCode.ServiceUnavailable })
    {
        var requests = 0;
        using var handler = new FakeHttpHandler(_ => { requests++; return new(status); });
        using var http = new HttpClient(handler);
        var resolver = new AppleArtworkResolver(http, requestInterval: TimeSpan.Zero);
        var media = Media("AppleMusic.exe", MediaSource.AppleMusic);
        var result = await resolver.ResolveAsync(media, default);
        Equal<ArtworkMatch?>(null, result.Match);
        Check(result.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(5) && result.ExpiresAt < DateTimeOffset.UtcNow.AddSeconds(40), "Backoff must not hide artwork for minutes");
        await resolver.ResolveAsync(media, default);
        Equal(status == System.Net.HttpStatusCode.TooManyRequests ? 1 : 4, requests);
    }
});

await Run("Artwork recovers from a failed storefront without waiting for a multi-minute negative cache", async () =>
{
    var requests = 0;
    using var handler = new FakeHttpHandler(request =>
    {
        requests++;
        Check(Uri.UnescapeDataString(request.RequestUri!.Query).Contains("term=Artist "), "Search must include the artist");
        return requests == 1 ? new(System.Net.HttpStatusCode.ServiceUnavailable)
            : new(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { results = new[] { Catalog("Song", "Artist", "Album") } })) };
    });
    using var http = new HttpClient(handler);
    var result = await new AppleArtworkResolver(http, requestInterval: TimeSpan.Zero).ResolveAsync(Media("AppleMusic.exe", MediaSource.AppleMusic), default);
    Check(result.Match is not null, "Second query must recover immediately");
    Equal(2, requests);
});

await Run("Artwork reuses a saved album after restart without sharing the previous song's URL", async () =>
{
    var folder = Path.Combine(Path.GetTempPath(), "synko-artwork-" + Guid.NewGuid().ToString("N"));
    var path = Path.Combine(folder, "catalog.json");
    var requests = 0;
    try
    {
        using var handler = new FakeHttpHandler(_ =>
        {
            requests++;
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { results = new[] { Catalog("Song", "Artist", "Album") } })) };
        });
        using var http = new HttpClient(handler);
        var first = Media("AppleMusic.exe", MediaSource.AppleMusic);
        var resolver = new AppleArtworkResolver(http, requestInterval: TimeSpan.Zero, cacheFile: path);
        var original = await resolver.ResolveAsync(first, default);
        var restarted = new AppleArtworkResolver(http, requestInterval: TimeSpan.Zero, cacheFile: path);
        var next = await restarted.ResolveAsync(first with { Title = "Another song" }, default);
        Equal(original.Match!.ImageUrl, next.Match!.ImageUrl);
        Equal<string?>(null, next.Match.AppleMusicUrl);
        Equal(1, requests);
        var other = await restarted.ResolveAsync(first with { Artist = "Different artist" }, default);
        Equal<ArtworkMatch?>(null, other.Match);
        Check(requests > 1, "Different artist must not inherit the cached album");
    }
    finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
});

await Run("Apple Retry-After applies across tracks and expires without a three-minute delay", async () =>
{
    var time = new ArtworkTestClock();
    var requests = 0;
    using var handler = new FakeHttpHandler(_ =>
    {
        requests++;
        if (requests == 1)
        {
            var limited = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new(TimeSpan.FromSeconds(12));
            return limited;
        }
        return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { results = new[] { Catalog("Song", "Artist", "Album") } })) };
    });
    using var http = new HttpClient(handler);
    var resolver = new AppleArtworkResolver(http, time, TimeSpan.Zero);
    var first = Media("AppleMusic.exe", MediaSource.AppleMusic);
    await resolver.ResolveAsync(first, default);
    await resolver.ResolveAsync(first with { Title = "Other" }, default);
    Equal(1, requests);
    time.Advance(TimeSpan.FromSeconds(13));
    Check((await resolver.ResolveAsync(first, default)).Match is not null, "Retry after server-requested delay");
    Equal(2, requests);
});
await Run("Artwork lookup does not attach a previous song's delayed result after switching or disabling", async () =>
{
    var resolver = new DelayedArtworkResolver();
    await using var coordinator = new ArtworkCoordinator(resolver);
    var first = Media("AppleMusic.exe", MediaSource.AppleMusic);
    var second = first with { Title = "Second" };
    Check(coordinator.Update(first, true)?.Match is null, "Initial lookup is pending");
    Check(coordinator.Update(second, true)?.Match is null, "New track cannot reuse old artwork");
    resolver.Complete(first, "old");
    Check(coordinator.Update(second, true)?.Match is null, "Late old result must be ignored");
    resolver.Complete(second, "new");
    // Completion continuations run asynchronously, so await the coordinator's
    // next observation without relying on a fixed timing delay.
    ArtworkResult? result = null;
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    while ((result = coordinator.Update(second, true))?.Match is null) { cts.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
    Equal("new", result.Match.ImageUrl);
    Equal<ArtworkResult?>(null, coordinator.Update(second, false));
    Equal<ArtworkResult?>(null, coordinator.Update(second with { Source = MediaSource.Browser }, true));
});

await Run("YouTube detection matches the current video including unread counts and browser suffixes", () =>
{
    const string video = "ОЧЕНЬ рекомендуем - Silver Pines (обзор)";
    Equal(MediaWebsite.YouTube, BrowserSiteClassifier.FromWindowTitle(video, "(372) " + video + " - YouTube - Google Chrome"));
    Equal(MediaWebsite.YouTube, BrowserSiteClassifier.FromWindowTitle(video, video + " - YouTube — Mozilla Firefox"));
    Equal(MediaWebsite.YouTube, BrowserSiteClassifier.FromWindowTitle(video, video + " - YouTube – Microsoft Edge"));
    Equal(MediaWebsite.YouTube, BrowserSiteClassifier.FromWindowTitle(video, "(378) " + video + " - YouTube: воспроизводится аудио"));
    Equal(MediaWebsite.YouTube, BrowserSiteClassifier.FromWindowTitle(video, video + " - YouTube: Audio playing"));
    Equal(MediaWebsite.Unknown, BrowserSiteClassifier.FromWindowTitle("Another playing video", video + " - YouTube - Google Chrome"));
    Equal(MediaWebsite.Unknown, BrowserSiteClassifier.FromWindowTitle(video, video + " - Netflix - Google Chrome"));
    Equal(MediaWebsite.Unknown, BrowserSiteClassifier.FromWindowTitle(video, video + " - YouTube Music - Google Chrome"));
    Equal(MediaWebsite.Unknown, BrowserSiteClassifier.FromWindowTitle("", video + " - YouTube"));
    return Task.CompletedTask;
});
await Run("Discord activity name follows YouTube, generic browser and Apple Music without renaming the application", () =>
{
    var browser = Media("chrome.exe", MediaSource.Browser) with { Website = MediaWebsite.YouTube };
    var youtube = PresenceBuilder.Build(browser, null);
    Equal(3, youtube.Type);
    Equal("видео на YouTube", youtube.ActivityName);
    using var json = JsonDocument.Parse(JsonSerializer.Serialize(youtube.ToActivity()));
    Equal("видео на YouTube", json.RootElement.GetProperty("name").GetString());
    Equal("Song", json.RootElement.GetProperty("details").GetString());
    var unknown = PresenceBuilder.Build(browser with { Website = MediaWebsite.Unknown }, null);
    Equal("видео в браузере", unknown.ActivityName);
    var apple = PresenceBuilder.Build(Media("AppleMusic.exe", MediaSource.AppleMusic), null);
    Equal("Apple Music", apple.ActivityName);
    Equal(2, apple.Type);
    return Task.CompletedTask;
});

await Run("YouTube URL parsing handles watch, shorts, live and shared links without leaking tracking parameters", () =>
{
    const string id = "Au6KnAm-Njc";
    foreach (var url in new[]
    {
        $"youtube.com/watch?v={id}&t=414s", $"https://www.youtube.com/watch?t=40&v={id}&list=secret",
        $"https://youtu.be/{id}?si=tracking", $"https://www.youtube.com/shorts/{id}",
        $"https://m.youtube.com/live/{id}", $"https://www.youtube-nocookie.com/embed/{id}"
    }) Equal(id, YouTubeVideo.IdFromUrl(url));
    Equal($"https://i.ytimg.com/vi/{id}/hqdefault.jpg", YouTubeVideo.ThumbnailUrl(id));
    Equal($"https://www.youtube.com/watch?v={id}", YouTubeVideo.WatchUrl(id));
    return Task.CompletedTask;
});
await Run("YouTube thumbnail rejects spoofed URLs, invalid IDs and ambiguous watch parameters", () =>
{
    foreach (var url in new[]
    {
        "https://youtube.com.attacker.invalid/watch?v=Au6KnAm-Njc", "https://attacker.invalid/youtube.com/watch?v=Au6KnAm-Njc",
        "https://user@youtube.com/watch?v=Au6KnAm-Njc", "https://www.youtube.com/watch?v=short",
        "https://www.youtube.com/watch?v=Au6KnAm-Njc&v=AAAAAAAAAAA", "https://www.youtube.com/channel/Au6KnAm-Njc",
        "javascript:alert(1)", "https://www.youtube.com/watch?v=../inject", ""
    }) Equal<string?>(null, YouTubeVideo.IdFromUrl(url));
    Equal<string?>(null, YouTubeVideo.ThumbnailUrl("../inject"));
    Equal<string?>(null, YouTubeVideo.ThumbnailUrl(null));
    return Task.CompletedTask;
});
await Run("Current video thumbnail replaces the fallback icon and switches cleanly to another video or Apple Music", () =>
{
    var video = Media("chrome.exe", MediaSource.Browser) with { Website = MediaWebsite.YouTube, VideoId = "Au6KnAm-Njc" };
    var presence = PresenceBuilder.Build(video, "apple-logo");
    Equal("https://i.ytimg.com/vi/Au6KnAm-Njc/hqdefault.jpg", presence.ImageKey);
    Equal("видео на YouTube", presence.ActivityName);
    Equal(video.Title, presence.ImageText);
    using var json = JsonDocument.Parse(JsonSerializer.Serialize(presence.ToActivity()));
    Equal("Открыть на YouTube", json.RootElement.GetProperty("buttons")[0].GetProperty("label").GetString());
    Equal("https://www.youtube.com/watch?v=Au6KnAm-Njc", json.RootElement.GetProperty("buttons")[0].GetProperty("url").GetString());
    var next = PresenceBuilder.Build(video with { VideoId = "AAAAAAAAAAA" }, "apple-logo");
    Check(next.ImageKey != presence.ImageKey, "Each video must use its own thumbnail");
    Equal("apple-logo", PresenceBuilder.Build(video with { VideoId = null }, "apple-logo").ImageKey);
    var apple = PresenceBuilder.Build(video with { Source = MediaSource.AppleMusic }, "apple-logo");
    Equal("apple-logo", apple.ImageKey);
    Equal<string?>(null, apple.YouTubeUrl);
    return Task.CompletedTask;
});

await Run("Automatic mode selects Chrome when Apple Music is paused and Windows has no current session", () =>
{
    var apple = Media("AppleMusic.exe", MediaSource.AppleMusic) with { IsPlaying = false, IsCurrent = false };
    var chrome = Media("chrome.exe", MediaSource.Browser) with { IsPlaying = true, IsCurrent = false };
    Equal(chrome, MediaSelector.Select([apple, chrome], SourceMode.Automatic, apple.TrackKey));
    Equal<MediaSnapshot?>(null, MediaSelector.Select([apple, chrome], SourceMode.AppleMusic, null));
    Equal(chrome, MediaSelector.Select([apple, chrome], SourceMode.Browser, null));
    foreach (var app in new[] { "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "vivaldi.exe", "opera.exe", "operagx.exe", "arc.exe", "yandex.exe", "chromium.exe", "thorium.exe", "librewolf.exe", "waterfox.exe", "floorp.exe", "zen.exe" })
        Equal(MediaSource.Browser, SourceClassifier.Classify(app).Source);
    return Task.CompletedTask;
});

await Run("Updater accepts only newer stable releases with the expected asset and SHA-256", () =>
{
    using var release = JsonDocument.Parse(JsonSerializer.Serialize(UpdateRelease()));
    var update = ReleaseUpdateClient.Parse(release.RootElement, "zephrx0/Synko", new Version(2, 2, 0));
    Equal(new Version(2, 3, 0), update!.Version);
    Equal<ReleaseUpdate?>(null, ReleaseUpdateClient.Parse(release.RootElement, "zephrx0/Synko", new Version(2, 3, 0)));
    using var draft = JsonDocument.Parse(JsonSerializer.Serialize(UpdateRelease(draft: true)));
    Equal<ReleaseUpdate?>(null, ReleaseUpdateClient.Parse(draft.RootElement, "zephrx0/Synko", new Version(2, 2, 0)));
    using var prerelease = JsonDocument.Parse(JsonSerializer.Serialize(UpdateRelease(prerelease: true)));
    Equal<ReleaseUpdate?>(null, ReleaseUpdateClient.Parse(prerelease.RootElement, "zephrx0/Synko", new Version(2, 2, 0)));
    Check(!ReleaseUpdateClient.IsRepository("https://example.com"), "Repository must be an owner/name pair");
    return Task.CompletedTask;
});

await Run("Updater rejects foreign download URLs, absent digests and oversized payloads", async () =>
{
    foreach (var metadata in new[] { UpdateRelease(url: "https://attacker.invalid/Synko.exe"), UpdateRelease(digest: ""), UpdateRelease(size: 300L * 1024 * 1024) })
    {
        using var release = JsonDocument.Parse(JsonSerializer.Serialize(metadata));
        await Throws<InvalidDataException>(() => Task.FromResult(ReleaseUpdateClient.Parse(release.RootElement, "zephrx0/Synko", new Version(2, 2, 0))));
    }
});

await Run("Updater download verifies bytes before replacing an existing staged file", async () =>
{
    var bytes = Encoding.UTF8.GetBytes("verified update fixture");
    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    using var http = new HttpClient(new FakeHttpHandler(_ => new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
    var client = new ReleaseUpdateClient(http, "zephrx0/Synko");
    var update = new ReleaseUpdate(new(2, 3, 0), new("https://github.com/zephrx0/Synko/releases/download/v2.3.0/Synko-2.3.0-win-x64.exe"), hash, bytes.Length, new("https://github.com/zephrx0/Synko/releases/tag/v2.3.0"));
    var path = Path.GetTempFileName();
    try
    {
        await client.DownloadAsync(update, path, null, default);
        Equal("verified update fixture", await File.ReadAllTextAsync(path));
        await File.WriteAllTextAsync(path, "previous payload");
        await Throws<InvalidDataException>(() => client.DownloadAsync(update with { Sha256 = new string('0', 64) }, path, null, default));
        Equal("previous payload", await File.ReadAllTextAsync(path));
        Check(!File.Exists(path + ".partial"), "Corrupt partial file must be removed");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Throws<OperationCanceledException>(() => client.DownloadAsync(update, path, null, cancelled.Token));
        Equal("previous payload", await File.ReadAllTextAsync(path));
    }
    finally { File.Delete(path); }
});

await Run("Updater distinguishes an unpublished repository and uses the latest-release API", async () =>
{
    using var missing = new HttpClient(new FakeHttpHandler(request =>
    {
        Equal("https://api.github.com/repos/zephrx0/Synko/releases/latest", request.RequestUri!.ToString());
        Check(request.Headers.UserAgent.ToString().Contains("Synko/"), "GitHub requires a User-Agent");
        return new(System.Net.HttpStatusCode.NotFound);
    }));
    await Throws<NoPublishedReleaseException>(() => new ReleaseUpdateClient(missing, "zephrx0/Synko").CheckAsync(new(2, 2, 0), default));
});

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

static object UpdateRelease(bool draft = false, bool prerelease = false, string? url = null, string? digest = null, long size = 1024)
    => new { tag_name = "v2.3.0", draft, prerelease, assets = new[] { new { name = "Synko-2.3.0-win-x64.exe", state = "uploaded", size,
        browser_download_url = url ?? "https://github.com/zephrx0/Synko/releases/download/v2.3.0/Synko-2.3.0-win-x64.exe",
        digest = digest ?? "sha256:" + new string('a', 64) } } };

async Task Run(string name, Func<Task> action)
{
    try { await action().WaitAsync(TimeSpan.FromSeconds(20)); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception ex) { Console.Error.WriteLine("FAIL " + name + ": " + ex); failed++; }
}
static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}");
static async Task<T> Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T ex) { return ex; }
    throw new Exception("Expected " + typeof(T).Name);
}
static MediaSnapshot Media(string appId, MediaSource source, bool current = false)
    => new(appId, "Source", source, "Song", "Artist", "Album", true, current,
        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(180), 1, DateTimeOffset.FromUnixTimeSeconds(1800000000));
static async Task WithPipe(Func<Stream, CancellationToken, Task> serverAction,
    Func<DiscordRpcClient, string, CancellationToken, Task> clientAction)
{
    var prefix = "nowplaying-test-" + Guid.NewGuid().ToString("N") + "-";
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    using var server = new NamedPipeServerStream(prefix + "0", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    await using var client = new DiscordRpcClient();
    var serverTask = Task.Run(async () =>
    {
        await server.WaitForConnectionAsync(cts.Token);
        await serverAction(server, cts.Token);
    });
    try { await Task.WhenAll(serverTask, clientAction(client, prefix, cts.Token)); }
    finally { await cts.CancelAsync(); }
}
static async Task Handshake(Stream server, CancellationToken token)
{
    var request = await RpcFrame.ReadAsync(server, token);
    Equal(0, request.Opcode);
    using var message = JsonDocument.Parse(request.Payload);
    Equal("123456789012345678", message.RootElement.GetProperty("client_id").GetString());
    await RpcFrame.WriteJsonAsync(server, 1, new { cmd = "DISPATCH", evt = "READY", data = new { v = 1 } }, token);
}
static Task Ack(Stream server, string nonce, CancellationToken token)
    => RpcFrame.WriteJsonAsync(server, 1, new { cmd = "SET_ACTIVITY", nonce, data = new { }, evt = (string?)null }, token);

static object Catalog(string title, string artist, string album,
    string image = "https://is1-ssl.mzstatic.com/image/thumb/cover.jpg/100x100bb.jpg", int milliseconds = 180000)
    => new
    {
        trackName = title, artistName = artist, collectionName = album, artworkUrl100 = image,
        trackTimeMillis = milliseconds, trackViewUrl = "https://music.apple.com/ru/album/test/123?i=456",
        collectionViewUrl = "https://music.apple.com/ru/album/test/123"
    };

sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(response(request));
}

sealed class ArtworkTestClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan elapsed) => _now += elapsed;
}

sealed class DelayedArtworkResolver : IArtworkResolver
{
    private readonly Dictionary<string, TaskCompletionSource<ArtworkResult>> _pending = new();
    public Task<ArtworkResult> ResolveAsync(MediaSnapshot media, CancellationToken cancellationToken)
    {
        var pending = new TaskCompletionSource<ArtworkResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[media.TrackKey] = pending;
        return pending.Task;
    }
    public void Complete(MediaSnapshot media, string image)
        => _pending[media.TrackKey].SetResult(new(new(image, "Album", null), "Found", DateTimeOffset.UtcNow.AddHours(1)));
}

sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => base.ReadAsync(buffer[..Math.Min(buffer.Length, 2)], cancellationToken);
}
