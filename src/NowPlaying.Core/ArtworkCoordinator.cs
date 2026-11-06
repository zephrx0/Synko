namespace NowPlaying.Core;

// Polling media and clearing Discord must remain responsive while the network
// lookup runs. Results from a previous track are never attached to a new one.
public sealed class ArtworkCoordinator(IArtworkResolver resolver, TimeProvider? timeProvider = null, TimeSpan? settleDelay = null) : IAsyncDisposable
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private CancellationTokenSource? _lookupCancellation;
    private Task<ArtworkResult>? _lookup;
    private string? _track;

    public ArtworkResult? Update(MediaSnapshot? media, bool enabled)
    {
        var key = enabled && media?.Source is MediaSource.AppleMusic or MediaSource.Spotify ? media.TrackKey : null;
        if (key != _track || (_lookup?.IsCompletedSuccessfully == true && _lookup.Result.ExpiresAt <= _time.GetUtcNow()))
        {
            _lookupCancellation?.Cancel();
            _lookupCancellation?.Dispose();
            _lookupCancellation = null;
            _lookup = null;
            _track = key;
        }
        if (key is null) return null;
        if (_lookup is null)
        {
            _lookupCancellation = new();
            _lookup = LookupAsync(media!, _lookupCancellation.Token);
        }
        if (_lookup.IsCompletedSuccessfully) return _lookup.Result;
        return new(null, "Поиск обложки альбома в Apple Music…", DateTimeOffset.MaxValue);
    }

    private async Task<ArtworkResult> LookupAsync(MediaSnapshot media, CancellationToken cancellationToken)
    {
        try
        {
            if (settleDelay is { } delay && delay > TimeSpan.Zero) await Task.Delay(delay, _time, cancellationToken);
            return await resolver.ResolveAsync(media, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new(null, "Поиск обложки отменён", _time.GetUtcNow()); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or ArgumentException)
        { return new(null, "Не удалось найти обложку; повторю поиск", _time.GetUtcNow().AddSeconds(10)); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_lookupCancellation is not null) await _lookupCancellation.CancelAsync();
        if (_lookup is not null) await _lookup;
        _lookupCancellation?.Dispose();
    }
}
