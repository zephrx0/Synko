using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace NowPlaying.Windows.SynkoUi;

internal sealed class SynkoWindow : Form
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private CoreWebView2Controller? _controller;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string? _forcedTheme;
    private double _scale;
    private readonly bool _preview;
    private WindowsAppearance _appearance;
    private bool _loaded;
    private bool _canExit;
    private object? _latestState;
    public Task Ready => _ready.Task;
    public CoreWebView2 Web => _controller?.CoreWebView2 ?? throw new InvalidOperationException("WebView is not initialized.");
    public event Action<JsonElement>? CommandReceived;
    public event Action? AppearanceChanged;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    public SynkoWindow(string? forcedTheme = null, double scale = 1)
    {
        _forcedTheme = forcedTheme;
        _preview = forcedTheme is not null;
        _scale = _preview ? scale : DeviceDpi / 96d;
        _appearance = WindowsAppearance.Read();
        Text = "Synko";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new((int)(440 * _scale), (int)(488 * _scale));
        StartPosition = FormStartPosition.CenterScreen;
        Opacity = 0;
        BackColor = SystemColors.Window;
        Shown += async (_, _) => await InitializeAsync();
        VisibleChanged += async (_, _) => await UpdateVisibilityAsync();
        Resize += async (_, _) =>
        {
            if (_controller is not null) _controller.Bounds = ClientRectangle;
            await UpdateVisibilityAsync();
        };
        DpiChanged += (_, e) =>
        {
            if (_preview) return;
            _scale = e.DeviceDpiNew / 96d;
            if (_controller is not null) { _controller.RasterizationScale = _scale; _controller.Bounds = ClientRectangle; }
        };
    }

    private async Task InitializeAsync()
    {
        try
        {
            var folder = LocalUiAssets.Extract();
            if (!_preview)
            {
                _scale = DeviceDpi / 96d;
                ClientSize = new((int)(440 * _scale), (int)(488 * _scale));
            }
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(SettingsStore.Folder, "webview"));
            _controller = await environment.CreateCoreWebView2ControllerAsync(Handle);
            _controller.ShouldDetectMonitorScaleChanges = false;
            _controller.RasterizationScale = _scale;
            _controller.BoundsMode = CoreWebView2BoundsMode.UseRawPixels;
            _controller.Bounds = ClientRectangle;
            Web.Settings.AreDefaultContextMenusEnabled = false;
            Web.Settings.AreDevToolsEnabled = false;
            Web.Settings.IsStatusBarEnabled = false;
            Web.Settings.IsZoomControlEnabled = false;
            Web.Settings.AreBrowserAcceleratorKeysEnabled = false;
            Web.SetVirtualHostNameToFolderMapping("synko.local", folder, CoreWebView2HostResourceAccessKind.DenyCors);
            Web.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("https://synko.local/", StringComparison.Ordinal)) e.Cancel = true; };
            Web.NewWindowRequested += (_, e) => e.Handled = true;
            Web.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            Web.WebMessageReceived += OnMessage;
            await Web.AddScriptToExecuteOnDocumentCreatedAsync("window.addEventListener('error',e=>chrome.webview.postMessage({type:'fault',message:e.message+' '+e.filename}));window.addEventListener('unhandledrejection',e=>chrome.webview.postMessage({type:'fault',message:String(e.reason)}));");
            await Web.AddScriptToExecuteOnDocumentCreatedAsync("window.synkoEnvironment=" + JsonSerializer.Serialize(EnvironmentMessage(), Json) + ";");
            await Web.AddScriptToExecuteOnDocumentCreatedAsync("window.synkoInitialMessage=" + JsonSerializer.Serialize(_latestState, Json) + ";");
            Web.Navigate("https://synko.local/index.html");
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
            Opacity = 1;
            MessageBox.Show("Не удалось выполнить действие\n\n" + ex.Message, "Synko", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private object EnvironmentMessage() => new
    {
        dark = _forcedTheme switch { "dark" => true, "light" => false, _ => _appearance.Dark },
        _appearance.Accent, _appearance.HighContrast, _appearance.ReducedMotion
    };

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!args.Source.StartsWith("https://synko.local/", StringComparison.Ordinal)) return;
        using var message = JsonDocument.Parse(args.WebMessageAsJson);
        var root = message.RootElement;
        switch (root.GetProperty("type").GetString())
        {
            case "palette":
                BackColor = _appearance.HighContrast ? SystemColors.Window : ColorTranslator.FromHtml(root.GetProperty("surface").GetString()!);
                _controller!.DefaultBackgroundColor = BackColor;
                var dark = root.GetProperty("dark").GetBoolean() ? 1 : 0;
                DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
                var color = ColorTranslator.ToWin32(BackColor);
                DwmSetWindowAttribute(Handle, 35, ref color, sizeof(int));
                var foreground = ColorTranslator.ToWin32(_appearance.HighContrast ? SystemColors.WindowText : ColorTranslator.FromHtml(root.GetProperty("foreground").GetString()!));
                DwmSetWindowAttribute(Handle, 36, ref foreground, sizeof(int));
                break;
            case "ready":
                _loaded = true;
                Opacity = 1;
                if (_latestState is not null) Send(_latestState);
                _ready.TrySetResult();
                break;
            case "fault":
                _ready.TrySetException(new InvalidOperationException(root.GetProperty("message").GetString()));
                break;
            default:
                CommandReceived?.Invoke(root.Clone());
                break;
        }
    }

    public void Send(object message)
    {
        if (_loaded && !IsDisposed) Web.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));
    }

    public void SetSnapshot(object state)
    {
        _latestState = new { type = "state", state };
        Send(_latestState);
    }

    private async Task UpdateVisibilityAsync()
    {
        if (!_loaded || IsDisposed) return;
        var visible = Visible && WindowState != FormWindowState.Minimized;
        if (visible) Web.Resume();
        _controller!.IsVisible = visible;
        Send(new { type = "visibility", visible });
        if (!visible) await Web.TrySuspendAsync();
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg != 0x001a || !_loaded) return;
        var next = WindowsAppearance.Read();
        if (next == _appearance) return;
        _appearance = next;
        AppearanceChanged?.Invoke();
        Send(new { type = "environment", environment = EnvironmentMessage() });
    }

    public void ExitPreview() { _canExit = true; Close(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _controller?.Close(); _controller = null; }
        base.Dispose(disposing);
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_canExit && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }
}
