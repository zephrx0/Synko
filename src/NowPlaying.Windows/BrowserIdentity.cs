using System.Diagnostics;

namespace NowPlaying.Windows;

internal static class BrowserIdentity
{
    internal static string FromMetadata(string fallback, string path, string product)
    {
        var identity = path + " " + product;
        if (identity.Contains("Opera GX", StringComparison.OrdinalIgnoreCase) || identity.Contains("operagx", StringComparison.OrdinalIgnoreCase)) return "Opera GX";
        if (identity.Contains("Yandex", StringComparison.OrdinalIgnoreCase)) return "Яндекс Браузер";
        return fallback;
    }

    internal static string ForProcess(Process process, string fallback)
    {
        if (fallback is not ("Браузер" or "Opera")) return fallback;
        try
        {
            var path = process.MainModule?.FileName;
            return path is null ? fallback : FromMetadata(fallback, path, FileVersionInfo.GetVersionInfo(path).ProductName ?? "");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return fallback; }
    }

    internal static string ForSession(string name)
    {
        if (name is not ("Браузер" or "Opera")) return name;
        var processes = Process.GetProcessesByName(name == "Opera" ? "opera" : "browser");
        try
        {
            var root = processes.FirstOrDefault(process => process.MainWindowHandle != IntPtr.Zero);
            return root is null ? name : ForProcess(root, name);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return name; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
