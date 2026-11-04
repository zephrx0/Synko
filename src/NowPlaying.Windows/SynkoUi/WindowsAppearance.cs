using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace NowPlaying.Windows.SynkoUi;

internal sealed record WindowsAppearance(bool Dark, bool TaskbarDark, string Accent, bool HighContrast, bool ReducedMotion)
{
    public static WindowsAppearance Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var settings = new UISettings();
        var color = settings.GetColorValue(UIColorType.Accent);
        return new(key?.GetValue("AppsUseLightTheme") is int apps && apps == 0,
            key?.GetValue("SystemUsesLightTheme") is int taskbar && taskbar == 0,
            $"#{color.R:x2}{color.G:x2}{color.B:x2}", SystemInformation.HighContrast, !settings.AnimationsEnabled);
    }
}
