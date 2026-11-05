namespace NowPlaying.Windows;

internal static class NativeArtwork
{
    public static string? ToDataUrl(byte[] bytes)
    {
        // Apple Music can report "image/jpeg,image/jpe,image/jpg". In a data
        // URL that comma terminates the header, so the image silently fails.
        // Identify the actual file instead of copying a player's MIME string.
        var data = bytes.AsSpan();
        var type = data.StartsWith(new byte[] { 0xff, 0xd8, 0xff }) ? "image/jpeg"
            : data.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }) ? "image/png"
            : data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8) ? "image/gif"
            : data.StartsWith("BM"u8) ? "image/bmp"
            : data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8) ? "image/webp"
            : null;
        return type is null ? null : "data:" + type + ";base64," + Convert.ToBase64String(bytes);
    }
}
