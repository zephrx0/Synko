using System.Text.Json;
using Microsoft.Win32;
using NowPlaying.Core;

namespace NowPlaying.Windows;

internal sealed record AppSettings
{
    public string ApplicationId { get; init; } = "";
    public SourceMode SourceMode { get; init; } = SourceMode.Automatic;
    public bool Enabled { get; init; } = true;
    public string ImageKey { get; init; } = "";
    public bool AutomaticArtwork { get; init; } = true;
    public bool YouTubeThumbnails { get; init; } = true;
    public string ThemeMode { get; init; } = "system";
    public string ColorSource { get; init; } = "artwork";
    public string? CustomSeed { get; init; }
    public bool OnboardingDone { get; init; }
    public int OnboardingStep { get; init; }
    public bool AutoCheckUpdates { get; init; } = true;
    public bool QuietStartup { get; init; } = true;
}

internal static class SettingsStore
{
    internal static string? OverrideFolder { get; set; }
    public static string Folder => OverrideFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NowPlaying");
    public static string FilePath => Path.Combine(Folder, "settings.json");
    public static string? LoadWarning { get; private set; }
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var contents = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(contents) ?? new();
            if (!Enum.IsDefined(settings.SourceMode)) settings = settings with { SourceMode = SourceMode.Automatic };
            if (settings.ThemeMode is not ("system" or "light" or "dark")) settings = settings with { ThemeMode = "system" };
            if (settings.ColorSource is not ("artwork" or "windows" or "custom")) settings = settings with { ColorSource = "artwork" };
            using var document = JsonDocument.Parse(contents);
            if (!document.RootElement.TryGetProperty(nameof(AppSettings.OnboardingDone), out _) && DiscordRpcClient.IsValidApplicationId(settings.ApplicationId))
                settings = settings with { OnboardingDone = true };
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadWarning = "Не удалось прочитать настройки: " + ex.Message;
            return new();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Folder);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, overwrite: true);
    }
}

internal static class StartupRegistration
{
    internal static string? OverrideRegistryPath { get; set; }
    private static string RegistryPath => OverrideRegistryPath ?? @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Synko";
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        return key?.GetValue(ValueName) is string || key?.GetValue("NowPlaying") is string || key?.GetValue("Now Playing") is string;
    }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --startup");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
        key.DeleteValue("NowPlaying", throwOnMissingValue: false);
        key.DeleteValue("Now Playing", throwOnMissingValue: false);
    }

    public static void Migrate()
    {
        if (IsEnabled()) SetEnabled(true);
    }
}
