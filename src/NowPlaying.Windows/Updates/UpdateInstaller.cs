using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace NowPlaying.Windows.Updates;

internal sealed record UpdateJob(int ParentPid, long ParentStartTicks, string Target, string Payload, string Sha256, string Version);

internal static class UpdateInstaller
{
    public static int Run(string jobPath)
    {
        string? log = null;
        UpdateJob? restart = null;
        var installed = false;
        try
        {
            var root = Path.GetFullPath(Path.Combine(SettingsStore.Folder, "updates")) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(jobPath);
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path) != "job.json" || new FileInfo(path).Length > 64 * 1024)
                throw new InvalidDataException("Invalid update manifest path.");
            log = Path.Combine(Path.GetDirectoryName(path)!, "update.log");
            var job = JsonSerializer.Deserialize<UpdateJob>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid update manifest.");
            if (Path.GetDirectoryName(Path.GetFullPath(job.Payload)) != Path.GetDirectoryName(path)) throw new InvalidDataException("Unexpected payload folder.");
            Validate(job);
            try
            {
                using var parent = Process.GetProcessById(job.ParentPid);
                if (parent.StartTime.ToUniversalTime().Ticks != job.ParentStartTicks) throw new InvalidDataException("Parent process changed.");
                if (!parent.WaitForExit(30_000)) throw new IOException("Synko did not close in time.");
            }
            catch (ArgumentException) { }
            restart = job;
            ApplyFiles(job);
            installed = true;
            var start = new ProcessStartInfo(job.Target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(job.Target)! };
            _ = Process.Start(start) ?? throw new IOException("Could not restart Synko.");
            File.AppendAllText(log, "Updated to " + job.Version + Environment.NewLine);
            File.WriteAllText(path + ".done", job.Version);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (log is not null) File.AppendAllText(log, ex + Environment.NewLine);
            if (restart is not null)
            {
                try
                {
                    if (installed && File.Exists(restart.Target + ".previous")) File.Copy(restart.Target + ".previous", restart.Target, overwrite: true);
                    if (File.Exists(restart.Target)) Process.Start(new ProcessStartInfo(restart.Target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(restart.Target)! });
                }
                catch (Exception retry) when (retry is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                { if (log is not null) File.AppendAllText(log, retry.Message + Environment.NewLine); }
            }
            MessageBox.Show("Не удалось установить обновление\n" + ex.Message, "Synko", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    internal static void Validate(UpdateJob job)
    {
        var target = Path.GetFullPath(job.Target); var payload = Path.GetFullPath(job.Payload);
        if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(target), @"\ASynko(?:-\d+\.\d+\.\d+-win-(?:x64|arm64))?\.exe\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            || Path.GetFileName(payload) != "Synko.exe" || target.Equals(payload, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid executable paths.");
        var root = Path.GetFullPath(Path.Combine(SettingsStore.Folder, "updates")) + Path.DirectorySeparatorChar;
        if (!payload.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Payload is outside the update folder.");
        if (job.Sha256.Length != 64 || !job.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid checksum.");
        using var input = File.OpenRead(payload);
        if (input.Length is <= 0 or > 256 * 1024 * 1024 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(input), Convert.FromHexString(job.Sha256)))
            throw new InvalidDataException("Update checksum failed.");
        input.Position = 0;
        if (input.ReadByte() != 'M' || input.ReadByte() != 'Z') throw new InvalidDataException("Update is not a Windows executable.");
        if (!Version.TryParse(FileVersionInfo.GetVersionInfo(payload).ProductVersion?.Split('+')[0], out var actual) || !Version.TryParse(job.Version, out var expected) || actual != expected)
            throw new InvalidDataException("Update version does not match the release.");
    }

    internal static void ApplyFiles(UpdateJob job)
    {
        Validate(job);
        var next = job.Target + ".next"; var previous = job.Target + ".previous";
        var moved = false;
        try
        {
            File.Copy(job.Payload, next, overwrite: true);
            File.Move(job.Target, previous, overwrite: true); moved = true;
            File.Move(next, job.Target);
        }
        catch
        {
            if (moved && !File.Exists(job.Target)) File.Copy(previous, job.Target, overwrite: true);
            throw;
        }
        finally { if (File.Exists(next)) File.Delete(next); }
    }
}
