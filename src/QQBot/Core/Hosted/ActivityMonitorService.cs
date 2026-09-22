using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QQBot.Core.Activity;
using QQBot.Core.Options;

namespace QQBot.Core.Hosted;

/// <summary>
/// 主机活动监测：每 PollSeconds 采一次样，判定主人处在「游戏 / 一般 / 看家」，供自主活动换挡。
///
/// 判定顺序（游戏优先，因为"打游戏挂机"键鼠也不动）：
///   ① Steam 正在跑游戏（AppID）→ 直接游戏模式，不管前台是什么
///   ② 前台是浏览器/IDE/终端这类"肯定不是游戏"的进程 → 不判游戏
///   ③ 整屏无边框/独占全屏 + 前台 GPU 吃满（+鼠标被锁）累计够分 → 游戏模式
///   ④ 键鼠静默超过 AwayAfterMinutes → 看家模式
///   ⑤ 其余 → 一般模式
/// </summary>
public sealed class ActivityMonitorService : BackgroundService
{
    private readonly ActivityOptions _options;
    private readonly ILogger<ActivityMonitorService> _logger;
    private readonly ActivityMonitor _monitor = new();

    private volatile ActivitySnapshot _latest = ActivitySnapshot.Unknown;

    public ActivityMonitorService(BotOptions options, ILogger<ActivityMonitorService> logger)
    {
        _options = options.Activity;
        _logger = logger;
    }

    /// <summary>最近一次判定（永远有值；还没采过样时是"一般模式"占位）</summary>
    public ActivitySnapshot Latest => _latest;

    /// <summary>这些前台进程"肯定不是在打游戏"，避免把全屏 IDE / 浏览器里跑的生图误判成游戏</summary>
    private static readonly HashSet<string> NonGameProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "iexplore", "workbuddy", "qq", "wechat", "tim",
        "explorer", "code", "devenv", "rider64", "idea64", "pycharm64", "webstorm64", "sublime_text", "notepad",
        "windowsterminal", "wt", "powershell", "pwsh", "cmd", "conhost", "mintty", "alacritty", "wezterm",
        "mstsc", "vmware-vmx", "virtualboxvm", "obs64", "obs32", "photoshop", "illustrator", "blender",
        "excel", "winword", "powerpnt", "wps", "acrobat", "sumatrapdf", "potplayer", "vlc", "mpc-hc64", "mpv",
    };

    /// <summary>手动强制模式（ForceMode=game/normal/away 时直接用它）</summary>
    private ActivityMode? ForcedMode => _options.ForceMode?.Trim().ToLowerInvariant() switch
    {
        "game" => ActivityMode.Game,
        "normal" => ActivityMode.Normal,
        "away" => ActivityMode.Away,
        _ => null,
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ActivityMode? lastMode = null;
        _logger.LogInformation("主机活动监测已启动：每 {Sec} 秒采样一次（键鼠空闲/前台窗口/GPU/全屏/Steam）", Math.Max(5, _options.PollSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_options.Enabled)
                {
                    if (lastMode != ActivityMode.Normal || _latest.SampledAtUtc == DateTime.MinValue)
                    {
                        _latest = ActivitySnapshot.Unknown with { Reason = "监测已关闭" };
                        lastMode = ActivityMode.Normal;
                        _logger.LogInformation("主机活动监测已关闭：一律按一般模式跑");
                    }
                }
                else
                {
                    var snap = SampleAndDecide();
                    _latest = snap;

                    if (lastMode != snap.Mode)
                    {
                        _logger.LogInformation("活动模式：{From} → {To}（{Reason}）｜{Summary}",
                            lastMode?.ToString() ?? "初始", snap.Mode, snap.Reason, snap.Summary);
                        lastMode = snap.Mode;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "活动监测采样失败（保持上一次判定）");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(_options.PollSeconds, 5, 3600)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>立刻采一次样并判定（面板/测试要"马上看结果"时也用它）</summary>
    public ActivitySnapshot SampleAndDecide()
    {
        var s = _monitor.Sample();
        return Decide(s);
    }

    private ActivitySnapshot Decide(ActivityMonitor.ActivitySampleResult s)
    {
        var opt = _options;
        var fg = s.Foreground;
        var atPc = s.IdleSeconds < Math.Max(1, opt.AwayAfterMinutes) * 60.0;
        var fgName = fg?.Process ?? "";

        var reasons = new List<string>();
        var score = 0;

        var fullscreenish = s.ExclusiveFullscreen || (fg?.CoversMonitor == true && fg.NoCaption);
        var gpuHeavy = s.ForegroundGpu >= opt.GameGpuThreshold;
        var gpuBusy = s.TotalGpu >= opt.GameGpuThreshold;

        if (s.SteamAppId is int appId) { score += 4; reasons.Add($"Steam 正在跑 AppID {appId}"); }
        if (fullscreenish) { score += 2; reasons.Add(s.ExclusiveFullscreen ? "独占全屏" : "前台整屏无边框"); }
        if (gpuHeavy) { score += 3; reasons.Add($"前台 GPU {s.ForegroundGpu:0}%"); }
        else if (gpuBusy) { score += 1; reasons.Add($"全机 GPU {s.TotalGpu:0}%"); }
        if (s.MouseClipped) { score += 1; reasons.Add("鼠标被锁在窗口内"); }

        var looksLikeNonGame = NonGameProcesses.Contains(fgName);
        var isGame = s.SteamAppId is not null || (score >= 4 && !looksLikeNonGame);
        if (!isGame && score > 0 && looksLikeNonGame)
            reasons.Add($"但前台是 {fgName}（非游戏进程），不按游戏算");

        var mode = ForcedMode ?? (isGame ? ActivityMode.Game
                                     : !atPc ? ActivityMode.Away
                                     : ActivityMode.Normal);
        if (ForcedMode is not null) reasons.Insert(0, "手动锁定模式");

        return new ActivitySnapshot
        {
            Mode = mode,
            Score = score,
            Reason = reasons.Count == 0 ? "没有任何游戏特征" : string.Join(" / ", reasons),
            OwnerAtPc = atPc,
            IdleSeconds = s.IdleSeconds,
            ForegroundProcess = fgName,
            ForegroundTitle = fg?.Title ?? "",
            ForegroundFullscreen = fullscreenish,
            ExclusiveFullscreen = s.ExclusiveFullscreen,
            MouseClipped = s.MouseClipped,
            ForegroundGpu = s.ForegroundGpu,
            TotalGpu = s.TotalGpu,
            SteamAppId = s.SteamAppId,
            SampledAtUtc = DateTime.UtcNow,
        };
    }

    public override void Dispose()
    {
        _monitor.Dispose();
        base.Dispose();
    }
}
