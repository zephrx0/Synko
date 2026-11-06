using NowPlaying.Core;
using NowPlaying.Windows;

namespace NowPlaying.Windows.Tests;

internal static class BrowserChecks
{
    public static void Run()
    {
        Check(BrowserIdentity.FromMetadata("Google Chrome", @"C:\Apps\Helium\chrome.exe", "Helium") == "Helium", "Helium's chrome.exe needs its own process identity");
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
        Console.WriteLine("PASS Helium process identity and native accessibility address detection with renamed toolbar and unrelated-tab rejection");
    }

    private static void PumpUntil(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
        Check(task.IsCompleted, "Browser accessibility read timed out");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
