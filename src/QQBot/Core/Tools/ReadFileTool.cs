using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using QQBot.Core.Options;

namespace QQBot.Core.Tools;

/// <summary>
/// read_file —— 读取静静自己空间里的**文本文件**（比 run_shell + type 快得多，也不会踩 Windows 命令语法的坑）。
///  - path 相对她的空间根目录（Shell.SandboxPath）；空间外的路径拒读
///  - 传目录 → 列出目录内容；路径不存在 → 列出同目录候选，方便一次找对
///  - 编码：BOM 优先 → UTF-8（严格）→ GBK(936, Win32 API) → Latin1 兜底
///  - 二进制（图片/压缩包等）不吐乱码，改为给出类型/大小与"该用什么方式看"的提示
/// </summary>
public sealed class ReadFileTool : ITool
{
    private readonly ShellOptions _shell;
    private const int HardMaxChars = 20000;   // 单次返回上限（防止刷爆上下文）

    public ReadFileTool(ShellOptions shell) => _shell = shell;

    public string Name => "read_file";

    public string Description =>
        "读取你自己空间里的文本文件内容（txt / md / json / csv / log / 代码等），参数 path = 文件路径，" +
        "相对你的空间根目录（例如 images/群123456_20260912-0246_a1b2.txt、笔记-画图tag备忘.txt）。" +
        "看聊天图片的元数据 txt、翻自己的笔记与脚本、读文本文件都优先用它——比 run_shell 快得多。" +
        "传目录路径会列出该目录里的文件；只能读你自己空间内的文件。";

    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "文件或目录路径（相对你的空间根目录，如 images/xxx.txt）" },
            ["maxChars"] = new JsonObject { ["type"] = "integer", ["description"] = "最多返回多少字符（可选，默认用 Shell.MaxOutputChars，上限 20000）" }
        },
        ["required"] = new JsonArray("path"),
        ["additionalProperties"] = false
    };

    public Task<string> ExecuteAsync(string argsJson, ToolContext ctx, CancellationToken ct)
        => Task.FromResult(Read(argsJson));

    private string Read(string argsJson)
    {
        JsonObject? args;
        try { args = JsonNode.Parse(argsJson) as JsonObject; }
        catch { return "参数解析失败：请传 {\"path\":\"相对路径\"}"; }

        var path = args?["path"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return "参数 path 为空：请给出要读的文件路径（相对你的空间根目录，例如 images/群123456_20260912-0246_a1b2.txt）。";

        int maxChars = 0;
        if (args?["maxChars"] is JsonValue mv && mv.TryGetValue<int>(out var mc) && mc > 0)
            maxChars = Math.Min(mc, HardMaxChars);
        if (maxChars == 0) maxChars = Math.Clamp(_shell.MaxOutputChars, 500, HardMaxChars);

        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _shell.SandboxPath));
        string full;
        try { full = Path.GetFullPath(Path.Combine(root, path)); }
        catch (Exception ex) { return $"路径不合法：{ex.Message}"; }

        if (!IsInside(root, full))
            return $"只能读取你自己空间（{_shell.SandboxPath}）里的文件；「{path}」在空间外，不能读。";

        if (Directory.Exists(full)) return ListDirectory(root, full, path);

        if (!File.Exists(full))
        {
            var near = SuggestNearby(root, full);
            return $"文件不存在：{path}{near}";
        }

        FileInfo fi;
        byte[] bytes;
        try
        {
            fi = new FileInfo(full);
            var cap = (int)Math.Min(fi.Length, 4L * 1024 * 1024);   // 最多读 4MB
            using var fs = File.OpenRead(full);
            bytes = new byte[cap];
            int read = fs.Read(bytes, 0, cap);
            if (read < cap) bytes = bytes[..read];
        }
        catch (Exception ex)
        {
            return $"读取失败：{ex.Message}";
        }

        var rel = ToRel(root, full);

        if (LooksBinary(bytes, out var kind))
            return $"【{rel}】是{kind}（{HumanSize(fi.Length)}），不是文本文件，这里读不出可读内容。{BinaryHint(kind)}";

        var text = Decode(bytes, out var encName);
        var totalChars = text.Length;
        var truncated = totalChars > maxChars;
        if (truncated) text = text[..maxChars];
        var lines = text.Count(c => c == '\n') + (text.Length > 0 && !text.EndsWith('\n') ? 1 : 0);

        var head = $"【{rel}】{HumanSize(fi.Length)} · {lines} 行 · {encName}" +
                   (truncated ? $" · 内容过长已截断到 {maxChars} 字符（可传 maxChars 调大，上限 {HardMaxChars}）" : "");
        return head + "\n--------\n" + text;
    }

    /// <summary>列目录（目录在前，带大小与时间）</summary>
    private static string ListDirectory(string root, string full, string path)
    {
        try
        {
            var rel = ToRel(root, full);
            var dirs = Directory.GetDirectories(full).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            var files = Directory.GetFiles(full).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            var sb = new StringBuilder($"【{rel}】目录：{dirs.Count} 个子目录 · {files.Count} 个文件\n");
            var shown = 0;
            foreach (var d in dirs)
            {
                if (shown++ >= 200) break;
                sb.Append("  [目录] ").Append(Path.GetFileName(d)).Append("/\n");
            }
            foreach (var f in files)
            {
                if (shown++ >= 200) break;
                var fi = new FileInfo(f);
                sb.Append("  ").Append(Path.GetFileName(f))
                  .Append("  (").Append(HumanSize(fi.Length)).Append(" · ").Append(fi.LastWriteTime.ToString("MM-dd HH:mm")).Append(")\n");
            }
            if (dirs.Count + files.Count > shown) sb.Append("  …（其余未显示）\n");
            return sb.ToString().TrimEnd('\n');
        }
        catch (Exception ex)
        {
            return $"列目录失败：{ex.Message}";
        }
    }

    /// <summary>路径不存在时，列出同目录里的候选（最多 20 条），帮她一次找对文件</summary>
    private static string SuggestNearby(string root, string full)
    {
        try
        {
            var dir = Path.GetDirectoryName(full);
            if (dir is null || !Directory.Exists(dir)) return "（该目录也不存在，先用 read_file 传目录路径看看你的空间结构）";
            var entries = Directory.GetFileSystemEntries(dir)
                                   .Select(Path.GetFileName)
                                   .Where(x => x is not null)
                                   .Take(20)
                                   .ToList();
            if (entries.Count == 0) return $"（同目录 {ToRel(root, dir)} 是空的）";
            return $"（同目录 {ToRel(root, dir)} 里有：{string.Join("、", entries!)}）";
        }
        catch
        {
            return "";
        }
    }

    private static bool LooksBinary(byte[] b, out string kind)
    {
        kind = "";
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) { kind = "PNG 图片"; return true; }
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) { kind = "JPEG 图片"; return true; }
        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F') { kind = "GIF 图片"; return true; }
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W') { kind = "WebP 图片"; return true; }
        if (b.Length >= 4 && b[0] == '%' && b[1] == 'P' && b[2] == 'D' && b[3] == 'F') { kind = "PDF 文档"; return true; }
        if (b.Length >= 4 && b[0] == 'P' && b[1] == 'K' && (b[2] == 3 || b[2] == 5 || b[2] == 7)) { kind = "压缩包/Office 文档（zip 容器）"; return true; }
        if (b.Length >= 2 && b[0] == 'M' && b[1] == 'Z') { kind = "可执行文件"; return true; }
        // 没有明显魔数时：前面出现 NUL 字节也算二进制
        int check = Math.Min(b.Length, 8000);
        for (int i = 0; i < check; i++)
            if (b[i] == 0) { kind = "二进制文件"; return true; }
        return false;
    }

    private static string BinaryHint(string kind)
    {
        if (kind.Contains("图片"))
            return "想看画面内容就用识图；想看这张图的生成参数/EXIF，请读与它同名的 .txt（元数据已转存成文本）。";
        if (kind.Contains("Office") || kind.Contains("PDF"))
            return "这类文档需要专门解析，目前读不了文本内容。";
        return "";
    }

    /// <summary>解码：BOM → UTF-8（严格）→ GBK(936) → Latin1</summary>
    private static string Decode(byte[] bytes, out string encName)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encName = "UTF-8 BOM";
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encName = "UTF-16LE";
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        try
        {
            var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
            var utf8 = strict.GetString(bytes);
            encName = "UTF-8";
            return utf8;
        }
        catch (DecoderFallbackException) { /* 不是合法 UTF-8，继续试 GBK */ }

        var gbk = DecodeCodePage(bytes, 936);
        if (gbk is not null)
        {
            encName = "GBK";
            return gbk;
        }
        encName = "Latin1（无法识别编码，中文可能乱码）";
        return Encoding.Latin1.GetString(bytes);
    }

    /// <summary>用 Win32 MultiByteToWideChar 解码指定代码页（无需额外的 CodePages 包）</summary>
    private static string? DecodeCodePage(byte[] bytes, uint codePage)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            int need = MultiByteToWideChar(codePage, 0, bytes, bytes.Length, null, 0);
            if (need <= 0) return null;
            var buf = new char[need];
            int got = MultiByteToWideChar(codePage, 0, bytes, bytes.Length, buf, need);
            return got <= 0 ? null : new string(buf, 0, got);
        }
        catch
        {
            return null;
        }
    }

    // CharSet.Unicode 必须显式指定：否则 char[] 会按 ANSI 语义来回转换，GBK 解出来是乱码
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MultiByteToWideChar(uint codePage, uint dwFlags, byte[] lpMultiByteStr,
        int cbMultiByte, [Out] char[]? lpWideCharStr, int cchWideChar);

    private static bool IsInside(string root, string full) =>
        full.Equals(root, StringComparison.OrdinalIgnoreCase)
        || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string ToRel(string root, string full) =>
        full.Equals(root, StringComparison.OrdinalIgnoreCase)
            ? "."
            : Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');

    private static string HumanSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.##} MB"
    };
}
