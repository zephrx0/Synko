using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;

namespace NowPlaying.Core;

// One owner calls Connect/Set/Disconnect in sequence. A dedicated reader handles
// acknowledgments, errors and pings even when the media has not changed.
public sealed class DiscordRpcClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _pending = new();
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _readerCancellation;
    private Task? _reader;
    private TaskCompletionSource? _ready;
    private string? _applicationId;
    public bool IsConnected => _pipe?.IsConnected == true && _ready?.Task.IsCompletedSuccessfully == true && _reader?.IsCompleted == false;

    public static bool IsValidApplicationId(string id)
        => id.Length is >= 17 and <= 20 && id.All(char.IsAsciiDigit) && ulong.TryParse(id, out var number) && number > 0;

    public async Task ConnectAsync(string applicationId, CancellationToken cancellationToken, string pipePrefix = "discord-ipc-")
    {
        if (IsConnected && _applicationId == applicationId) return;
        await DisconnectAsync();
        if (!IsValidApplicationId(applicationId)) throw new ArgumentException("Укажите корректный Discord Application ID.");
        NamedPipeClientStream? connected = null;
        for (var i = 0; i < 10; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = new NamedPipeClientStream(".", pipePrefix + i, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await candidate.ConnectAsync(100, cancellationToken);
                connected = candidate;
                break;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                candidate.Dispose();
            }
            catch { candidate.Dispose(); throw; }
        }
        if (connected is null) throw new IOException("Discord не найден. Откройте настольный клиент Discord.");
        _pipe = connected;
        _applicationId = applicationId;
        _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _readerCancellation = new();
        _reader = ReadLoopAsync(connected, _readerCancellation.Token);
        try
        {
            await SendJsonAsync(0, new { v = 1, client_id = applicationId }, cancellationToken);
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch { await DisconnectAsync(); throw; }
    }

    public async Task SetActivityAsync(Presence? presence, CancellationToken cancellationToken)
    {
        if (!IsConnected) throw new IOException("Нет соединения с Discord.");
        var nonce = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[nonce] = completion;
        try
        {
            await SendJsonAsync(1, new
            {
                cmd = "SET_ACTIVITY",
                args = new { pid = Environment.ProcessId, activity = presence?.ToActivity() },
                nonce
            }, cancellationToken);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        finally { _pending.TryRemove(nonce, out _); }
    }

    private async Task SendJsonAsync(int opcode, object payload, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try { await RpcFrame.WriteJsonAsync(_pipe ?? throw new IOException("Соединение закрыто."), opcode, payload, cancellationToken); }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync(Stream pipe, CancellationToken cancellationToken)
    {
        Exception reason = new IOException("Discord закрыл соединение.");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await RpcFrame.ReadAsync(pipe, cancellationToken);
                if (frame.Opcode == 2) throw new IOException("Discord закрыл RPC-соединение. Проверьте Application ID.");
                if (frame.Opcode == 3)
                {
                    await _writeLock.WaitAsync(cancellationToken);
                    try { await RpcFrame.WriteAsync(pipe, 4, frame.Payload, cancellationToken); }
                    finally { _writeLock.Release(); }
                    continue;
                }
                if (frame.Opcode != 1) continue;
                using var message = JsonDocument.Parse(frame.Payload);
                var root = message.RootElement;
                var evt = root.TryGetProperty("evt", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                var nonce = root.TryGetProperty("nonce", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                if (evt == "READY") _ready?.TrySetResult();
                else if (evt == "ERROR")
                {
                    var detail = root.TryGetProperty("data", out var data) && data.TryGetProperty("message", out var error)
                        ? error.GetString() : "неизвестная ошибка";
                    var exception = new IOException("Discord: " + detail);
                    if (nonce is not null && _pending.TryRemove(nonce, out var failed)) failed.TrySetException(exception);
                    else if (_ready?.Task.IsCompleted == false) _ready.TrySetException(exception);
                    else throw exception;
                }
                else if (nonce is not null && _pending.TryRemove(nonce, out var completed)) completed.TrySetResult();
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or JsonException or InvalidOperationException)
        {
            reason = ex;
        }
        finally
        {
            _ready?.TrySetException(reason);
            foreach (var item in _pending)
                if (_pending.TryRemove(item.Key, out var completion)) completion.TrySetException(reason);
        }
    }

    public async Task DisconnectAsync()
    {
        _readerCancellation?.Cancel();
        _pipe?.Dispose();
        if (_reader is not null) await _reader;
        _readerCancellation?.Dispose();
        _readerCancellation = null;
        _reader = null;
        _pipe = null;
        _ready = null;
        _applicationId = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _writeLock.Dispose();
    }
}
