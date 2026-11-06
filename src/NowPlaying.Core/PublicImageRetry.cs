namespace NowPlaying.Core;

// An activity ACK can omit an image that Discord failed to resolve. Retry the
// current public URL with a bounded rate instead of treating it as delivered.
public sealed class PublicImageRetry
{
    private string? _url;
    private int _attempts;
    private DateTimeOffset _retryAt = DateTimeOffset.MaxValue;

    public void Observe(Presence? presence, bool? accepted, DateTimeOffset now)
    {
        if (accepted != false || !Uri.TryCreate(presence?.ImageKey, UriKind.Absolute, out var uri) || uri.Scheme != "https")
        { Reset(); return; }
        if (_url != presence!.ImageKey) _attempts = 0;
        _url = presence.ImageKey;
        _retryAt = now.AddSeconds(Math.Min(60, 10 << Math.Min(_attempts++, 3)));
    }

    public bool Due(Presence? desired, DateTimeOffset now) => _url is not null && desired?.ImageKey == _url && now >= _retryAt;
    public void Reset() { _url = null; _attempts = 0; _retryAt = DateTimeOffset.MaxValue; }
}
