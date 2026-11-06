using System.Runtime.InteropServices;
using System.Text;

namespace NowPlaying.Windows;

internal static class WindowsRestart
{
    internal static string Arguments(bool quiet) => quiet ? "--startup --background" : "--startup";

    public static void Configure(bool enabled, bool quiet)
    {
        // Use the same explicit launch mode for Windows Run and Restart
        // Manager. Crash/hang recovery remains disabled; Windows controls
        // whether to restore the application after its own updates/sign-in.
        var result = enabled ? RegisterApplicationRestart(Arguments(quiet), 1 | 2) : UnregisterApplicationRestart();
        if (result < 0 && result != unchecked((int)0x80070490))
            throw new IOException("Не удалось сохранить режим восстановления Synko", Marshal.GetExceptionForHR(result));
    }

    internal static string? ReadArguments()
    {
        var text = new StringBuilder(1024);
        uint length = (uint)text.Capacity;
        var result = GetApplicationRestartSettings(new IntPtr(-1), text, ref length, out _);
        if (result == unchecked((int)0x80070490)) return null;
        Marshal.ThrowExceptionForHR(result);
        return text.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegisterApplicationRestart(string commandLine, uint flags);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int UnregisterApplicationRestart();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetApplicationRestartSettings(IntPtr process, StringBuilder commandLine, ref uint size, out uint flags);
}
