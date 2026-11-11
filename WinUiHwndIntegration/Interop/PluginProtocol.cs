using System.Buffers.Binary;
using System.IO;

namespace WinUiHwndIntegration.Interop;

internal enum PluginMessageKind { Ready = 1, Resize, Focus, Leave, Shutdown, Attached, Error }

internal readonly record struct PluginMessage(PluginMessageKind Kind, long A = 0, long B = 0);

internal static class PluginProtocol
{
    private const int Magic = 0x48574E31;

    public static async Task WriteAsync(Stream stream, PluginMessage message, CancellationToken token)
    {
        byte[] bytes = new byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), (int)message.Kind);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), message.A);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), message.B);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    public static async Task<PluginMessage> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] bytes = new byte[24];
        await stream.ReadExactlyAsync(bytes, token);
        var kind = (PluginMessageKind)BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        if (BinaryPrimitives.ReadInt32LittleEndian(bytes) != Magic || !Enum.IsDefined(kind))
        {
            throw new InvalidDataException("Invalid plugin protocol message.");
        }
        return new(kind, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8)), BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16)));
    }
}
