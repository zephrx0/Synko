using System.Diagnostics;
using System.Collections.Concurrent;
using Microsoft.Win32;
using NowPlaying.Core;

namespace NowPlaying.Windows;

internal static class BrowserIdentity
{
    internal static string? OverrideMozillaRegistryPath { get; set; }
    private static readonly object Sync = new();
    private static Dictionary<string, string> _modelIds = new(StringComparer.OrdinalIgnoreCase);
    private static DateTimeOffset _refreshAt;
    private static readonly ConcurrentDictionary<string, (string Product, DateTimeOffset Expires)> Products = new(StringComparer.OrdinalIgnoreCase);

    internal static string FromMetadata(string fallback, string path, string product)
    {
        var identity = path + " " + product;
        if (identity.Contains("Helium", StringComparison.OrdinalIgnoreCase)) return "Helium";
        if (identity.Contains("Opera GX", StringComparison.OrdinalIgnoreCase) || identity.Contains("operagx", StringComparison.OrdinalIgnoreCase)) return "Opera GX";
        if (identity.Contains("Yandex", StringComparison.OrdinalIgnoreCase)) return "Яндекс Браузер";
        if (identity.Contains("Zen Browser", StringComparison.OrdinalIgnoreCase) || product.Equals("Zen", StringComparison.OrdinalIgnoreCase)) return "Zen";
        foreach (var brand in new[] { "LibreWolf", "Waterfox", "Floorp", "Thorium", "Brave", "Vivaldi", "Firefox" })
            if (identity.Contains(brand, StringComparison.OrdinalIgnoreCase)) return brand;
        return fallback;
    }

    internal static string ForProcess(Process process, string fallback)
    {
        try
        {
            var path = process.MainModule?.FileName;
            if (path is null) return fallback;
            if (!Products.TryGetValue(path, out var product) || product.Expires <= DateTimeOffset.UtcNow)
            {
                product = (FileVersionInfo.GetVersionInfo(path).ProductName ?? "", DateTimeOffset.UtcNow.AddMinutes(5));
                if (Products.Count >= 64) Products.Clear();
                Products[path] = product;
            }
            return FromMetadata(fallback, path, product.Product);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return fallback; }
    }

    internal static (MediaSource Source, string Name) ResolveSession(string appId, string? mediaTitle = null)
    {
        var classified = SourceClassifier.Classify(appId);
        if (classified.Source != MediaSource.Other)
            return classified.Source == MediaSource.Browser ? (classified.Source, ForSession(classified.Name, mediaTitle)) : classified;
        // Gecko's Windows media ID can be an installation hash, not firefox.exe.
        // Resolve the actual installation registry rather than guessing that
        // every hexadecimal player ID means Firefox.
        if (OverrideMozillaRegistryPath is { } testPath)
        {
            using var key = Registry.CurrentUser.OpenSubKey(testPath);
            var model = ReadModels(key);
            if (model.TryGetValue(appId, out var browser)) return (MediaSource.Browser, browser);
        }
        else lock (Sync)
        {
            if (DateTimeOffset.UtcNow >= _refreshAt)
            {
                var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using var root = RegistryKey.OpenBaseKey(hive, view);
                        using var key = root.OpenSubKey(@"Software\Mozilla\Firefox\TaskBarIDs");
                        foreach (var pair in ReadModels(key)) models[pair.Key] = pair.Value;
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
                }
                _modelIds = models;
                _refreshAt = DateTimeOffset.UtcNow.AddMinutes(1);
            }
            if (_modelIds.TryGetValue(appId, out var browser)) return (MediaSource.Browser, browser);
        }
        return classified;
    }

    private static Dictionary<string, string> ReadModels(RegistryKey? key)
    {
        var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (key is null) return models;
        foreach (var install in key.GetValueNames())
            if (key.GetValue(install) is string id && id.Length is > 0 and < 256)
                models[id] = FromMetadata("Firefox", install, "");
        return models;
    }

    internal static string ForSession(string name, string? mediaTitle = null)
    {
        if (name is not ("Браузер" or "Opera" or "Google Chrome" or "Firefox")) return name;
        var processes = Process.GetProcessesByName(name == "Opera" ? "opera" : name == "Google Chrome" ? "chrome" : name == "Firefox" ? "firefox" : "browser");
        try
        {
            if (mediaTitle is not null)
            {
                var ids = processes.Select(p => (uint)p.Id).ToHashSet();
                var matchedNames = BrowserWebsiteDetector.ReadBrowserWindows().Where(w => ids.Contains(BrowserWebsiteDetector.WindowProcessId(w.Handle))
                    && BrowserSiteClassifier.FromWindowTitle(mediaTitle, w.Title) == MediaWebsite.YouTube).Select(w => w.Browser).Distinct().ToArray();
                if (matchedNames.Length == 1) return matchedNames[0];
            }
            var roots = processes.Where(process => process.MainWindowHandle != IntPtr.Zero).ToArray();
            var matched = mediaTitle is null ? [] : roots.Where(process => BrowserSiteClassifier.FromWindowTitle(mediaTitle, process.MainWindowTitle) == MediaWebsite.YouTube).ToArray();
            var identities = (matched.Length > 0 ? matched : roots).Select(root => ForProcess(root, name)).Distinct().ToArray();
            return identities.Length == 1 ? identities[0] : name;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return name; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
