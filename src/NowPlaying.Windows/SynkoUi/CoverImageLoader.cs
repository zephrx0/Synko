using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace NowPlaying.Windows.SynkoUi;

// Small, bounded local cache shared by consecutive tracks. No upload service is
// needed: Discord uses the public Apple/YouTube URL, the UI uses local bytes.
internal sealed class CoverImageLoader(HttpClient http, string cacheDirectory)
{
    private readonly ConcurrentDictionary<string, string> _memory = new();

    public async Task<string?> LoadAsync(string url, CancellationToken cancellationToken)
    {
        if (!PreviewSnapshot.IsCoverUrl(url)) return null;
        if (_memory.TryGetValue(url, out var cached)) return cached;
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))) + ".cover";
        var path = Path.Combine(cacheDirectory, name);
        try
        {
            var file = new FileInfo(path);
            if (file.Exists && file.Length is > 0 and <= 3 * 1024 * 1024
                && file.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-7))
            {
                var data = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                if (data.StartsWith("data:image/", StringComparison.Ordinal) && data.Contains(";base64,", StringComparison.Ordinal))
                {
                    Remember(url, data);
                    return data;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var cover = await PreviewSnapshot.LoadCoverAsync(http, url, cancellationToken).ConfigureAwait(false);
        if (cover is null) return null;
        Remember(url, cover);
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            await File.WriteAllTextAsync(path + ".tmp", cover, cancellationToken).ConfigureAwait(false);
            File.Move(path + ".tmp", path, overwrite: true);
            foreach (var old in new DirectoryInfo(cacheDirectory).EnumerateFiles("*.cover")
                .OrderByDescending(f => f.LastWriteTimeUtc).Skip(32)) old.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return cover;
    }

    private void Remember(string url, string cover)
    {
        if (_memory.Count >= 32) _memory.Clear();
        _memory[url] = cover;
    }
}
