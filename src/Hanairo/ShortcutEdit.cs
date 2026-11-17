using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Hanairo;

internal static class ShortcutEdit
{
    private const int HeaderSize = 0x4c;
    private const uint IconLocation = 1 << 6;
    private const uint Unicode = 1 << 7;
    private const uint IconEnvironment = 1 << 14;
    private const uint IconEnvironmentSignature = 0xA0000007;
    private static readonly Guid ShellLinkType = new("00021401-0000-0000-C000-000000000046");

    // MS-SHLLINK. Preserve the target ID list, LinkInfo, working directory,
    // arguments, property store (including AppUserModelID) and unknown blocks.
    public static byte[] WithIcon(byte[] input, string icon)
    {
        ReadOnlySpan<byte> bytes = input;
        if (bytes.Length < HeaderSize || Read32(bytes, 0) != HeaderSize ||
            new Guid(bytes.Slice(4, 16)) != ShellLinkType)
            throw InvalidLink();

        uint flags = Read32(bytes, 0x14);
        int cursor = HeaderSize;
        if ((flags & 1) != 0)
            cursor += 2 + Read16(bytes, cursor);
        if ((flags & 2) != 0)
        {
            uint size = Read32(bytes, cursor);
            if (size < 0x1c || size > bytes.Length - cursor)
                throw InvalidLink();
            cursor += (int)size;
        }
        if (cursor > bytes.Length)
            throw InvalidLink();

        bool wasUnicode = (flags & Unicode) != 0;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding oldEncoding = wasUnicode ? Encoding.Unicode : Encoding.GetEncoding(0);
        bool convertToUnicode = !wasUnicode && oldEncoding.GetString(oldEncoding.GetBytes(icon)) != icon;
        Encoding newEncoding = convertToUnicode ? Encoding.Unicode : oldEncoding;

        using var output = new MemoryStream(input.Length + 512);
        using var writer = new BinaryWriter(output);
        writer.Write(bytes[..cursor]);
        for (int bit = 2; bit <= 6; bit++)
        {
            if ((flags & (1u << bit)) == 0)
                continue;

            int count = Read16(bytes, cursor);
            int byteCount = count * (wasUnicode ? 2 : 1);
            if (byteCount > bytes.Length - cursor - 2)
                throw InvalidLink();

            if (bit != 6)
            {
                if (convertToUnicode)
                    WriteString(writer, oldEncoding.GetString(bytes.Slice(cursor + 2, byteCount)), newEncoding);
                else
                    writer.Write(bytes.Slice(cursor, byteCount + 2));
            }
            cursor += byteCount + 2;
        }
        WriteString(writer, icon, newEncoding);

        while (cursor < bytes.Length)
        {
            uint size = Read32(bytes, cursor);
            if (size < 4)
            {
                writer.Write(bytes[cursor..]);
                break;
            }
            if (size < 8 || size > bytes.Length - cursor)
                throw InvalidLink();

            if (Read32(bytes, cursor + 4) != IconEnvironmentSignature)
                writer.Write(bytes.Slice(cursor, (int)size));
            cursor += (int)size;
        }

        output.Position = 0x14;
        writer.Write((flags | IconLocation | (convertToUnicode ? Unicode : 0)) & ~IconEnvironment);
        output.Position = 0x38;
        writer.Write(0); // IconIndex.
        return output.ToArray();
    }

    private static void WriteString(BinaryWriter writer, string text, Encoding encoding)
    {
        byte[] bytes = encoding.GetBytes(text);
        int units = encoding == Encoding.Unicode ? text.Length : bytes.Length;
        writer.Write(checked((ushort)units));
        writer.Write(bytes);
    }

    private static uint Read32(ReadOnlySpan<byte> data, int offset) =>
        offset <= data.Length - 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) : throw InvalidLink();

    private static ushort Read16(ReadOnlySpan<byte> data, int offset) =>
        offset <= data.Length - 2 ? BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]) : throw InvalidLink();

    private static IOException InvalidLink() => new("无法读取这个 Windows 快捷方式的图标字段。");
}
