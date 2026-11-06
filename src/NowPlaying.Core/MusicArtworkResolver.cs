namespace NowPlaying.Core;

// Race a slow/limited primary catalogue against a bounded fallback. Both
// requests belong to this track: switching tracks cancels the entire lookup.
public sealed class MusicArtworkResolver(IArtworkResolver primary, IArtworkResolver fallback, TimeSpan? fallbackDelay = null) : IArtworkResolver
{
    public async Task<ArtworkResult> ResolveAsync(MediaSnapshot media, CancellationToken cancellationToken)
    {
        if (media.Source is not (MediaSource.AppleMusic or MediaSource.Spotify))
            return new(null, "Обложки музыки выключены для этого источника", DateTimeOffset.UtcNow.AddMinutes(1));
        using var lookup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lookup.CancelAfter(TimeSpan.FromSeconds(25));
        var first = SafeAsync(primary, media, lookup.Token);
        var delay = Task.Delay(fallbackDelay ?? TimeSpan.FromMilliseconds(900), lookup.Token);
        Task<ArtworkResult>? second = null;
        try
        {
            if (await Task.WhenAny(first, delay) == first && (await first).Match is not null) return await first;
            second = SafeAsync(fallback, media, lookup.Token);
            var pending = new List<Task<ArtworkResult>> { first, second };
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending).WaitAsync(lookup.Token);
                pending.Remove(done);
                var result = await done;
                if (result.Match is not null) return result;
            }
            return new(null, "Обложка пока не найдена; повторю поиск", DateTimeOffset.UtcNow.AddSeconds(15));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(null, "Каталоги временно недоступны; повторю поиск", DateTimeOffset.UtcNow.AddSeconds(10)); }
        finally
        {
            await lookup.CancelAsync();
            // Observe errors from the losing request without holding up the UI.
            _ = ObserveAsync(first);
            if (second is not null) _ = ObserveAsync(second);
        }
    }

    private static async Task<ArtworkResult> SafeAsync(IArtworkResolver resolver, MediaSnapshot media, CancellationToken token)
    {
        try { return await resolver.ResolveAsync(media, token); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        { return new(null, "Каталог временно недоступен", DateTimeOffset.UtcNow.AddSeconds(10)); }
    }

    private static async Task ObserveAsync(Task<ArtworkResult> task)
    { try { await task; } catch (Exception) { /* Optional, cancelled losing lookup. */ } }
}
