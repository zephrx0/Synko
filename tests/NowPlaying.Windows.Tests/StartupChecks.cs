using Microsoft.Win32;
using NowPlaying.Windows;

namespace NowPlaying.Windows.Tests;

internal static class StartupChecks
{
    public static void Run()
    {
        var previousFolder = SettingsStore.OverrideFolder;
        var previousRegistry = StartupRegistration.OverrideRegistryPath;
        var folder = Path.Combine(Path.GetTempPath(), "synko-startup-" + Guid.NewGuid().ToString("N"));
        var registryPath = @"Software\Synko.Tests\" + Guid.NewGuid().ToString("N");
        SettingsStore.OverrideFolder = folder;
        StartupRegistration.OverrideRegistryPath = registryPath;
        try
        {
            Check(SettingsStore.Load().QuietStartup, "Fresh installs must default to quiet startup");
            Directory.CreateDirectory(folder);
            File.WriteAllText(SettingsStore.FilePath, "{\"ApplicationId\":\"123456789012345678\",\"SourceMode\":4}");
            var settings = SettingsStore.Load();
            Check(settings.QuietStartup && settings.SourceMode == NowPlaying.Core.SourceMode.Spotify, "Existing profiles must default to quiet startup without losing preferences");
            SettingsStore.Save(settings with { QuietStartup = false });
            Check(!SettingsStore.Load().QuietStartup, "Explicitly disabling quiet startup must persist");
            Check(NowPlaying.Windows.Program.IsBackgroundLaunch(["--startup"], settings), "Startup should hide the window by default");
            Check(!NowPlaying.Windows.Program.IsBackgroundLaunch(["--startup"], settings with { QuietStartup = false }), "Startup should honour opting out");
            Check(!NowPlaying.Windows.Program.IsBackgroundLaunch([], settings), "Manual launches should open the window");
            Check(NowPlaying.Windows.Program.IsBackgroundLaunch(["--background"], settings with { QuietStartup = false }), "Legacy explicit background launches must remain quiet");
            Check(!StartupRegistration.IsEnabled(), "Quiet preference must not enable Windows autostart");
            StartupRegistration.Migrate();
            Check(!StartupRegistration.IsEnabled(), "Migration must preserve disabled autostart");
            using (var key = Registry.CurrentUser.CreateSubKey(registryPath)) key.SetValue("NowPlaying", "old.exe --background");
            StartupRegistration.Migrate();
            using (var key = Registry.CurrentUser.OpenSubKey(registryPath))
            {
                Check((string?)key?.GetValue("Synko") == $"\"{Environment.ProcessPath}\" --startup", "Migration must register the current path and preference-aware startup argument");
                Check(key?.GetValue("NowPlaying") is null, "Migration must remove legacy entries");
            }
            StartupRegistration.SetEnabled(false);
            Check(!StartupRegistration.IsEnabled(), "Autostart must still be removable");

            using var signal = new EventWaitHandle(false, EventResetMode.AutoReset);
            using var context = new TrayApplication(new AppSettings { Enabled = false, AutoCheckUpdates = false, OnboardingDone = false }, signal, background: true);
            try { Check(!context.Window.Visible, "Background launch must not expose unfinished onboarding"); }
            finally
            {
                var exit = context.ExitAsync();
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!exit.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
                Check(exit.IsCompleted, "Background service did not stop");
                exit.GetAwaiter().GetResult();
            }
            Console.WriteLine("PASS quiet startup defaults, opt-out persistence, launch policy, registry migration and hidden onboarding");
        }
        finally
        {
            SettingsStore.OverrideFolder = previousFolder;
            StartupRegistration.OverrideRegistryPath = previousRegistry;
            Registry.CurrentUser.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: false);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
