using System.Buffers.Binary;
using System.Text.Json;

namespace NowPlaying.Core;

public sealed record RpcFrame(int Opcode, byte[] Payload)
{
    public const int MaxPayloadBytes = 1024 * 1024;

    public static async Task<RpcFrame> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var opcode = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        var size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (size < 0 || size > MaxPayloadBytes) throw new InvalidDataException("Недопустимая длина RPC-пакета.");
        var payload = new byte[size];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return new(opcode, payload);
    }

    public static Task WriteJsonAsync(Stream stream, int opcode, object value, CancellationToken cancellationToken)
        => WriteAsync(stream, opcode, JsonSerializer.SerializeToUtf8Bytes(value), cancellationToken);

    public static async Task WriteAsync(Stream stream, int opcode, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length > MaxPayloadBytes) throw new InvalidDataException("Слишком большой RPC-пакет.");
        var frame = new byte[8 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), payload.Length);
        payload.CopyTo(frame, 8);
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
