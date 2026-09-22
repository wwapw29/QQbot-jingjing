using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using QQBot.Core.Options;
using QQBot.Core.Vision;

namespace QQBot.Core.Tools;

/// <summary>一块显示器的信息（给面板下拉和工具说明用）</summary>
public sealed record MonitorInfo(int Index, int X, int Y, int Width, int Height, bool Primary)
{
    /// <summary>人看得懂的标签：① 2560x1440（主屏，位于 0,0）</summary>
    public string Label => $"{Index} · {Width}x{Height}{(Primary ? "（主屏）" : "")}（位置 {X},{Y}）";
}

/// <summary>
/// capture_screen —— 截取主人电脑的屏幕，让静静"看见你在做什么"。
///
/// **仅主人可用**（OwnerOnly=true）：客人的工具清单里根本不会出现它，硬调用也会被拒，
/// 且不受 Tools.GuestAllowed 白名单影响。
///
/// **截哪块屏**由后台「屏幕截图 → 默认截取的屏幕」配置（Tools.ScreenCaptureMonitor：
/// 0=所有屏幕拼成整幅，1/2…=第几块），主人在对话里明确要求时她也可以临时传 monitor 参数覆盖。
/// 流程：截图 →（按 maxWidth 等比缩小）→ JPEG 存进她的空间 → 交给识图模型看一眼 → 把描述还给她。
/// </summary>
public sealed class ScreenCaptureTool : ITool
{
    private readonly VisionService _vision;
    private readonly ToolsOptions _options;
    private readonly ILogger<ScreenCaptureTool> _logger;

    public ScreenCaptureTool(VisionService vision, ToolsOptions options, ILogger<ScreenCaptureTool> logger)
    {
        _vision = vision;
        _options = options;
        _logger = logger;
    }

    public string Name => "capture_screen";

    /// <summary>隐私工具：只给主人</summary>
    public bool OwnerOnly => true;

    public string Description
    {
        get
        {
            var def = _options.ScreenCaptureMonitor;
            var monitors = ListMonitors();
            var list = monitors.Count == 0
                ? "（没枚举到显示器）"
                : string.Join("；", monitors.Select(m => m.Label));
            var defDesc = def <= 0
                ? "所有屏幕拼成的整幅桌面"
                : (def <= monitors.Count ? $"第 {def} 块（{monitors[def - 1].Label}）" : $"第 {def} 块（当前没这么多屏）");

            return "截取主人电脑的屏幕画面，并让识图模型描述出来——用来了解主人此刻在做什么（在写代码？在看视频？在聊天？）。" +
                   "当你想主动关心主人、想知道他是不是在忙、想找话题时调用。\n" +
                   $"【当前显示器】{list}\n" +
                   $"【默认截取】{defDesc}（主人可以在后台改；**主人没明确让你看另一块屏时，别传 monitor 参数，用默认的就行**）\n" +
                   "注意：这是隐私操作，只在主人自己的会话里可用；别频繁截，也别把和当前话题无关的屏幕内容到处说。";
        }
    }

    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["monitor"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "想看第几块显示器时才传（1=第一块，2=第二块…）；0=所有屏幕拼接。" +
                                  "默认不传 = 用后台设置好的那块屏"
            },
            ["maxWidth"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "保存的图片最大宽度（像素；不传则用后台设置）"
            },
            ["describe"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "是否让识图模型描述画面（默认 true）。false 只截图不描述，返回文件路径"
            }
        },
        ["additionalProperties"] = false
    };

    public async Task<string> ExecuteAsync(string argsJson, ToolContext ctx, CancellationToken ct)
    {
        var args = JsonNode.Parse(argsJson) as JsonObject;
        // monitor 缺省（null）→ 用后台配置的那块屏；显式传值（含 0）→ 按她说的来
        var monitor = args?["monitor"]?.GetValue<int>() ?? _options.ScreenCaptureMonitor;
        var maxWidth = Math.Clamp(args?["maxWidth"]?.GetValue<int>() ?? _options.ScreenCaptureMaxWidth, 320, 4096);
        var describe = args?["describe"]?.GetValue<bool>() ?? true;

        byte[] jpeg;
        int w, h;
        try
        {
            var rect = ResolveRect(monitor);
            (jpeg, w, h) = Capture(rect, maxWidth);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "截图失败");
            return $"截图失败：{ex.Message}（可能是当前会话没有桌面访问权限——比如跑在服务/会话 0 里）";
        }

        var where = DescribeChoice(monitor);
        var saved = _vision.SaveJpegToSpace(jpeg, "屏幕截图");
        var sizeKb = jpeg.Length / 1024;
        _logger.LogInformation("截图完成：{Where} {W}x{H} {Kb}KB → {Path}", where, w, h, sizeKb, saved);

        var head = $"已截取{where}（{w}x{h}，{sizeKb}KB），保存为：{saved ?? "（存档失败）"}";
        if (!describe)
            return head + "\n（本次没让识图模型看；需要的话再调用一次让我看，或用 read_file 读这个文件）";

        var desc = await _vision.DescribeImageBytesAsync(jpeg,
            "这是主人此刻电脑屏幕的截图。请描述：主人正在做什么？屏幕上主要是什么程序/网页/内容？" +
            "如果是代码、文档或聊天窗口，简单说明是什么内容。不用逐字念，抓住重点。", ct);

        if (string.IsNullOrWhiteSpace(desc))
            return head + "\n【注意】识图模型这次没能给出描述（可能没配识图模型/调用失败），所以你看不到画面内容。";

        return head + $"\n【你看到的画面】{desc}";
    }

    /// <summary>
    /// 供其它服务复用：按"第几块屏"截一张 JPEG（已按 maxWidth 缩放），不落盘、不调识图。
    /// 自主活动的"游戏模式自动截屏"用它。
    /// </summary>
    public static (byte[] Jpeg, int W, int H) CaptureJpeg(int monitor, int maxWidth)
        => Capture(ResolveRect(monitor), Math.Clamp(maxWidth, 320, 4096));

    /// <summary>把"第几块屏"翻译成人话（截图结果里告诉她截的是哪块）</summary>
    private static string DescribeChoice(int monitor)
    {
        if (monitor <= 0) return "整幅桌面（所有屏幕）";
        var monitors = ListMonitors();
        return monitor <= monitors.Count ? $"第 {monitor} 块屏幕（{monitors[monitor - 1].Label}）" : $"第 {monitor} 块屏幕";
    }

    // ---------------- 屏幕捕获 ----------------

    /// <summary>枚举所有显示器（面板下拉 & 工具说明都用它；失败返回空列表）</summary>
    public static List<MonitorInfo> ListMonitors()
    {
        var list = new List<MonitorInfo>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref RECT _, IntPtr _) =>
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(hMonitor, ref mi))
                {
                    list.Add(new MonitorInfo(list.Count + 1, mi.rcMonitor.Left, mi.rcMonitor.Top,
                        mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                        (mi.dwFlags & 1) != 0));   // MONITORINFOF_PRIMARY = 1
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { /* 枚举失败走虚拟桌面兜底 */ }
        return list;
    }

    /// <summary>虚拟桌面（所有屏幕拼成的大矩形）尺寸</summary>
    public static (int X, int Y, int W, int H) VirtualScreen()
        => (GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    private static RECT ResolveRect(int monitor)
    {
        if (monitor > 0)
        {
            var monitors = ListMonitors();
            if (monitors.Count == 0) throw new InvalidOperationException("没有枚举到显示器");
            if (monitor > monitors.Count) throw new InvalidOperationException($"只有 {monitors.Count} 块显示器");
            var m = monitors[monitor - 1];
            return new RECT { Left = m.X, Top = m.Y, Right = m.X + m.Width, Bottom = m.Y + m.Height };
        }

        var (x, y, w, h) = VirtualScreen();
        if (w <= 0 || h <= 0) throw new InvalidOperationException("取屏幕尺寸失败");
        return new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }

    /// <summary>截图并按 maxWidth 等比缩小，返回 JPEG 字节与最终尺寸</summary>
    private static (byte[] Jpeg, int W, int H) Capture(RECT rect, int maxWidth)
    {
        var w = Math.Max(1, rect.Right - rect.Left);
        var h = Math.Max(1, rect.Bottom - rect.Top);

        using var full = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full))
        {
            g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        }

        Bitmap output = full;
        Bitmap? scaled = null;
        if (w > maxWidth)
        {
            var nw = maxWidth;
            var nh = Math.Max(1, (int)Math.Round(h * (double)nw / w));
            scaled = new Bitmap(nw, nh, PixelFormat.Format24bppRgb);
            using var g2 = Graphics.FromImage(scaled);
            g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g2.DrawImage(full, 0, 0, nw, nh);
            output = scaled;
        }

        try
        {
            using var ms = new MemoryStream();
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ps = new EncoderParameters(1);
            ps.Param[0] = new EncoderParameter(Encoder.Quality, 80L);
            output.Save(ms, codec, ps);
            return (ms.ToArray(), output.Width, output.Height);
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    // ---------------- Win32 ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    // CharSet.Unicode 必须显式指定：默认按 ANSI 语义封送，结构里的 RECT 会错位
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
}
