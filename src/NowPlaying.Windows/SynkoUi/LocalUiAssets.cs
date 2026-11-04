using System.Reflection;
using System.Security.Cryptography;

namespace NowPlaying.Windows.SynkoUi;

internal static class LocalUiAssets
{
    private const string Prefix = "Synko.Ui.";

    public static string Extract()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var names = assembly.GetManifestResourceNames().Where(name => name.StartsWith(Prefix, StringComparison.Ordinal)).Order().ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in names)
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(name));
            using var source = assembly.GetManifestResourceStream(name)!;
            var buffer = new byte[8192];
            int count;
            while ((count = source.Read(buffer)) > 0) hash.AppendData(buffer.AsSpan(0, count));
        }
        var version = Convert.ToHexString(hash.GetHashAndReset())[..16];
        var folder = Path.Combine(SettingsStore.Folder, "ui", version);
        if (File.Exists(Path.Combine(folder, ".complete"))) return folder;
        Directory.CreateDirectory(folder);
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        foreach (var name in names)
        {
            var relative = name[Prefix.Length..].Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            var destination = Path.GetFullPath(Path.Combine(folder, relative));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid UI resource path.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var source = assembly.GetManifestResourceStream(name)!;
            using var target = File.Create(destination);
            source.CopyTo(target);
        }
        File.WriteAllText(Path.Combine(folder, ".complete"), version);
        return folder;
    }
}
