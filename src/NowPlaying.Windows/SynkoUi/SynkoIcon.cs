using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace NowPlaying.Windows.SynkoUi;

internal static class SynkoIcon
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create()
    {
        using var bitmap = new Bitmap(64, 64);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.ScaleTransform(64f / 56, 64f / 56);
            graphics.TranslateTransform(4, 0);
            using var path = new GraphicsPath();
            path.AddBezier(38, 7, 28, 10, 9, 16, 9, 22);
            path.AddBezier(9, 22, 9, 27, 35, 27, 35, 33);
            path.AddBezier(35, 33, 35, 39, 17, 45, 8, 49);
            using var pen = new Pen(WindowsAppearance.Read().TaskbarDark ? Color.FromArgb(240, 240, 242) : Color.FromArgb(40, 40, 44), 6)
                { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            graphics.DrawPath(pen, path);
        }
        var handle = bitmap.GetHicon();
        try { using var icon = Icon.FromHandle(handle); return (Icon)icon.Clone(); }
        finally { DestroyIcon(handle); }
    }
}
