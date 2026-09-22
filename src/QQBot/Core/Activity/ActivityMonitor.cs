using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace QQBot.Core.Activity;

/// <summary>
/// 静静感知到的"主人此刻在干什么"：
///   Normal=一般（在电脑前干别的） / Game=在打游戏 / Away=不在电脑前。
/// </summary>
public enum ActivityMode
{
    Normal,
    Game,
    Away,
}

/// <summary>
/// 一次采样的结果（给自主活动、面板、日志共用）。
/// 全部信号都是免管理员、免驱动的 Win32/PDH/注册表读取。
/// </summary>
public sealed record ActivitySnapshot
{
    public required ActivityMode Mode { get; init; }
    public required int Score { get; init; }
    public required string Reason { get; init; }

    /// <summary>键鼠是否还在动（= 人还在电脑前）</summary>
    public required bool OwnerAtPc { get; init; }
    /// <summary>键鼠静默了多久（秒）</summary>
    public required double IdleSeconds { get; init; }

    public required string ForegroundProcess { get; init; }
    public required string ForegroundTitle { get; init; }
    public required bool ForegroundFullscreen { get; init; }
    public required bool ExclusiveFullscreen { get; init; }
    public required bool MouseClipped { get; init; }

    public required double ForegroundGpu { get; init; }
    public required double TotalGpu { get; init; }
    public required int? SteamAppId { get; init; }

    public required DateTime SampledAtUtc { get; init; }

    /// <summary>采样失败/还没采过时的占位（一切按"一般模式"处理）</summary>
    public static ActivitySnapshot Unknown { get; } = new()
    {
        Mode = ActivityMode.Normal,
        Score = 0,
        Reason = "还没采样",
        OwnerAtPc = true,
        IdleSeconds = 0,
        ForegroundProcess = "",
        ForegroundTitle = "",
        ForegroundFullscreen = false,
        ExclusiveFullscreen = false,
        MouseClipped = false,
        ForegroundGpu = 0,
        TotalGpu = 0,
        SteamAppId = null,
        SampledAtUtc = DateTime.MinValue,
    };

    /// <summary>主人现在玩的是什么（能认出来就认，认不出给进程名）</summary>
    public string GameHint =>
        SteamAppId is int id ? $"Steam 游戏（AppID {id}，进程 {ForegroundProcess}）" :
        ForegroundProcess.Length > 0 ? ForegroundProcess : "未知游戏";

    /// <summary>一行摘要（喂给 LLM / 打日志 / 面板小卡片都用它）</summary>
    public string Summary
    {
        get
        {
            var where = OwnerAtPc
                ? (IdleSeconds < 60 ? "在电脑前（刚还在动）" : $"在电脑前（键鼠已静默 {IdleSeconds / 60:0.#} 分钟）")
                : $"不在电脑前（键鼠静默 {IdleSeconds / 60:0.#} 分钟）";
            var fg = ForegroundProcess.Length > 0
                ? $"前台：{ForegroundProcess}｜{(ForegroundTitle.Length > 30 ? ForegroundTitle[..30] + "…" : ForegroundTitle)}"
                : "前台：未知";
            var gpu = ForegroundGpu > 0.5 ? $"｜前台 GPU {ForegroundGpu:0}%（全机 {TotalGpu:0}%）" : "";
            var game = SteamAppId is int id ? $"｜Steam 正在跑 AppID {id}" : "";
            return $"{ModeText}｜{where}｜{fg}{gpu}{game}";
        }
    }

    public string ModeText => Mode switch
    {
        ActivityMode.Game => "游戏中",
        ActivityMode.Away => "看家",
        _ => "一般",
    };
}

/// <summary>
/// 主机活动采样器：键鼠空闲、前台窗口、GPU 占用、全屏状态、鼠标剪裁、Steam 在跑什么。
///
/// ⚠️ PDH 的 GPU 计数器是 rate 类型，**必须两次采样（间隔约 1 秒）才有值**，
/// 所以这里把 query/counter 常驻，让调用方按自己的节奏反复 <see cref="Sample"/>——
/// 监测服务本身就是每 30 秒一轮，等于白拿。
///
/// ⚠️ 不用"进程加载了 dxgi/d3d11"当判据：实测 Chromium 应用也会加载这些（连 NVIDIA 捕获库都挂着）。
/// </summary>
public sealed class ActivityMonitor : IDisposable
{
    private readonly object _lock = new();
    private IntPtr _query = IntPtr.Zero;
    private IntPtr _counter = IntPtr.Zero;
    private bool _pdhReady;

    /// <summary>采一次样（不判定模式——判定要结合配置，由调用方按阈值决定）</summary>
    public ActivitySampleResult Sample()
    {
        lock (_lock)
        {
            var idle = ReadIdleSeconds();
            var fg = DescribeForeground();
            var gpu = ReadGpu();
            SHQueryUserNotificationState(out var qs);

            var clipW = 0; var clipH = 0;
            try
            {
                if (GetClipCursor(out var clip)) { clipW = clip.Right - clip.Left; clipH = clip.Bottom - clip.Top; }
            }
            catch { /* 失败就当没限制 */ }

            var steam = ReadSteamAppId();

            double fgGpu = 0, totalGpu = 0;
            if (gpu is not null)
            {
                totalGpu = gpu.Values.Sum();
                if (fg is not null) gpu.TryGetValue(fg.Pid, out fgGpu);
            }

            var mouseClipped = fg is not null && clipW > 0 && (clipW < fg.MonW || clipH < fg.MonH);

            return new ActivitySampleResult
            {
                IdleSeconds = idle,
                Foreground = fg,
                ExclusiveFullscreen = qs == 3,          // QUNS_RUNNING_D3D_FULL_SCREEN
                NotificationBusy = qs is 2 or 4,        // BUSY / PRESENTATION
                MouseClipped = mouseClipped,
                ForegroundGpu = fgGpu,
                TotalGpu = totalGpu,
                SteamAppId = steam,
                GpuAvailable = gpu is not null,
            };
        }
    }

    // ---------------- 采样细节 ----------------

    private static double ReadIdleSeconds()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref li)) return 0;
        var ms = unchecked((uint)(Environment.TickCount - (int)li.dwTime));
        return ms / 1000.0;
    }

    private static readonly string[] QunsText =
        ["", "不在", "全屏/勿扰", "独占全屏", "投影", "正常", "安静时间", "-", "Windows 应用"];

    public static string QunsName(int state) => QunsText.ElementAtOrDefault(state) ?? state.ToString();

    /// <summary>读一次 GPU 占用（按 PID 汇总）。第一次（或计数器没准备好）返回 null</summary>
    private Dictionary<int, double>? ReadGpu()
    {
        if (!_pdhReady && !InitPdh()) return null;

        if (PdhCollectQueryData(_query) != 0) return null;

        uint size = 0, count = 0;
        var rc = PdhGetFormattedCounterArrayW(_counter, PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero);
        if (size == 0) return null;
        if (rc != 0x800007D2 /*PDH_MORE_DATA*/ && rc != 0) return null;

        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            rc = PdhGetFormattedCounterArrayW(_counter, PDH_FMT_DOUBLE, ref size, out count, buf);
            if (rc != 0) return null;

            var byPid = new Dictionary<int, double>();
            var itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_W>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_W>(buf + i * itemSize);
                var pid = ParsePid(Marshal.PtrToStringUni(item.szName) ?? "");
                if (pid <= 0 || item.FmtValue.CStatus != 0) continue;
                byPid.TryGetValue(pid, out var cur);
                byPid[pid] = cur + item.FmtValue.DoubleValue;
            }
            return byPid;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private bool InitPdh()
    {
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) return false;
            if (PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter) != 0)
            {
                PdhCloseQuery(_query);
                _query = IntPtr.Zero;
                return false;
            }
            PdhCollectQueryData(_query);   // 第一次采集只作为基线
            _pdhReady = true;
            return true;
        }
        catch
        {
            _pdhReady = false;
            return false;
        }
    }

    /// <summary>实例名形如 pid_12345_luid_0x00000000_0x0000ABCD_phys_0_eng_0_engtype_3D</summary>
    private static int ParsePid(string instance)
    {
        const string tag = "pid_";
        var i = instance.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return -1;
        i += tag.Length;
        var j = i;
        while (j < instance.Length && char.IsDigit(instance[j])) j++;
        return int.TryParse(instance.AsSpan(i, j - i), out var pid) ? pid : -1;
    }

    /// <summary>Steam 正在运行的游戏（HKCU\Software\Valve\Steam\RunningAppID；0=没在玩）</summary>
    private static int? ReadSteamAppId()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (k?.GetValue("RunningAppID") is int v && v > 0) return v;
        }
        catch { /* 没装 Steam / 读不到就当没有 */ }
        return null;
    }

    /// <summary>前台窗口画像（进程名、标题、是否整屏无边框、所在显示器尺寸）</summary>
    public static ForegroundInfo? DescribeForeground()
    {
        try
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return null;
            GetWindowThreadProcessId(h, out var pid);

            var title = new StringBuilder(300);
            GetWindowTextW(h, title, title.Capacity);
            var cls = new StringBuilder(200);
            GetClassNameW(h, cls, cls.Capacity);

            GetWindowRect(h, out var r);
            var w = r.Right - r.Left;
            var hh = r.Bottom - r.Top;

            var mon = MonitorFromWindow(h, 2 /*NEAREST*/);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfoW(mon, ref mi);
            var mw = mi.rcMonitor.Right - mi.rcMonitor.Left;
            var mh = mi.rcMonitor.Bottom - mi.rcMonitor.Top;

            var style = GetWindowLongPtr(h, GWL_STYLE);
            const int WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
            var noCaption = (style & WS_CAPTION) != WS_CAPTION && (style & WS_THICKFRAME) == 0;

            // ⚠️ 不能用"严格覆盖"：实测最大化窗口 2576x1408 vs 显示器 2560x1440（宽超、高不足）
            var covers = w >= mw * 0.97 && hh >= mh * 0.97;

            var proc = "";
            try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { }

            return new ForegroundInfo((int)pid, proc, title.ToString(), cls.ToString(), covers, noCaption, w, hh, mw, mh);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_query != IntPtr.Zero) { try { PdhCloseQuery(_query); } catch { } }
            _query = IntPtr.Zero;
            _counter = IntPtr.Zero;
            _pdhReady = false;
        }
    }

    // ---------------- 采样结果 ----------------

    public sealed record ActivitySampleResult
    {
        public required double IdleSeconds { get; init; }
        public required ForegroundInfo? Foreground { get; init; }
        public required bool ExclusiveFullscreen { get; init; }
        public required bool NotificationBusy { get; init; }
        public required bool MouseClipped { get; init; }
        public required double ForegroundGpu { get; init; }
        public required double TotalGpu { get; init; }
        public required int? SteamAppId { get; init; }
        public required bool GpuAvailable { get; init; }
    }

    public sealed record ForegroundInfo(int Pid, string Process, string Title, string Class,
                                        bool CoversMonitor, bool NoCaption, int W, int H, int MonW, int MonH);

    // ---------------- Win32 / PDH ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern bool GetClipCursor(out RECT r);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

    // GetWindowLongPtr 在 64 位下才是长指针；用 EntryPoint 指到 64 位实现
    private const int GWL_STYLE = -16;
    private static int GetWindowLongPtr(IntPtr h, int index)
        => IntPtr.Size == 8 ? (int)GetWindowLongPtr64(h, index) : GetWindowLong(h, index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr h, int index);

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM_W
    {
        public IntPtr szName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    private const uint PDH_FMT_DOUBLE = 0x00000200;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? src, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
