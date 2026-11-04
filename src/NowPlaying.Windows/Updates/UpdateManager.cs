using System.Diagnostics;
using System.Text.Json;
using NowPlaying.Core;

namespace NowPlaying.Windows.Updates;

internal sealed record UiUpdateState(string Status, string Message, string? Version = null, double Progress = 0, bool Available = false);

internal sealed class UpdateManager : IDisposable
{
    public const string Repository = "zephrx0/Synko";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly CancellationTokenSource _stop = new();
    private ReleaseUpdate? _release;
    private bool _busy;
    public UiUpdateState State { get; private set; } = new("idle", "");
    public event Action? Changed;
    private void Set(UiUpdateState state) { State = state; Changed?.Invoke(); }

    public async Task CheckAsync()
    {
        if (_busy) return;
        _busy = true;
        Set(new("checking", "Проверяем обновления"));
        try
        {
            CleanupCompletedDownloads();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var version = Version.Parse(Application.ProductVersion.Split('+')[0]);
            _release = await new ReleaseUpdateClient(_http, Repository).CheckAsync(version, timeout.Token);
            Set(_release is null ? new("current", "Установлена последняя версия") : new("available", "Доступна версия " + _release.Version, _release.Version.ToString(3), Available: true));
        }
        catch (NoPublishedReleaseException) { _release = null; Set(new("unpublished", "Релизы ещё не опубликованы")); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            _release = null;
            if (!_stop.IsCancellationRequested) Set(new("error", "Не удалось проверить обновления"));
        }
        finally { _busy = false; }
    }

    public async Task<string?> PrepareAsync()
    {
        if (_busy || _release is null) return null;
        _busy = true;
        var release = _release;
        Set(new("downloading", "Загружаем обновление", release.Version.ToString(3)));
        try
        {
            var folder = Path.Combine(SettingsStore.Folder, "updates", release.Version.ToString(3) + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var payload = Path.Combine(folder, "Synko.exe");
            var lastProgress = -1d;
            var progress = new Progress<double>(value =>
            {
                if (value < 1 && value - lastProgress < .01) return;
                lastProgress = value;
                Set(new("downloading", "Загружаем обновление", release.Version.ToString(3), value));
            });
            await new ReleaseUpdateClient(_http, Repository).DownloadAsync(release, payload, progress, _stop.Token);
            var current = Environment.ProcessPath!;
            using var parent = Process.GetCurrentProcess();
            var job = new UpdateJob(Environment.ProcessId, parent.StartTime.ToUniversalTime().Ticks, current, payload, release.Sha256, release.Version.ToString(3));
            UpdateInstaller.Validate(job);
            var jobPath = Path.Combine(folder, "job.json");
            await File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(job), _stop.Token);
            var helper = Path.Combine(folder, "Synko.Update.exe");
            File.Copy(current, helper, overwrite: false);
            Set(new("ready", "Перезапускаем Synko", release.Version.ToString(3), 1));
            return jobPath;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
        {
            if (!_stop.IsCancellationRequested) Set(new("error", "Не удалось загрузить обновление", release.Version.ToString(3), Available: true));
            return null;
        }
        finally { _busy = false; }
    }

    public static void StartInstaller(string jobPath)
    {
        var helper = Path.Combine(Path.GetDirectoryName(jobPath)!, "Synko.Update.exe");
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(jobPath);
        _ = Process.Start(start) ?? throw new IOException("Could not start updater.");
    }

    private static void CleanupCompletedDownloads()
    {
        var root = Path.Combine(SettingsStore.Folder, "updates");
        if (!Directory.Exists(root)) return;
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 || !File.Exists(Path.Combine(folder, "job.json.done"))) continue;
            foreach (var name in new[] { "Synko.exe", "Synko.exe.partial", "Synko.Update.exe" })
            {
                try { File.Delete(Path.Combine(folder, name)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public void Dispose() { _stop.Cancel(); _http.Dispose(); _stop.Dispose(); }
}
