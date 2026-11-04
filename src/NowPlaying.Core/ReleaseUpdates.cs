using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace NowPlaying.Core;

public sealed record ReleaseUpdate(Version Version, Uri DownloadUrl, string Sha256, long Size, Uri ReleaseUrl);
public sealed class NoPublishedReleaseException() : IOException("No published release is available.");

public sealed class ReleaseUpdateClient(HttpClient http, string repository)
{
    public static bool IsRepository(string value) => System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z");

    public async Task<ReleaseUpdate?> CheckAsync(Version current, CancellationToken cancellationToken)
    {
        if (!IsRepository(repository)) throw new InvalidOperationException("Update repository is not configured.");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + repository + "/releases/latest");
        request.Headers.UserAgent.ParseAdd("Synko/" + current);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new NoPublishedReleaseException();
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(1024 * 1024, cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return Parse(json.RootElement, repository, current);
    }

    public static ReleaseUpdate? Parse(JsonElement release, string repository, Version current)
    {
        if (!IsRepository(repository) || release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v'), out var version) || version.Build < 0 || version <= current) return null;
        var expectedName = "Synko-" + version.ToString(3) + "-win-x64.exe";
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != expectedName || asset.GetProperty("state").GetString() != "uploaded") continue;
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            if (digest?.StartsWith("sha256:", StringComparison.Ordinal) != true || digest.Length != 71 || !digest.AsSpan(7).ToString().All(Uri.IsHexDigit))
                throw new InvalidDataException("The release has no valid SHA-256 digest.");
            var url = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            if (url.Scheme != "https" || url.Host != "github.com" || !url.AbsolutePath.StartsWith("/" + repository + "/releases/download/", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(url.UserInfo))
                throw new InvalidDataException("Unexpected release download URL.");
            var size = asset.GetProperty("size").GetInt64();
            if (size is <= 0 or > 256 * 1024 * 1024) throw new InvalidDataException("Unexpected update size.");
            return new(version, url, digest[7..], size, new Uri("https://github.com/" + repository + "/releases/tag/" + Uri.EscapeDataString(tag)));
        }
        return null;
    }

    public async Task DownloadAsync(ReleaseUpdate update, string destination, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var temporary = destination + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        try
        {
            using var response = await http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != update.Size) throw new InvalidDataException("Update size does not match release metadata.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                var buffer = new byte[64 * 1024]; long total = 0; int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += count;
                    if (total > update.Size) throw new InvalidDataException("Update exceeds the declared size.");
                    hash.AppendData(buffer.AsSpan(0, count));
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    progress?.Report((double)total / update.Size);
                }
                if (total != update.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(update.Sha256)))
                    throw new InvalidDataException("Update checksum does not match the release.");
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
