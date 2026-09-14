using System.Globalization;

namespace DanmuApi.Platform;

/// <summary>Minimal PE header reader. It exists to prove which architecture a binary really is, so a
/// mis-stamped runtime bundle fails on the machine that produced it rather than on a user's.</summary>
public static class ExecutableImage
{
    public static ushort Machine(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[0x40];
        stream.ReadExactly(header);
        if (header[0] != (byte)'M' || header[1] != (byte)'Z') throw new InvalidDataException("不是可执行文件：" + path);
        var offset = BitConverter.ToInt32(header[0x3C..0x40]);
        if (offset < 0x40) throw new InvalidDataException("PE 头偏移无效：" + path);
        stream.Position = offset;
        Span<byte> signature = stackalloc byte[6];
        stream.ReadExactly(signature);
        if (signature[0] != (byte)'P' || signature[1] != (byte)'E' || signature[2] != 0 || signature[3] != 0)
            throw new InvalidDataException("PE 签名无效：" + path);
        return BitConverter.ToUInt16(signature[4..6]);
    }

    public static string ArchitectureName(ushort machine) => machine switch
    {
        0x014c => "x86",
        0x8664 => "x64",
        0xAA64 => "arm64",
        _ => throw new InvalidDataException("未知的 PE 架构：0x" + machine.ToString("X4", CultureInfo.InvariantCulture)),
    };
}
