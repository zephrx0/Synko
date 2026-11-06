using System.Text.Json;
using NowPlaying.Core;

namespace NowPlaying.Windows;

internal sealed class PlaybackDiagnostics
{
    private string? _last;
    public void Write(MediaSnapshot? selected, Presence? desired, Presence? published, bool connected, bool? imageAccepted)
    {
        // Keep only the current delivery state locally. No image bytes,
        // listening history, application credentials or automatic uploads.
        var state = new
        {
            version = Application.ProductVersion.Split('+')[0],
            sourceAppId = selected?.AppId, browser = selected?.SourceName, source = selected?.Source.ToString(),
            website = selected?.Website.ToString(), videoId = selected?.VideoId,
            nativeArtworkAvailable = selected?.ThumbnailData is not null,
            requestedImage = desired?.ImageKey, publishedImage = published?.ImageKey,
            discordConnected = connected,
            discordImageAcknowledged = desired?.ImageKey == published?.ImageKey ? imageAccepted : null,
            publicVideoUrlAvailable = selected?.Website == MediaWebsite.YouTube ? selected.VideoId is not null : (bool?)null,
            lastBrowserProbe = BrowserAddressProbe.LastDiagnostic ?? BrowserAddressReader.LastDiagnostic
        };
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        if (json == _last) return;
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            var path = Path.Combine(SettingsStore.Folder, "playback-diagnostic.json");
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, overwrite: true);
            _last = json;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
