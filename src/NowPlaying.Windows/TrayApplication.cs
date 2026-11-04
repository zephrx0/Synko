using System.Diagnostics;
using System.Text.Json;
using NowPlaying.Core;
using NowPlaying.Windows.SynkoUi;

namespace NowPlaying.Windows;

internal sealed class TrayApplication : ApplicationContext
{
    private readonly SynkoWindow _window;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _toggle;
    private readonly PresenceService _service;
    private readonly LiveSnapshot _snapshots = new();
    private readonly Updates.UpdateManager _updates = new();
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 3000 };
    private readonly RegisteredWaitHandle _showRegistration;
    private AppSettings _settings;
    private ServiceState _state = new([], null, "", null, false);
    private CancellationTokenSource? _validation;
    private bool _exiting;
    private readonly Func<string> _readClipboard;
    private readonly Action<string> _openUrl;
    internal SynkoWindow Window => _window;

    public TrayApplication(AppSettings settings, EventWaitHandle showEvent, bool background, Func<string>? readClipboard = null, Action<string>? openUrl = null)
    {
        _settings = settings;
        _readClipboard = readClipboard ?? (() => Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "");
        _openUrl = openUrl ?? (url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }));
        _window = new();
        _ = _window.Handle;
        MainForm = _window;
        _window.CommandReceived += OnUiCommand;
        _updates.Changed += () =>
        {
            if (_exiting || _window.IsDisposed) return;
            _window.BeginInvoke(() => { if (!_exiting) _window.Send(new { type = "update", update = _updates.State }); });
        };
        _window.Shown += async (_, _) =>
        {
            try { await _window.Ready; PushState(); }
            catch (Exception ex) { ShowError(ex); }
        };
        _service = new(settings);
        _service.Updated += state =>
        {
            if (!_exiting && !_window.IsDisposed)
                _window.BeginInvoke(() =>
                {
                    if (_exiting) return;
                    _state = state;
                    PushState();
                    var text = state.Selected is { } track ? "Synko: " + track.Title : "Synko";
                    _tray!.Text = text.Length > 120 ? text[..120] : text;
                });
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть Synko", null, (_, _) => ShowWindow());
        _toggle = new("Показывать в Discord") { Checked = settings.Enabled };
        _toggle.Click += (_, _) => Save(s => s with { Enabled = !s.Enabled });
        menu.Items.Add(_toggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, async (_, _) => await ExitAsync());
        _tray = new() { Text = "Synko", Icon = SynkoIcon.Create(), ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowWindow();
        _window.AppearanceChanged += () => { var previous = _tray.Icon; _tray.Icon = SynkoIcon.Create(); previous?.Dispose(); };
        _showRegistration = ThreadPool.RegisterWaitForSingleObject(showEvent,
            (_, _) => { if (!_exiting) _window.BeginInvoke(ShowWindow); }, null, Timeout.Infinite, false);
        _service.Start();
        PushState();
        if (!background || !settings.OnboardingDone) ShowWindow();
        _updateTimer.Tick += async (_, _) => { _updateTimer.Stop(); if (!_exiting) await _updates.CheckAsync(); };
        if (settings.AutoCheckUpdates && SettingsStore.OverrideFolder is null) _updateTimer.Start();
    }

    private void PushState() => _window.SetSnapshot(_snapshots.Create(_state, _settings) with { Update = _updates.State });
    private void ShowWindow()
    {
        _window.Show();
        _window.WindowState = FormWindowState.Normal;
        _window.Activate();
    }

    private async void OnUiCommand(JsonElement command)
    {
        try
        {
            switch (command.GetProperty("type").GetString())
            {
                case "publication": Save(s => s with { Enabled = command.GetProperty("enabled").GetBoolean() }); break;
                case "preference":
                    var key = command.GetProperty("key").GetString();
                    var value = command.GetProperty("value");
                    Save(s => key switch
                    {
                        "sourceMode" when value.TryGetInt32(out var mode) && Enum.IsDefined((SourceMode)mode) => s with { SourceMode = (SourceMode)mode },
                        "automaticArtwork" => s with { AutomaticArtwork = value.GetBoolean() },
                        "youTubeThumbnails" => s with { YouTubeThumbnails = value.GetBoolean() },
                        "imageKey" => s with { ImageKey = (value.GetString() ?? "").Trim() },
                        "themeMode" when value.GetString() is "system" or "light" or "dark" => s with { ThemeMode = value.GetString()! },
                        "colorSource" when value.GetString() is "artwork" or "windows" or "custom" => s with { ColorSource = value.GetString()! },
                        "customSeed" when IsColor(value.GetString()) => s with { CustomSeed = value.GetString(), ColorSource = "custom" },
                        "onboardingStep" => s with { OnboardingStep = Math.Clamp(value.GetInt32(), 0, 4) },
                        "onboardingDone" => s with { OnboardingDone = value.GetBoolean(), OnboardingStep = 0 },
                        "autoCheckUpdates" => s with { AutoCheckUpdates = value.GetBoolean() },
                        _ => s
                    });
                    break;
                case "startup": StartupRegistration.SetEnabled(command.GetProperty("enabled").GetBoolean()); PushState(); break;
                case "applicationId": await ValidateIdAsync(command.GetProperty("value").GetString() ?? ""); break;
                case "paste":
                    _window.Send(new { type = "clipboard", value = _readClipboard() });
                    break;
                case "portal": _openUrl("https://discord.com/developers/applications"); break;
                case "checkUpdates": await _updates.CheckAsync(); break;
                case "installUpdate":
                    var job = await _updates.PrepareAsync();
                    if (job is not null) { Updates.UpdateManager.StartInstaller(job); await ExitAsync(); }
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or
            JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { ShowError(ex); PushState(); }
    }

    private static bool IsColor(string? value) => value?.Length == 7 && value[0] == '#' && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    private async Task ValidateIdAsync(string id)
    {
        _validation?.Cancel();
        _updateTimer.Stop(); _updates.Dispose();
        var cancellation = new CancellationTokenSource();
        _validation = cancellation;
        try
        {
            if (!DiscordRpcClient.IsValidApplicationId(id))
            { _window.Send(new { type = "validation", id, valid = false, status = "Неверный ID" }); return; }
            _window.Send(new { type = "validation", id, valid = false, status = "Проверяем" });
            await using var rpc = new DiscordRpcClient();
            await rpc.ConnectAsync(id, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            Save(s => s with { ApplicationId = id });
            _window.Send(new { type = "validation", id, valid = true, status = "" });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or TimeoutException or ArgumentException)
        {
            if (!cancellation.IsCancellationRequested)
                _window.Send(new { type = "validation", id, valid = false,
                    status = ex.Message.Contains("ID", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("client", StringComparison.OrdinalIgnoreCase) ? "Неверный ID" : "Запустите Discord" });
        }
        finally { if (ReferenceEquals(_validation, cancellation)) _validation = null; cancellation.Dispose(); }
    }

    private void Save(Func<AppSettings, AppSettings> change)
    {
        var next = change(_settings);
        SettingsStore.Save(next);
        _settings = next;
        _service.Configure(next);
        _toggle.Checked = next.Enabled;
        PushState();
    }

    private void ShowError(Exception ex)
    {
        Directory.CreateDirectory(SettingsStore.Folder);
        File.AppendAllText(Path.Combine(SettingsStore.Folder, "synko-error.log"), DateTimeOffset.Now + " " + ex + Environment.NewLine);
        _window.Send(new { type = "error", message = "Не удалось выполнить действие" });
    }

    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _validation?.Cancel();
        _showRegistration.Unregister(null);
        _tray.Visible = false;
        await _service.DisposeAsync();
        _snapshots.Dispose();
        _tray.Icon?.Dispose();
        _tray.ContextMenuStrip?.Dispose();
        _tray.Dispose();
        _window.ExitPreview();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _showRegistration.Unregister(null); _updateTimer.Dispose(); _tray.Dispose(); _window.Dispose(); }
        base.Dispose(disposing);
    }
}
