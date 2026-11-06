using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using NowPlaying.Core;

namespace NowPlaying.Windows;

internal static class BrowserAddressProbe
{
    internal sealed record Request(long Window, string Title);
    internal static string? LastDiagnostic { get; private set; }

    // Accessibility providers are foreign COM code: a hung browser must not
    // occupy the only lookup worker forever. Bound it in a separate process.
    internal static async Task<BrowserAddressReader.Result> ReadAsync(IntPtr window, string title, CancellationToken cancellationToken)
    {
        var exe = System.Reflection.Assembly.GetEntryAssembly() == typeof(Program).Assembly
            ? Environment.ProcessPath! : Path.Combine(AppContext.BaseDirectory, "Synko.exe");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        using var process = new Process { StartInfo = new(exe) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("--read-browser-address");
        process.StartInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Request(window.ToInt64(), title)))));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var errors = process.StandardError.ReadToEndAsync(deadline.Token);
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, errors);
            if (process.ExitCode != 0 || output.Result.Length > 8192) throw new IOException("Accessibility helper failed");
            var result = JsonSerializer.Deserialize<BrowserAddressReader.Result>(output.Result) ?? throw new IOException("Empty accessibility result");
            LastDiagnostic = result.Diagnostic;
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { LastDiagnostic = "Browser accessibility timed out; lookup will be retried"; }
        catch (Exception ex) when (ex is IOException or JsonException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { LastDiagnostic = "Browser accessibility unavailable: " + ex.GetType().Name; }
        finally
        {
            try
            {
                if (process.Id > 0 && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
        }
        return new(MediaWebsite.Unknown, null, LastDiagnostic);
    }

    internal static int Run(string encoded)
    {
        SetErrorMode(0x0001 | 0x0002);
        try
        {
            if (encoded.Length > 16384) return 1;
            var request = JsonSerializer.Deserialize<Request>(Convert.FromBase64String(encoded));
            if (request is null || request.Window == 0 || request.Title is null || request.Title.Length > 4096) return 1;
            var result = Task.Run(() => BrowserAddressReader.Read(new IntPtr(request.Window), request.Title)).GetAwaiter().GetResult();
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.Write(JsonSerializer.Serialize(result with { Diagnostic = BrowserAddressReader.LastDiagnostic }));
            return 0;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or IOException) { return 1; }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint SetErrorMode(uint mode);
}
