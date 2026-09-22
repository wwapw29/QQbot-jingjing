using System.IO.Compression;
using System.Text;

namespace QQBot.Core.Vision;

/// <summary>
/// 图片文本元数据抽取（纯托管实现，无第三方依赖）：
///  - PNG：tEXt / iTXt / zTXt 文本块（AI 出图的 prompt/参数通常写在这里）
///  - JPEG：EXIF（ImageDescription / UserComment / Software / DateTime / XP* 等）+ COM 注释段 + XMP
///  - WebP：EXIF / XMP 块
/// 用途：聊天图片的元数据抽成 sidecar 文本存进静静的空间——她直接读文本即可，
/// 不必去解析图片二进制（压缩过的图已经不带这些信息了）。
/// </summary>
public static class ImageMetadataExtractor
{
    /// <summary>抽取文本元数据；没有可读元数据时返回 null</summary>
    public static string? Extract(byte[] bytes, int maxChars = 20000)
    {
        try
        {
            if (bytes is null || bytes.Length < 16) return null;
            var parts = new List<string>();

            if (Match(bytes, 0, "\u0089PNG\r\n\u001a\n")) ExtractPng(bytes, parts);
            else if (bytes[0] == 0xFF && bytes[1] == 0xD8) ExtractJpeg(bytes, parts);
            else if (Match(bytes, 0, "RIFF") && Match(bytes, 8, "WEBP")) ExtractWebp(bytes, parts);

            if (parts.Count == 0) return null;
            var text = string.Join("\n", parts).Trim();
            if (text.Length == 0) return null;
            if (text.Length > maxChars) text = text[..maxChars] + "\n…（元数据过长，已截断）";
            return text;
        }
        catch
        {
            return null;   // 元数据抽取失败不影响主流程
        }
    }

    // ---------------- PNG ----------------

    private static void ExtractPng(byte[] b, List<string> parts)
    {
        int pos = 8;   // 跳过 PNG 签名
        while (pos + 12 <= b.Length)
        {
            int len = BE32(b, pos);
            if (len < 0 || pos + 12L + len > b.Length) break;
            var type = Encoding.ASCII.GetString(b, pos + 4, 4);
            int data = pos + 8;
            switch (type)
            {
                case "tEXt":
                {
                    int z = IndexOf(b, data, data + len, 0);
                    if (z > data)
                        parts.Add($"[PNG tEXt:{Ascii(b, data, z)}] {Latin1(b, z + 1, data + len)}");
                    break;
                }
                case "zTXt":
                {
                    int z = IndexOf(b, data, data + len, 0);
                    if (z > data && z + 2 <= data + len)
                    {
                        var key = Ascii(b, data, z);
                        var txt = Inflate(b, z + 2, data + len - (z + 2));
                        if (!string.IsNullOrEmpty(txt)) parts.Add($"[PNG zTXt:{key}] {txt}");
                    }
                    break;
                }
                case "iTXt":
                {
                    int z = IndexOf(b, data, data + len, 0);            // keyword\0
                    if (z > data && z + 3 <= data + len)
                    {
                        var key = Ascii(b, data, z);
                        byte compFlag = b[z + 1];
                        int p = z + 3;                                   // 跳过 compFlag + compMethod
                        int langEnd = IndexOf(b, p, data + len, 0);      // languageTag\0
                        int textStart = langEnd > p ? IndexOf(b, langEnd + 1, data + len, 0) : -1;
                        if (textStart > 0)
                        {
                            var raw = b.AsSpan(textStart + 1, data + len - (textStart + 1)).ToArray();
                            var txt = compFlag == 1 ? Inflate(raw, 0, raw.Length) : Encoding.UTF8.GetString(raw);
                            if (!string.IsNullOrEmpty(txt)) parts.Add($"[PNG iTXt:{key}] {txt}");
                        }
                    }
                    break;
                }
            }
            pos += 12 + len;   // len(4)+type(4)+data+crc(4)
        }
    }

    // ---------------- JPEG ----------------

    private static void ExtractJpeg(byte[] b, List<string> parts)
    {
        int pos = 2;
        while (pos + 4 <= b.Length)
        {
            if (b[pos] != 0xFF) { pos++; continue; }
            byte marker = b[pos + 1];
            if (marker == 0xFF) { pos++; continue; }
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { pos += 2; continue; }
            if (marker == 0xDA) break;          // SOS 之后是压缩像素数据，不再有元数据段
            int len = BE16(b, pos + 2);
            if (len < 2 || pos + 2L + len > b.Length) break;
            int segStart = pos + 4;
            int segLen = len - 2;

            if (marker == 0xFE && segLen > 0)
            {
                parts.Add("[JPEG 注释] " + Encoding.UTF8.GetString(b, segStart, segLen).Trim('\0').Trim());
            }
            else if (marker == 0xE1 && segLen > 6 && Match(b, segStart, "Exif\0\0"))
            {
                ParseTiff(b, segStart + 6, parts);
            }
            else if (marker == 0xE1 && segLen > 29 && Match(b, segStart, "http://ns.adobe.com/xap/1.0/\0"))
            {
                var xmp = Encoding.UTF8.GetString(b, segStart + 29, segLen - 29);
                if (!string.IsNullOrWhiteSpace(xmp)) parts.Add("[XMP] " + xmp.Trim());
            }
            pos += 2 + len;
        }
    }

    // ---------------- WebP ----------------

    private static void ExtractWebp(byte[] b, List<string> parts)
    {
        int pos = 12;   // RIFF????WEBP
        while (pos + 8 <= b.Length)
        {
            var fourcc = Encoding.ASCII.GetString(b, pos, 4);
            int size = (int)LE32(b, pos + 4);
            if (size < 0 || pos + 8L + size > b.Length) break;
            int data = pos + 8;
            if (fourcc == "EXIF") ParseTiff(b, data, parts);
            else if (fourcc == "XMP ")
            {
                var xmp = Encoding.UTF8.GetString(b, data, size);
                if (!string.IsNullOrWhiteSpace(xmp)) parts.Add("[XMP] " + xmp.Trim());
            }
            pos += 8 + size + (size % 2);   // 块按偶数字节对齐
        }
    }

    // ---------------- TIFF / EXIF ----------------

    private static void ParseTiff(byte[] b, int start, List<string> parts)
    {
        if (start + 8 > b.Length) return;
        bool le = b[start] == 'I' && b[start + 1] == 'I';
        bool be = b[start] == 'M' && b[start + 1] == 'M';
        if (!le && !be) return;
        if (ReadU16(b, start + 2, le) != 42) return;

        var ifd0 = (int)ReadU32(b, start + 4, le);
        ParseIfd(b, start, ifd0, le, parts, depth: 0);
    }

    private static void ParseIfd(byte[] b, int tiff, int ifdOffset, bool le, List<string> parts, int depth)
    {
        if (depth > 2 || ifdOffset <= 0) return;
        int at = tiff + ifdOffset;
        if (at + 2 > b.Length) return;
        int count = ReadU16(b, at, le);
        if (count <= 0 || count > 512) return;

        for (int i = 0; i < count; i++)
        {
            int e = at + 2 + i * 12;
            if (e + 12 > b.Length) return;
            int tag = ReadU16(b, e, le);
            int type = ReadU16(b, e + 2, le);
            long n = ReadU32(b, e + 4, le);
            int unit = TypeSize(type);
            if (unit == 0 || n <= 0) continue;
            long total = n * unit;
            int valuePos = total <= 4 ? e + 8 : tiff + (int)ReadU32(b, e + 8, le);
            if (valuePos < 0 || valuePos > b.Length) continue;
            int avail = (int)Math.Min(total, b.Length - valuePos);

            switch (tag)
            {
                case 0x8769:   // ExifIFD 指针：跳进去再读一遍（UserComment/DateTimeOriginal 在这里）
                    ParseIfd(b, tiff, (int)ReadU32(b, e + 8, le), le, parts, depth + 1);
                    continue;
                case 0x0112:   // Orientation
                    if (type == 3) parts.Add($"[EXIF 方向] {ReadU16(b, valuePos, le)}");
                    continue;
                case 0x010E: parts.Add("[EXIF 描述] " + Ascii(b, valuePos, valuePos + avail)); continue;
                case 0x010F: parts.Add("[EXIF 厂商] " + Ascii(b, valuePos, valuePos + avail)); continue;
                case 0x0110: parts.Add("[EXIF 机型] " + Ascii(b, valuePos, valuePos + avail)); continue;
                case 0x0131: parts.Add("[EXIF 软件] " + Ascii(b, valuePos, valuePos + avail)); continue;
                case 0x0132: parts.Add("[EXIF 时间] " + Ascii(b, valuePos, valuePos + avail)); continue;
                case 0x013B: parts.Add("[EXIF 作者] " + Ascii(b, valuePos, valuePos + avail)); continue;
                case 0x8298: parts.Add("[EXIF 版权] " + Ascii(b, valuePos, valuePos + avail)); continue;
                case 0x9003: parts.Add("[EXIF 拍摄时间] " + Ascii(b, valuePos, valuePos + avail)); continue;
                case 0x9C9C: parts.Add("[EXIF 备注] " + Utf16Le(b, valuePos, avail)); continue;
                case 0x9C9E: parts.Add("[EXIF 关键词] " + Utf16Le(b, valuePos, avail)); continue;
                case 0x9286:   // UserComment：可选 8 字节字符集标记（ASCII/UNICODE/JIS），之后才是正文
                {
                    int p = valuePos, len0 = avail;
                    var head = Latin1(b, valuePos, Math.Min(valuePos + 8, b.Length));
                    if (head.StartsWith("ASCII", StringComparison.OrdinalIgnoreCase)
                        || head.StartsWith("UNICODE", StringComparison.OrdinalIgnoreCase)
                        || head.StartsWith("JIS", StringComparison.OrdinalIgnoreCase))
                    {
                        p += 8;
                        len0 -= 8;
                    }
                    var txt = DecodeAuto(b, p, len0);
                    if (txt.Length > 0) parts.Add("[EXIF 用户注释] " + txt);
                    continue;
                }
            }
        }
    }

    // ---------------- 小工具 ----------------

    private static int TypeSize(int type) => type switch
    {
        1 or 2 or 6 or 7 => 1,   // BYTE / ASCII / SBYTE / UNDEFINED
        3 or 8 => 2,             // SHORT / SSHORT
        4 or 9 or 11 => 4,       // LONG / SLONG / FLOAT
        5 or 10 or 12 => 8,      // RATIONAL / SRATIONAL / DOUBLE
        _ => 0
    };

    private static bool Match(byte[] b, int off, string ascii)
    {
        if (off < 0 || off + ascii.Length > b.Length) return false;
        for (int i = 0; i < ascii.Length; i++)
            if (b[off + i] != (byte)ascii[i]) return false;
        return true;
    }

    private static int IndexOf(byte[] b, int from, int to, byte value)
    {
        to = Math.Min(to, b.Length);
        for (int i = from; i < to; i++)
            if (b[i] == value) return i;
        return -1;
    }

    private static int BE16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
    private static int BE32(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
    private static long LE32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

    private static int ReadU16(byte[] b, int o, bool le)
    {
        if (o + 2 > b.Length) return 0;
        return le ? b[o] | (b[o + 1] << 8) : BE16(b, o);
    }

    private static long ReadU32(byte[] b, int o, bool le)
    {
        if (o + 4 > b.Length) return 0;
        return le ? LE32(b, o) : (uint)BE32(b, o);
    }

    private static string Ascii(byte[] b, int from, int to)
    {
        to = Math.Min(to, b.Length);
        if (to <= from) return "";
        return Latin1(b, from, to).Trim('\0', ' ', '\r', '\n');
    }

    private static string Latin1(byte[] b, int from, int to)
    {
        to = Math.Min(to, b.Length);
        return to <= from ? "" : Encoding.Latin1.GetString(b, from, to - from);
    }

    private static string Utf16Le(byte[] b, int from, int len)
    {
        len = Math.Min(len, b.Length - from);
        if (len <= 0) return "";
        return Encoding.Unicode.GetString(b, from, len).Trim('\0', ' ', '\r', '\n');
    }

    /// <summary>
    /// 自动判断编码再解码（EXIF 文本有时是 UTF-16LE、有时是 UTF-8/ASCII，且不一定带字符集标记）：
    /// 奇数位大量 0x00 → 按 UTF-16LE；否则按 UTF-8（失败再退回 Latin1）。
    /// </summary>
    private static string DecodeAuto(byte[] b, int from, int len)
    {
        len = Math.Min(len, b.Length - from);
        if (len <= 0) return "";
        int oddZeros = 0, pairs = len / 2;
        for (int i = from + 1; i < from + len; i += 2)
            if (b[i] == 0) oddZeros++;
        string text;
        if (pairs > 0 && oddZeros * 4 >= pairs * 3)   // ≥75% 的奇数位是 0 → UTF-16LE
        {
            text = Encoding.Unicode.GetString(b, from, len);
        }
        else
        {
            try { text = Encoding.UTF8.GetString(b, from, len); }
            catch { text = Latin1(b, from, from + len); }
        }
        return text.Trim('\0', ' ', '\r', '\n');
    }

    /// <summary>zlib 解压（PNG zTXt/iTXt 用）</summary>
    private static string? Inflate(byte[] b, int from, int len)
    {
        try
        {
            using var ms = new MemoryStream(b, from, len);
            using var z = new ZLibStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            z.CopyTo(outMs);
            return Encoding.UTF8.GetString(outMs.ToArray()).Trim('\0', ' ', '\r', '\n');
        }
        catch
        {
            return null;
        }
    }
}
