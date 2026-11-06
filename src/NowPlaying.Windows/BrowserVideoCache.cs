using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NowPlaying.Core;

namespace NowPlaying.Windows;

// Only the last known video per browser, with a hashed metadata key. This
// restores an already identified background tab after an app restart without
// storing a list of titles or reading browser history.
internal sealed class BrowserVideoCache(string path, TimeProvider? timeProvider = null)
{
    private sealed record Entry(string TrackHash, string VideoId, DateTimeOffset Expires);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _loaded;

    internal string? Find(string track)
    {
        Load();
        return _entries.TryGetValue(SourceKey(track), out var value) && value.Expires > _time.GetUtcNow() && value.TrackHash == Hash(track)
            ? value.VideoId : null;
    }

    internal void Remember(string track, string videoId)
    {
        if (!YouTubeVideo.IsValidId(videoId)) return;
        Load();
        var source = SourceKey(track);
        var hash = Hash(track);
        if (_entries.TryGetValue(source, out var old) && old.TrackHash == hash && old.VideoId == videoId && old.Expires > _time.GetUtcNow().AddHours(7)) return;
        _entries[source] = new(hash, videoId, _time.GetUtcNow().AddHours(8));
        while (_entries.Count > 8) _entries.Remove(_entries.MinBy(p => p.Value.Expires).Key);
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_entries));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { if (temporary is not null) try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    private void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16384) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path));
            if (data is null) return;
            foreach (var item in data.Where(p => p.Key.Length == 64 && p.Value is { TrackHash.Length: 64 } value
                && YouTubeVideo.IsValidId(value.VideoId) && value.Expires > _time.GetUtcNow() && value.Expires <= _time.GetUtcNow().AddHours(8)).Take(8)) _entries[item.Key] = item.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private static string SourceKey(string track) => Hash(track.Split('\n', 2)[0]);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
