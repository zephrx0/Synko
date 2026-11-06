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
            Check(WindowsRestart.ReadArguments() is null, "Disabling startup must remove this process's restart registration");
            StartupRegistration.SetEnabled(true, quiet: true);
            using (var key = Registry.CurrentUser.OpenSubKey(registryPath))
                Check((string?)key?.GetValue("Synko") == $"\"{Environment.ProcessPath}\" --startup --background", "Quiet autostart must explicitly contain the background argument");
            Check(WindowsRestart.ReadArguments() == "--startup --background", "Windows restoration must use the quiet command, not an empty/manual launch");
            Check(NowPlaying.Windows.Program.IsBackgroundLaunch(WindowsRestart.ReadArguments()!.Split(' '), settings), "The registered restart command must remain in the tray");
            StartupRegistration.SetEnabled(true, quiet: false);
            Check(WindowsRestart.ReadArguments() == "--startup", "Opting out must refresh the Windows restart command immediately");
            StartupRegistration.SetEnabled(false);

            using var signal = new EventWaitHandle(false, EventResetMode.AutoReset);
            using var context = new TrayApplication(new AppSettings { Enabled = false, AutoCheckUpdates = false, OnboardingDone = false }, signal, background: true);
            using var timer = new System.Windows.Forms.Timer { Interval = 100 };
            Exception? loopFailure = null;
            var shown = false;
            context.Window.Shown += (_, _) => shown = true;
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                try
                {
                    Check(!context.Window.Visible && !shown, "Application.Run exposed the background window after the constructor");
                    await Task.Delay(1000);
                    Check(!context.Window.Visible && !shown && !context.Window.Ready.IsCompleted, "Background startup must not show or initialise WebView after the message loop starts");
                }
                catch (Exception ex) { loopFailure = ex; }
                finally { await context.ExitAsync(); }
            };
            timer.Start();
            Application.Run(context);
            if (loopFailure is not null) throw new InvalidOperationException("Background message loop regression", loopFailure);
            Console.WriteLine("PASS quiet startup defaults, opt-out persistence, explicit Run/restart commands, native Windows registration and hidden onboarding");
        }
        finally
        {
            WindowsRestart.Configure(false, true);
            SettingsStore.OverrideFolder = previousFolder;
            StartupRegistration.OverrideRegistryPath = previousRegistry;
            Registry.CurrentUser.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: false);
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* A failed startup may have started WebView before this regression check. */ }
        }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
