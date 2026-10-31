using System.Buffers.Binary;
using System.Text;

namespace NetworkAccelerator.Core.Services;

/// <summary>
/// File capabilities are applied to the ELF the kernel executes. A shebang script, including the
/// NixOS pattern that execs <c>ld-linux</c> against a sibling <c>sing-box.bin</c>, never receives them.
/// </summary>
public static class LinuxExecutableFormat
{
    public static bool CanHoldFileCapabilities(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[64];
            var read = stream.Read(header, 0, header.Length);
            return CanHoldFileCapabilities(header.AsSpan(0, read));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool CanHoldFileCapabilities(ReadOnlySpan<byte> header)
    {
        if (IsShebang(header)) return false;
        return IsElfExecutable(header);
    }

    public static bool IsShebang(ReadOnlySpan<byte> header) =>
        header.Length >= 2 && header[0] == (byte)'#' && header[1] == (byte)'!';

    public static bool IsElfExecutable(ReadOnlySpan<byte> header)
    {
        if (header.Length < 20 || !HasElfMagic(header)) return false;
        var klass = header[4];
        var data = header[5];
        if (klass is not (1 or 2) || data is not (1 or 2) || header[6] != 1) return false;
        var type = ReadU16(header, 16, data);
        return type is 2 or 3;
    }

    /// <summary>
    /// Returns the PT_INTERP path when it is recorded and not present on this machine.
    /// Static binaries and scripts return null.
    /// </summary>
    public static string? MissingInterpreter(string path)
    {
        try
        {
            var interpreter = ReadInterpreter(path);
            if (string.IsNullOrWhiteSpace(interpreter)) return null;
            return File.Exists(interpreter) ? null : interpreter;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? ReadInterpreter(string path)
    {
        using var stream = File.OpenRead(path);
        var header = new byte[64];
        var read = stream.Read(header, 0, header.Length);
        if (!IsElfExecutable(header.AsSpan(0, read))) return null;
        var klass = header[4];
        var data = header[5];
        long phoff;
        int phentsize;
        int phnum;
        if (klass == 2)
        {
            if (read < 64) return null;
            phoff = (long)ReadU64(header, 32, data);
            phentsize = ReadU16(header, 54, data);
            phnum = ReadU16(header, 56, data);
        }
        else
        {
            if (read < 52) return null;
            phoff = ReadU32(header, 28, data);
            phentsize = ReadU16(header, 42, data);
            phnum = ReadU16(header, 44, data);
        }

        if (phoff <= 0 || phnum <= 0 || phentsize is < 16 or > 512 || phnum > 128) return null;
        if (phoff > 16L * 1024 * 1024) return null;
        var tableLength = phentsize * phnum;
        var table = new byte[tableLength];
        stream.Position = phoff;
        if (stream.Read(table, 0, table.Length) != table.Length) return null;

        for (var index = 0; index < phnum; index++)
        {
            var entry = table.AsSpan(index * phentsize, phentsize);
            if (ReadU32(entry, 0, data) != 3) continue;
            long offset;
            long size;
            if (klass == 2)
            {
                if (entry.Length < 40) return null;
                offset = (long)ReadU64(entry, 8, data);
                size = (long)ReadU64(entry, 32, data);
            }
            else
            {
                if (entry.Length < 20) return null;
                offset = ReadU32(entry, 4, data);
                size = ReadU32(entry, 16, data);
            }

            if (offset < 0 || size is <= 1 or > 4096) return null;
            var text = new byte[size];
            stream.Position = offset;
            if (stream.Read(text, 0, text.Length) != text.Length) return null;
            var end = Array.IndexOf(text, (byte)0);
            if (end < 0) end = text.Length;
            var interpreter = Encoding.UTF8.GetString(text, 0, end);
            return interpreter.Length > 0 && interpreter[0] == '/' ? interpreter : null;
        }

        return null;
    }

    public static bool LooksLikeMissingLoader(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("ld-linux", StringComparison.Ordinal)
               || text.Contains("ld.so", StringComparison.Ordinal)
               || text.Contains("error while loading shared libraries", StringComparison.OrdinalIgnoreCase)
               || text.Contains("cannot open shared object", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasElfMagic(ReadOnlySpan<byte> header) =>
        header.Length >= 4
        && header[0] == 0x7F
        && header[1] == (byte)'E'
        && header[2] == (byte)'L'
        && header[3] == (byte)'F';

    private static ushort ReadU16(ReadOnlySpan<byte> buffer, int offset, byte data)
    {
        var slice = buffer.Slice(offset, 2);
        return data == 2
            ? BinaryPrimitives.ReadUInt16BigEndian(slice)
            : BinaryPrimitives.ReadUInt16LittleEndian(slice);
    }

    private static uint ReadU32(ReadOnlySpan<byte> buffer, int offset, byte data)
    {
        var slice = buffer.Slice(offset, 4);
        return data == 2
            ? BinaryPrimitives.ReadUInt32BigEndian(slice)
            : BinaryPrimitives.ReadUInt32LittleEndian(slice);
    }

    private static ulong ReadU64(ReadOnlySpan<byte> buffer, int offset, byte data)
    {
        var slice = buffer.Slice(offset, 8);
        return data == 2
            ? BinaryPrimitives.ReadUInt64BigEndian(slice)
            : BinaryPrimitives.ReadUInt64LittleEndian(slice);
    }
}
