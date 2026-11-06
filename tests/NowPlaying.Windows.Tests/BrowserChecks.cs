using NowPlaying.Core;
using NowPlaying.Windows;
using Microsoft.Win32;
using System.Text.Json;

namespace NowPlaying.Windows.Tests;

internal static class BrowserChecks
{
    public static void Run()
    {
        var registryPath = @"Software\Synko.Tests\" + Guid.NewGuid().ToString("N");
        var previous = BrowserIdentity.OverrideMozillaRegistryPath;
        BrowserIdentity.OverrideMozillaRegistryPath = registryPath;
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(registryPath))
            {
                key.SetValue(@"C:\Program Files\Mozilla Firefox", "308046B0AF4A39CB");
                key.SetValue(@"C:\Program Files\Zen Browser", "F0DC299D809B9700");
            }
            Check(BrowserIdentity.ResolveSession("308046B0AF4A39CB") == (MediaSource.Browser, "Firefox"), "Firefox's real installation ID must not be published as a player name");
            Check(BrowserIdentity.ResolveSession("F0DC299D809B9700") == (MediaSource.Browser, "Zen"), "Gecko forks must use their installation identity too");
            Check(BrowserIdentity.ResolveSession("0123456789ABCDEF").Source == MediaSource.Other, "Unknown hexadecimal players must not be guessed as Firefox");
        }
        finally { BrowserIdentity.OverrideMozillaRegistryPath = previous; Registry.CurrentUser.DeleteSubKeyTree(registryPath, false); }
        Check(BrowserIdentity.FromMetadata("Google Chrome", @"C:\Apps\Helium\chrome.exe", "Helium") == "Helium", "Helium's chrome.exe needs its own process identity");
        var now = DateTimeOffset.UtcNow;
        var firefox = new MediaSnapshot("308046B0AF4A39CB", "Firefox", MediaSource.Browser, "Firefox video", "Channel", "", true, false, TimeSpan.Zero, TimeSpan.FromMinutes(3), 1, now);
        var helium = firefox with { AppId = "Helium.test", SourceName = "Helium", Title = "Helium video" };
        var lookedUp = new List<string>();
        using (var detector = new BrowserWebsiteDetector(windowReader: _ => [("Firefox", "Firefox video - YouTube - Mozilla Firefox", new IntPtr(1)), ("Helium", "Helium video - YouTube - Helium", new IntPtr(2))],
            addressReader: (window, title, _) => { lookedUp.Add(title); return Task.FromResult(new BrowserAddressReader.Result(MediaWebsite.YouTube, window == new IntPtr(1) ? null : "Au6KnAm-Njc")); }))
        {
            detector.Enrich([firefox, helium]);
            detector.Enrich([firefox, helium]);
            var enriched = detector.Enrich([firefox, helium]);
            Check(lookedUp.SequenceEqual(["Firefox video", "Helium video"]), "A browser without a readable URL must not monopolise the lookup worker");
            Check(enriched[1].VideoId == "Au6KnAm-Njc", "The other browser must receive its own public video ID");
        }
        var cancelled = false;
        var stopping = new BrowserWebsiteDetector(windowReader: _ => [("Firefox", "Firefox video - YouTube - Mozilla Firefox", new IntPtr(1))],
            addressReader: async (_, _, token) =>
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false); }
                finally { cancelled = true; }
                return new(MediaWebsite.Unknown, null);
            });
        stopping.Enrich([firefox]);
        stopping.DisposeAsync().GetAwaiter().GetResult();
        Check(cancelled, "Shutdown/update must wait for its browser lookup to stop");
        var previousFolder = SettingsStore.OverrideFolder;
        var diagnosticFolder = Path.Combine(Path.GetTempPath(), "synko-browser-diagnostic-" + Guid.NewGuid().ToString("N"));
        SettingsStore.OverrideFolder = diagnosticFolder;
        try
        {
            var localOnly = firefox with { Title = "Private media title must not be logged", Website = MediaWebsite.YouTube, ThumbnailData = "data:image/png;base64,local-only" };
            new PlaybackDiagnostics().Write(localOnly, PresenceBuilder.Build(localOnly, null), null, true, null);
            var text = File.ReadAllText(Path.Combine(diagnosticFolder, "playback-diagnostic.json"));
            using var diagnostic = JsonDocument.Parse(text);
            Check(diagnostic.RootElement.GetProperty("nativeArtworkAvailable").GetBoolean() && !diagnostic.RootElement.GetProperty("publicVideoUrlAvailable").GetBoolean(), "Local artwork must not be confused with a public Discord preview");
            Check(!text.Contains(localOnly.Title) && !text.Contains("data:image"), "Diagnostic must not store the media title or image bytes");
        }
        finally { SettingsStore.OverrideFolder = previousFolder; Directory.Delete(diagnosticFolder, recursive: true); }
        foreach (var name in new[] { "Search or enter URL", "Localised custom address field" })
        {
            using var form = new Form { Text = "Fixture video - YouTube - Helium", StartPosition = FormStartPosition.Manual, Location = new(-3000, -3000), ShowInTaskbar = false };
            using var toolbar = new ToolStrip { AccessibleRole = AccessibleRole.ToolBar };
            var address = new ToolStripTextBox { AccessibleName = name, Text = "https://www.youtube.com/watch?v=Au6KnAm-Njc", Width = 350 };
            toolbar.Items.Add(address);
            var focus = new Button { Text = "Focus outside address", Dock = DockStyle.Bottom };
            form.Controls.Add(toolbar); form.Controls.Add(focus);
            form.Show(); focus.Focus();
            var lookup = Task.Run(() => BrowserAddressReader.Read(form.Handle, "Fixture video"));
            PumpUntil(lookup);
            var result = lookup.GetAwaiter().GetResult();
            Check(result is { Website: MediaWebsite.YouTube, VideoId: "Au6KnAm-Njc" }, "Address detection failed for " + name + ": " + BrowserAddressReader.LastDiagnostic);
            var unrelated = Task.Run(() => BrowserAddressReader.Read(form.Handle, "Different background video"));
            PumpUntil(unrelated);
            Check(unrelated.GetAwaiter().GetResult().VideoId is null, "An unrelated active tab must not supply another video's thumbnail");
        }
        using (var form = new Form { Text = "Fixture video - YouTube - Mozilla Firefox", ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new(-3000, -3000) })
        {
            var toolbar = new ToolStrip();
            toolbar.Items.Add(new ToolStripComboBox { AccessibleName = "Custom Firefox address", Text = "https://www.youtube.com/watch?v=Au6KnAm-Njc", Width = 350 });
            var focus = new Button { Text = "Outside address", Dock = DockStyle.Bottom };
            form.Controls.Add(toolbar); form.Controls.Add(focus); form.Show(); focus.Focus();
            var lookup = Task.Run(() => BrowserAddressReader.Read(form.Handle, "Fixture video"));
            PumpUntil(lookup);
            Check(lookup.GetAwaiter().GetResult().VideoId == "Au6KnAm-Njc", "A Gecko combo-box address must not be ignored");
        }
        using (var form = new Form { Text = "Fixture video - YouTube - Helium", ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new(-3000, -3000) })
        {
            var toolbar = new ToolStrip();
            toolbar.Items.Add(new ToolStripTextBox { AccessibleName = "Search or enter URL", Text = "https://www.youtube.com/watch?v=Au6KnAm-Njc", Width = 350 });
            var focus = new Button { Text = "Outside address", Dock = DockStyle.Bottom };
            form.Controls.Add(toolbar); form.Controls.Add(focus);
            Exception? failure = null;
            form.Shown += async (_, _) =>
            {
                try
                {
                    focus.Focus();
                    var result = await BrowserAddressProbe.ReadAsync(form.Handle, "Fixture video", default);
                    Check(result.VideoId == "Au6KnAm-Njc", "The bounded child process must preserve the native result: " + BrowserAddressProbe.LastDiagnostic);
                }
                catch (Exception ex) { failure = ex; }
                finally { form.Close(); }
            };
            Application.Run(form);
            if (failure is not null) throw failure;
        }
        Console.WriteLine("PASS Helium process identity and native accessibility address detection with renamed toolbar and unrelated-tab rejection");
    }

    private static void PumpUntil(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(7);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
        Check(task.IsCompleted, "Browser accessibility read timed out");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
