using System.Buffers.Binary;
using System.Text;

namespace AndroidScreen.Module.Services;

// Wire format pinned to Genymobile/scrcpy v3.3.4 (app/src/control_msg.c).
public static class ScrcpyProtocol
{
    public static byte[] Key(uint code, byte action)
    {
        var data = new byte[14];
        data[1] = action;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(2), code);
        return data;
    }

    public static byte[] Touch(byte action, int x, int y, int width, int height)
    {
        var data = new byte[32]; data[0] = 2; data[1] = action;
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(2), ulong.MaxValue - 1); // generic finger
        Position(data.AsSpan(10), x, y, width, height);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(22), action is 1 or 3 ? (ushort)0 : ushort.MaxValue);
        return data;
    }

    public static byte[] Scroll(int x, int y, int width, int height, double horizontal, double vertical)
    {
        var data = new byte[21]; data[0] = 3;
        Position(data.AsSpan(1), x, y, width, height);
        BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(13), ScrollValue(horizontal));
        BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(15), ScrollValue(vertical));
        return data;
    }

    public static byte[] Text(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 300) throw new ArgumentException("单次输入不能超过 300 个 UTF-8 字节。");
        var data = new byte[5 + bytes.Length]; data[0] = 1;
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(1), bytes.Length);
        bytes.CopyTo(data, 5); return data;
    }

    public static byte[] Clipboard(string text, bool paste)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 256 * 1024 - 14) throw new ArgumentException("剪贴板内容过大，请分段发送。");
        var data = new byte[14 + bytes.Length]; data[0] = 9; data[9] = paste ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(10), bytes.Length);
        bytes.CopyTo(data, 14); return data;
    }

    private static short ScrollValue(double value) => (short)Math.Clamp(value * 2048, short.MinValue, short.MaxValue);
    private static void Position(Span<byte> data, int x, int y, int width, int height)
    {
        if (width is <= 0 or > ushort.MaxValue || height is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width));
        BinaryPrimitives.WriteInt32BigEndian(data, Math.Clamp(x, 0, width - 1));
        BinaryPrimitives.WriteInt32BigEndian(data[4..], Math.Clamp(y, 0, height - 1));
        BinaryPrimitives.WriteUInt16BigEndian(data[8..], (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(data[10..], (ushort)height);
    }
}
