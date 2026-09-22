using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;   // Thumb 的拖拽事件参数
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopPet.Pet;
using DesktopPet.UI;

namespace DesktopPet;

/// <summary>
/// 宠物主窗口（透明置顶、可拖拽、可贴边）：
///  - 加载皮肤 PNG（按 pet.json 的动作帧序列播放）
///  - 静止时"呼吸"浮动，看起来是活的
///  - 左键拖动（松手自动吸附屏幕边缘）、双击说话、右键菜单
///  - 说的话交给 QQBot 的 /api/pet/chat，回到的每句话逐条冒气泡
/// Tips：位置会自动记住（存在用户目录，不动素材目录里的 pet.json）
/// </summary>
public partial class MainWindow : Window
{
    private readonly PetConfig _cfg = App.Config;

    /// <summary>与后端的通道（她的大脑在 QQBot 那边；桌面端只管显示）</summary>
    private readonly PetBackend _backend = new(App.Config.Backend);

    /// <summary>帧缓存：动作名 → 已解码的 BitmapImage 列表</summary>
    private readonly Dictionary<string, ActionFrames> _frameCache = new(StringComparer.OrdinalIgnoreCase);

    private DispatcherTimer? _frameTimer;        // 逐帧播放
    private double _frameFps = 4;                // 当前动作的 fps（只用于"没有自带延时"的帧）
    private int _frameLogLeft;                   // 还剩几帧要打"实际间隔"（校对 GIF 节奏用）
    private DateTime _frameLastAt;               // 上一帧的换帧时刻
    private DispatcherTimer? _breathTimer;       // 呼吸浮动
    private DispatcherTimer? _snapTimer;         // 贴边滑动动画
    private string _currentAction = "idle";
    private int _frameIndex;
    private int _framesLeft;
    private bool _holdAction;                    // 当前动作是"一直循环"（说话时的 talk 用它）
    private bool _talkingLoop;                   // 此刻正处在"逐字说话"的 talk 循环里
    private string? _returnAction;               // 当前这个"一次性"动作播完回到哪儿（thinking / idle）
    private bool _thinking;                      // 此刻正处在"等她回话"的 thinking 循环里
    private bool _awaitingReply;                 // 请求已发出、还没拿到回复（这段里 thinking 不许被打断）
    private CancellationTokenSource? _toolPollCts;   // 等待回复期间的工具轮询
    private long _toolHintSeq = -1;              // 已经处理过的"工具调用序号"
    private long _inboxToolSeq = -1;             // 心跳里带回来的工具序号（-1=还没收到过，第一次只对齐不播）
    private string? _pendingEmotion;             // 这句话出完字之后要播的表情动作
    private TaskCompletionSource? _typingDone;   // 这句话"出完字"的信号（连播要等它）
    private TaskCompletionSource? _bubbleGone;   // 这个气泡"消失了"的信号（连播要等它走完自己的停留时长）
    private double _naturalWidth = 1, _naturalHeight = 1;
    private double _breathPhase;

    /// <summary>正在等后端回话（期间不接第二句，免得气泡打架）</summary>
    private bool _chatting;
    private bool _broadcasting;   // 正在连播多段回复（等待可能有点久，提示语会据此变化）

    /// <summary>轮询她的主动消息（同时也是"精灵在线"的心跳）</summary>
    private DispatcherTimer? _inboxTimer;
    private long _inboxSeq;                       // 已收到的最后一条序号（去重）
    private bool _inboxSynced;                    // 首次轮询是否已对齐游标（防重启复读，见 PollInboxAsync）
    private bool _polling;                        // 防重入（上一次还没回来就别发下一次）
    private bool _inboxWarned;                    // 断线只提醒一次，别每 3 秒刷日志
    private readonly List<PetReplyItem> _heldPushes = new();   // 忙着/藏着时收到的，等空下来再播

    /// <summary>已套用的后台配置版本（-1=还没拉过）；心跳发现版本变了就重新拉一份并套用</summary>
    private int _appliedConfigVersion = -1;

    /// <summary>上一次"动作帧找不到文件"的提示内容——同一份错误只弹一次</summary>
    private string? _lastSkinWarn;

    // 上次看到的"面板值"——用来判断面板这次到底改了哪个字段（见 ApplyRemoteConfigAsync）
    private double? _lastRemoteScale;
    private bool? _lastRemoteLocked;
    private bool? _lastRemoteClickThrough;
    private bool? _lastRemoteSnap;

    /// <summary>当前气泡（同一时间只显示一个）</summary>
    private BubbleWindow? _bubble;

    /// <summary>输入框（双击唤出）</summary>
    private InputWindow? _input;

    public MainWindow()
    {
        InitializeComponent();
        Topmost = _cfg.AlwaysOnTop;
        Loaded += OnLoaded;
        MouseDoubleClick += (_, _) => ShowInputBox();
    }

    // ---------------- 启动 ----------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadSkin();
        RestorePosition();
        // 「登场」要等真正的皮肤就位再播：开机时本机 pet.json 指向的是占位素材（主人说的那个"钢坦克"），
        // 后台面板里那套真素材要晚 1~3 秒才套用上来。以前开机就播 → 你会先看见一小会儿占位图。
        // 现在：先隐身等后台皮肤到位（最多 4 秒；超时就用本机素材登场，后端没开也不至于一直空白）。
        var deadline = DateTime.UtcNow.AddSeconds(4);
        var startupTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        startupTimer.Tick += (_, _) =>
        {
            // 报错动画（比如开机就连不上后端）正在播 → 等它播完再登场，别互相打断
            if (_errorPlaying) return;
            if (!_remoteSkinReady && DateTime.UtcNow < deadline) return;
            startupTimer.Stop();
            PlayStartupOnce();
        };
        startupTimer.Start();
        StartBreathing();
        SnapToEdge(animate: false);          // 上次是贴着边关的，这次开机也贴回去
        IsVisibleChanged += (_, _) => OnVisibleChanged();
        _ = CheckBackendAsync();
        StartInboxPolling();
    }

    // ---------------- 收件箱轮询（她的主动消息 + 在线心跳） ----------------

    private void StartInboxPolling()
    {
        if (!_cfg.Backend.Enabled) return;
        var ms = Math.Clamp(_cfg.Backend.InboxPollMs, 1000, 60000);
        _inboxTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        _inboxTimer.Tick += async (_, _) => await PollInboxAsync();
        _inboxTimer.Start();
        PetLog.Info($"开始轮询她的主动消息（每 {ms}ms；这个轮询同时是「精灵在线」的心跳）");
    }

    private async Task PollInboxAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            // 带着"我露着脸没"一起报上去：藏着的时候后端会判她离线，她就改走 QQ 找你了
            var r = await _backend.PollInboxAsync(_inboxSeq, IsVisible);
            if (!r.Ok)
            {
                if (!_inboxWarned) { _inboxWarned = true; PlayError($"收件箱轮询失败：{r.Error}"); }
                return;
            }
            if (_inboxWarned) { _inboxWarned = false; PetLog.Info("收件箱已恢复"); }

            _inboxSeq = r.LastSeq;

            // 主人切到游戏模式 → 整只宠物鼠标穿透（防误触挡操作）
            ApplyActivityMode(r.ActivityMode);

            // 后台改了精灵配置 → 版本号变 → 拉一份套用（皮肤/气泡/吸附/缩放等）
            if (r.ConfigVersion != _appliedConfigVersion)
                await ApplyRemoteConfigAsync(r.ConfigVersion);

            // 她刚调了什么工具 → 播"工具绑定动作"。
            // 以前只有"主人正在等她回复"时才轮询 /api/pet/status，所以她在自主活动里做的
            // 事（游戏模式主动截屏、自己翻文件）桌面上完全没反应；现在跟着心跳一起带回来。
            // ⚠️ 第一次收到响应只对齐基线，不然开机就会把上一次活动的动作补播一遍。
            if (_inboxToolSeq < 0)
            {
                _inboxToolSeq = r.ToolSeq;
            }
            else if (r.ToolSeq > _inboxToolSeq)
            {
                _inboxToolSeq = r.ToolSeq;
                _toolHintSeq = Math.Max(_toolHintSeq, r.ToolSeq);   // 和"等回复期间"的轮询去重，别播两遍
                if (!_chatting) PlayToolAction(r.Tool);             // 对话中的交给 status 轮询，别打架
            }

            if (!_inboxSynced)
            {
                // 首次轮询**只对齐游标、不补播历史**。
                // 后端 PushesSince(since) 是"给我 seq > since 的"，而 _inboxSeq 初值是 0 ═ "我一条都没看过"，
                // 后端就会把 PushTtl 内留着的消息全给过来 → 每次重启她都要把上次说过的话再说一遍（复读）。
                // 她不在线的这段时间，后端本来就改走 QQ 找主人了，所以这里跳过不会漏消息。
                _inboxSynced = true;
                if (r.Pushes.Count > 0)
                    PetLog.Info($"启动对齐消息游标：跳过 {r.Pushes.Count} 条历史（本次不重播）");
                return;
            }

            if (r.Pushes.Count == 0) return;

            if (!IsVisible || _chatting)
            {
                _heldPushes.AddRange(r.Pushes);   // 先攒着，等露脸/说完再播
                return;
            }
            await PlayPushesAsync(r.Pushes);
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>
    /// 套用后台（QQBot 面板）下发的精灵配置：皮肤、动作帧、缩放、置顶、呼吸、气泡样式、吸附参数。
    /// 位置不动；backend 的地址/令牌也不动（那是本机连接设置，被改错就连不上、连不上就再也拿不到配置）。
    /// </summary>
    private async Task ApplyRemoteConfigAsync(int version)
    {
        var r = await _backend.GetConfigAsync();
        if (!r.Ok)
        {
            PlayError($"拉取后台精灵配置失败（v{version}）：{r.Error}");
            return;                                   // 不记版本，下次心跳再试
        }
        // 开机后第一次套用配置不算"主人改了我的样子"——不然每次重启她都要说一句
        // "……换好了，这样看着还行吧？"（用户会以为她在没话找话）
        var firstApply = _appliedConfigVersion < 0;
        _appliedConfigVersion = version;
        if (r.Config is null)
        {
            PetLog.Info("后台还没存过精灵配置，继续用本地 pet.json");
            return;
        }
        try
        {
            // 面板里只有"真的改了"的字段才允许覆盖本机值。否则每次开机第一次套用面板配置，
            // 都会把主人本机拖出来的尺寸 / 在托盘按过的开关，按面板里存的旧值冲回去
            // —— 表现就是"我调过的大小，每次打开又变回默认"。
            var ov = new PetConfig.RemoteOverrides(
                Scale:        PetConfig.PanelChanged(ref _lastRemoteScale, r.Config.Scale) || !_cfg.UserSetScale,
                Locked:       PetConfig.PanelChanged(ref _lastRemoteLocked, r.Config.Locked) || !_cfg.UserSetLocked,
                ClickThrough: PetConfig.PanelChanged(ref _lastRemoteClickThrough, r.Config.ClickThroughInGame) || !_cfg.UserSetClickThrough,
                Snap:         PetConfig.PanelChanged(ref _lastRemoteSnap, r.Config.Snap.Enabled) || !_cfg.UserSetSnap);
            PetLog.Info($"套用面板配置 v{version}：覆盖本机的字段 = {(ov.Any ? ov.ToString() : "无（本机值为准）")}");

            // 气泡样式**真的变了**才丢掉重建。以前是无条件 `_bubble?.Close()`——
            // 结果每次套用面板配置都会把"正在逐字说话"的气泡当场掐断（字没出完，停留计时和表情都走不到）。
            var bubbleBefore = JsonSerializer.Serialize(_cfg.Bubble);
            _cfg.ApplyRemote(r.Config, ov);
            if (ov.Any) _cfg.SaveUserConfig();        // 面板定的也落到本机，免得下次开机又被面板的旧值反冲
            if (JsonSerializer.Serialize(_cfg.Bubble) != bubbleBefore)
            {
                _bubble?.Close();
                _bubble = null;                       // 气泡样式变了 → 丢掉旧的，下次显示时按新样式重建
            }

            Topmost = _cfg.AlwaysOnTop;
            LoadSkin();                               // 皮肤目录/动作帧可能变了（内部会套用缩放）
            _remoteSkinReady = true;                  // 真皮肤就位（登场动画在等这个标记）
            SnapToEdge(animate: false);
            ResizeGrip.Opacity = _cfg.Locked ? 0 : ResizeGrip.Opacity;   // 远端改了锁定 → 手柄跟着显隐
        SyncGripVisibility();
            ApplyActivityMode(_activityMode == "" ? "normal" : _activityMode);   // 防误触开关可能被改过
            PetLog.Info($"已套用后台精灵配置 v{version}：名称={_cfg.Name} 皮肤={_cfg.AssetDir} 缩放={_cfg.Scale:0.00} " +
                        $"置顶={_cfg.AlwaysOnTop} 吸附={_cfg.Snap.Enabled}({_cfg.Snap.Threshold}px) " +
                        $"锁定={_cfg.Locked} 游戏防误触={_cfg.ClickThroughInGame}");
            // 动作部分被"纠正"过的地方（系统动作被删、工具重复绑定）——面板应该看不到，但万一有就记下来
            foreach (var w in _cfg.LastActionWarnings) PetLog.Warn("动作配置：" + w);
            _cfg.LastActionWarnings.Clear();
            if (!firstApply) ShowBubble("……换好了，这样看着还行吧？");
        }
        catch (Exception ex)
        {
            PlayError($"套用后台精灵配置失败：{ex.Message}");
        }
    }

    /// <summary>把她主动发来的消息逐条播出来</summary>
    private async Task PlayPushesAsync(List<PetReplyItem> pushes)
    {
        for (var i = 0; i < pushes.Count; i++)
        {
            var p = pushes[i];
            var text = p.DisplayText;
            PetLog.Info($"她主动说话：{(text.Length > 40 ? text[..40] + "…" : text)}");
            if (p.IsImage && !string.IsNullOrEmpty(p.DataUrl)) ShowPicture(p.DataUrl, p.Caption);
            else ShowBubble(text);

            await WaitTypingDoneAsync();     // 先把这句说完（逐字可能要好几秒）
            if (i >= pushes.Count - 1) break;   // 最后一句就让它挂着，按自己的停留时长消失
            await WaitBubbleStayedAsync((p.IsImage ? (p.Caption ?? "") : text).Length, p.IsImage);
            await WaitBubbleGapAsync(text.Length);
        }
    }

    /// <summary>攒下来的主动消息，等空下来（露脸 + 没在对话）再播</summary>
    private async Task TryFlushHeldAsync()
    {
        if (_chatting || !IsVisible || _heldPushes.Count == 0) return;
        var batch = _heldPushes.ToList();
        _heldPushes.Clear();
        await PlayPushesAsync(batch);
    }

    /// <summary>露脸/躲起来：立刻把状态告诉后端（不等下一次轮询），她都主动找你才不会投到看不见的地方</summary>
    private void OnVisibleChanged()
    {
        if (!_cfg.Backend.Enabled) return;
        if (IsVisible)
        {
            _ = TryFlushHeldAsync();
        }
        else
        {
            PetLog.Info("精灵躲起来了 → 告诉后端判定为离线（她主动说话会改走 QQ）");
            _ = _backend.MarkOfflineAsync();
        }
    }

    /// <summary>启动时探一下后端口，连不上就直说（别让人对着不说话的宠物发呆）</summary>
    private async Task CheckBackendAsync()
    {
        if (!_cfg.Backend.Enabled)
        {
            PetLog.Info("后端未启用（pet.json 的 backend.enabled=false），处于回显模式");
            return;
        }
        var (ok, info) = await _backend.PingAsync();
        PetLog.Info($"后端探测：{info}（{_backend.BaseUrl}）");
        if (!ok)
        {
            PlayError("连不上后端 " + info);
            ShowBubble($"呜……连不上 QQBot。\n{info}\n\n检查一下 {_backend.BaseUrl} 那边开着没。");
            return;
        }
        // 探测成功后**立刻**把面板配置拉下来套用：不然要等第一次心跳（3 秒），
        // 这 3 秒她会顶着本机 pet.json 的占位素材（主人看到的就是"开机先闪一下别的图"）
        var remote = await _backend.GetConfigAsync();
        if (remote.Ok && remote.Config is not null && remote.Version != _appliedConfigVersion)
            await ApplyRemoteConfigAsync(remote.Version);
    }

    /// <summary>加载皮肤：读动作帧 + 按缩放设置窗口尺寸</summary>
    private void LoadSkin()
    {
        // 先加载到临时表：**全部加载失败时不砸掉当前皮肤**（比如后台把皮肤目录改错了，
        // 至少她还站在桌面上，你能改回来）
        var loaded = new Dictionary<string, ActionFrames>(StringComparer.OrdinalIgnoreCase);
        var skinDir = _cfg.SkinDir(App.AppDir);
        PetLog.Info($"加载皮肤目录：{skinDir}");
        var missingNotes = new List<string>();

        foreach (var action in _cfg.Actions.Keys.ToList())
        {
            // 先看"配了但找不到"的帧：ResolveFrames 会把不存在的文件**悄悄跳过**，
            // 结果就是"面板里明明写了 talk，实际只加载出 idle"——这种静默降级必须喊出来
            // （2026-09-22 主人遇到的"talk 从来不播"就是这么来的：文件名少了个后缀）
            var configured = _cfg.Actions[action].Frames ?? new List<string>();
            var resolved = _cfg.ResolveFrames(App.AppDir, action);
            if (resolved.Count < configured.Count)
            {
                var have = new HashSet<string>(resolved.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
                var missing = configured.Where(f => !have.Contains(Path.GetFileName(f))).ToList();
                var tag = SystemActions.Is(action) ? "系统动作" : "动作";
                PetLog.Warn($"{tag}「{action}」有 {missing.Count} 个帧文件找不到（皮肤目录 {skinDir}）：{string.Join("、", missing)}");
                missingNotes.Add($"{action}：{string.Join("、", missing)}");
            }
            else if (configured.Count == 0 && SystemActions.IsCore(action))
            {
                PetLog.Warn($"系统动作「{action}」没配帧路径——她这个状态会没动画（面板 → 桌面精灵 → 动作帧）");
                missingNotes.Add($"{action}：（没配帧）");
            }
            else if (configured.Count == 0)
            {
                // startup / error 是"可选"的：没配就只是没有登场/报错动画，别为此弹气泡烦他
                PetLog.Info($"系统动作「{action}」没配帧，跳过（可选动作，不影响她正常待机说话）");
            }

            var frames = new ActionFrames();
            foreach (var path in resolved)
            {
                var got = FrameLoader.LoadFile(path, out var err);
                if (got is null || got.Count == 0)
                {
                    PetLog.Warn($"帧加载失败 {path}：{err ?? "未知原因"}");
                    continue;
                }
                foreach (var (img, delayMs) in got)
                {
                    frames.Images.Add(img);
                    frames.DelaysMs.Add(delayMs);
                }
                // GIF 会被拆成多帧（每帧还带着它自己的停留时长）
                if (got.Count > 1)
                    PetLog.Info($"  {action}：{Path.GetFileName(path)} 拆出 {got.Count} 帧（GIF，用自带逐帧延时）");
            }
            if (frames.Count > 0) loaded[action] = frames;
        }

        if (missingNotes.Count > 0)
        {
            var note = string.Join("\n", missingNotes);
            if (note != _lastSkinWarn)          // 同一份错误只提醒一次，别每次套用配置都弹
            {
                _lastSkinWarn = note;
                ShowBubble("有几个动作的图没找到，我先跳过它们：\n" + note +
                           "\n\n（面板 → 桌面精灵 → 动作帧，路径是相对皮肤目录的）");
            }
        }
        else
        {
            _lastSkinWarn = null;
        }

        if (loaded.Count == 0)
        {
            if (_frameCache.Count > 0)
            {
                PlayError($"新皮肤一张图都没加载到（{skinDir}）");
                ShowBubble("这个皮肤目录里没找到图……我先保持原样。");
                return;
            }
            PlayError("没有任何可用动作帧（素材全加载失败）");
            ShowBubble("找不到素材：请检查 pet.json 的 AssetDir 与动作帧路径");
            return;
        }

        _frameCache.Clear();
        foreach (var kv in loaded) _frameCache[kv.Key] = kv.Value;

        var any = _frameCache.Values.First();
        _naturalWidth = any.Images[0].PixelWidth;
        _naturalHeight = any.Images[0].PixelHeight;
        ApplyScale();
        PetLog.Info($"皮肤就绪：{_frameCache.Count} 个动作，基准尺寸 {_naturalWidth}x{_naturalHeight}");
    }

    /// <summary>按配置缩放设置图片尺寸（窗口 SizeToContent 会跟着变）</summary>
    private void ApplyScale()
    {
        PetImage.Width = Math.Max(16, _naturalWidth * _cfg.Scale);
        PetImage.Height = Math.Max(16, _naturalHeight * _cfg.Scale);
        PetLog.Info($"应用缩放：{_cfg.Scale:0.00} → {PetImage.Width:0}x{PetImage.Height:0}");
    }

    // ---------------- 动作播放 ----------------

    /// <summary>
    /// 切换到某个动作。<paramref name="loop"/> = true 时**一直循环**、不自动回 idle
    /// （说话期间让嘴一直动就靠它，收尾由上层显式换动作）。
    /// <paramref name="returnTo"/>：这次是一次性动作时，**播完回到哪个动作**（默认 idle）
    /// —— thinking 期间插播"工具绑定动作"就是靠它回到 thinking。
    /// </summary>
    public void PlayAction(string action, int times = 1, bool loop = false, string? returnTo = null)
    {
        if (!_frameCache.TryGetValue(action, out var frames) || frames.Count == 0)
        {
            // 目标动作没加载出来（路径写错 / 文件缺失最常见，见 LoadSkin 的警告）→ 退回兜底动作
            var back = _returnAction ?? SystemActions.Idle;
            _returnAction = null;
            if (string.Equals(action, back, StringComparison.OrdinalIgnoreCase))
            {
                PetLog.Warn($"动作「{action}」没有可用帧，保持不变");
                return;                                  // 兜底就是它自己（比如 idle 也缺）→ 别再递归
            }
            PetLog.Warn($"动作「{action}」没有可用帧 → 退回「{back}」");
            PlayAction(back, loop: SystemActions.Is(back));
            return;
        }

        _currentAction = action;
        _errorPlaying = string.Equals(action, SystemActions.Error, StringComparison.OrdinalIgnoreCase);
        _frameIndex = 0;
        _holdAction = loop;
        _returnAction = loop ? null : returnTo;
        _framesLeft = loop ? int.MaxValue : frames.Count * Math.Max(1, times);
        _frameFps = _cfg.Actions.TryGetValue(action, out var a) && a.Fps > 0 ? a.Fps : 4;
        _frameLogLeft = 8;                     // 只打前几帧的实际间隔：校对 GIF 自带节奏用，不刷屏
        _frameLastAt = DateTime.UtcNow;

        _frameTimer ??= new DispatcherTimer();
        _frameTimer.Stop();
        _frameTimer.Tick -= OnFrameTick;
        _frameTimer.Tick += OnFrameTick;
        _frameTimer.Interval = TimeSpan.FromMilliseconds(FrameMs(frames, 0));
        _frameTimer.Start();

        PetImage.Source = frames.Images[0];
        PetLog.Info($"播放动作：{action}（{frames.Count} 帧 @ {_frameFps:0.#}fps"
            + (loop ? "，循环播放" : $" × {times}")
            + (returnTo is not null ? $"，播完回 {returnTo}" : "")
            + (frames.TimedFrames > 0 ? $"，其中 {frames.TimedFrames} 帧用 GIF 自带延时" : "") + "）");
    }

    /// <summary>
    /// 播一个"一次性"动作（表情 / 工具绑定动作）：按它自己的节奏播一遍；
    /// **太短就多播几遍凑够 <see cref="MinActionMs"/>**（单帧 PNG 否则只闪 250ms，等于没播）。
    /// </summary>
    private void PlayOnce(string action, string? returnTo, double minMs = MinActionMs)
    {
        if (!_frameCache.TryGetValue(action, out var frames) || frames.Count == 0)
        {
            PlayAction(action, 1, false, returnTo);   // 交给它走兜底逻辑
            return;
        }
        var fps = _cfg.Actions.TryGetValue(action, out var a) && a.Fps > 0 ? a.Fps : 4;
        var once = frames.TotalMs(fps);
        var times = minMs <= 0 || once >= minMs ? 1 : (int)Math.Ceiling(minMs / Math.Max(1, once));
        PlayAction(action, Math.Max(1, times), false, returnTo);
    }

    /// <summary>一次性动作的最短时长：短于它的（单帧图、眨眼 GIF）会重复播到差不多这么久</summary>
    private const double MinActionMs = 1200;

    /// <summary>上一次播"报错"动作的时刻——断线时收件箱每 3 秒失败一次，不节流会变成永动机</summary>
    private DateTime _lastErrorShownAt = DateTime.MinValue;

    /// <summary>
    /// 「停止工作」动画正在播。这期间**不许别的动作抢镜**：报错往往同时弹气泡说明原因，
    /// 而气泡一逐字就会切成 talk —— 以前报错动画只播了 2 秒就被说话盖掉，等于白播。
    /// </summary>
    private bool _errorPlaying;

    /// <summary>
    /// 出错了 → 播「报错」系统动作提醒主人（她卡住时就是站着不动，光弹气泡容易被忽略）。
    /// 45 秒内不重复播，避免连续报错把动画刷成永动机。
    /// </summary>
    private void PlayError(string reason)
    {
        PetLog.Warn($"报错：{reason}");
        if ((DateTime.UtcNow - _lastErrorShownAt).TotalSeconds < 45) return;   // 断线时每 3 秒失败一次，不节流会变成永动机
        if (!_frameCache.ContainsKey(SystemActions.Error)) return;      // 皮肤里没配就只记日志
        _lastErrorShownAt = DateTime.UtcNow;
        // 只播一遍：「停止工作」那张图本身就有 99 帧（约 3 秒），重复两轮会变成 6 秒的罚站，太吵
        PlayOnce(SystemActions.Error, SystemActions.Idle, minMs: 0);
        _errorPlaying = string.Equals(_currentAction, SystemActions.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>开场动画是否已经播过（本次运行只播一次）</summary>
    private bool _startupPlayed;

    /// <summary>后台那套真皮肤是否已经套用上来（开机"登场"在等它，免得先用占位素材露个脸）</summary>
    private bool _remoteSkinReady;

    /// <summary>
    /// 开机"登场"：播一遍 startup 动作，播完自动进 idle（皮肤里没配就直接 idle）。
    /// 只在本次运行里播一次——面板配置套用后皮肤会重载，不能跟着重播。
    /// </summary>
    private void PlayStartupOnce()
    {
        if (_startupPlayed) return;
        _startupPlayed = true;
        if (_frameCache.ContainsKey(SystemActions.Startup)) PlayOnce(SystemActions.Startup, SystemActions.Idle, minMs: 0);
        else PlayAction(SystemActions.Idle, loop: true);
    }

    /// <summary>
    /// 第 i 帧该停多久：**帧自带延时优先**（GIF 拆出来的帧就属于这种，它自己知道该多快），
    /// 没有自带延时的普通图（PNG 序列）才用动作的 fps 算。
    /// </summary>
    private double FrameMs(ActionFrames frames, int index)
    {
        if (index >= 0 && index < frames.DelaysMs.Count && frames.DelaysMs[index] > 0)
            return frames.DelaysMs[index];
        return 1000.0 / (_frameFps > 0 ? _frameFps : 4);
    }

    private void OnFrameTick(object? sender, EventArgs e)
    {
        if (!_frameCache.TryGetValue(_currentAction, out var frames) || frames.Count == 0) return;
        _frameIndex = (_frameIndex + 1) % frames.Count;
        PetImage.Source = frames.Images[_frameIndex];

        // 开头几帧把"实际间隔"打出来——调素材时能一眼看出 GIF 的节奏有没有被用上
        var now = DateTime.UtcNow;
        if (_frameLogLeft > 0)
        {
            _frameLogLeft--;
            PetLog.Info($"  帧计时 {_currentAction}[{_frameIndex}]：距上一帧 {(now - _frameLastAt).TotalMilliseconds:0}ms"
                + (_frameLogLeft == 0 ? "（核对到此为止）" : ""));
        }
        _frameLastAt = now;

        if (!_holdAction && --_framesLeft <= 0)
        {
            // 一次性动作播完了 → 回"该回的地方"（thinking 期间插播的动作要回到 thinking，其余回 idle）
            var back = _returnAction ?? SystemActions.Idle;
            var finished = _currentAction;
            _returnAction = null;
            if (string.Equals(finished, SystemActions.Error, StringComparison.OrdinalIgnoreCase))
                _errorPlaying = false;               // 报错动画放完了，交还控制权
            if (!string.Equals(_currentAction, back, StringComparison.OrdinalIgnoreCase))
            {
                PlayAction(back, loop: SystemActions.Is(back));   // 内部会重设定时器，别在它之后再改间隔
                return;
            }
        }
        _frameTimer!.Interval = TimeSpan.FromMilliseconds(FrameMs(frames, _frameIndex));
    }

    /// <summary>呼吸浮动：正弦竖直位移，静止时也不像张死图</summary>
    private void StartBreathing()
    {
        if (!_cfg.Breath) return;
        _breathTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _breathTimer.Tick += (_, _) =>
        {
            _breathPhase += 0.06;
            BreathOffset.Y = -Math.Abs(Math.Sin(_breathPhase)) * 5;   // 向上浮 0~5px
        };
        _breathTimer.Start();
    }

    // ---------------- 交互 ----------------

    private void OnPetMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ShowInputBox();
            return;
        }

        if (_cfg.Locked)
        {
            ShowBubble("……锁着呢。想挪动我的话，右键把我解开。");
            return;
        }

        try
        {
            DragMove();     // 按住拖动（阻塞到松开鼠标）
        }
        catch (InvalidOperationException) { /* 鼠标已松开，忽略 */ }
        RememberPosition();
        SnapToEdge();       // 松手：离哪条边近就贴上去
    }

    // ---------------- 缩放手柄（保持比例） ----------------

    private double _resizeStartScale = 1;
    private Point _resizeOrigin;      // 按下那一刻的鼠标屏幕位置（逻辑像素），缩放全程以它为原点

    /// <summary>鼠标移进她身上：把缩放手柄显出来（锁定时不显示）</summary>
    private void OnPetMouseEnter(object sender, MouseEventArgs e)
    {
        _gripTimer?.Stop();          // 刚才只是边缘抖了一下，人又回来了 → 取消待收计时
        SyncGripVisibility();
    }

    /// <summary>
    /// 鼠标离开她：**不立刻收**，等 300ms 再确认一次。
    /// 窗口按像素 alpha 命中，立绘的空隙（角落、发丝之间）都是穿透的，
    /// 鼠标从她身上的实心处挪向手柄时中间很可能穿过这些空隙 → MouseLeave 假信号。
    /// 立刻收就成了"手柄一闪就没了"；延迟确认正好给鼠标走到手柄的时间。
    /// </summary>
    private void OnPetMouseLeave(object sender, MouseEventArgs e)
    {
        if (_resizeDragging) return;   // 正拖着缩放，别收
        EnsureGripTimer();
        _gripTimer!.Stop();
        _gripTimer.Start();
    }

    /// <summary>手柄显隐的唯一出口</summary>
    private void SyncGripVisibility()
    {
        var usable = !_cfg.Locked && !_clickThrough;    // 锁了 / 穿透中 → 手柄整个失效
        ResizeGrip.IsHitTestVisible = usable;           // 失效时别挡住她身上的点击
        var over = IsMouseOver || ResizeGrip.IsMouseOver;
        SetGripArt(usable && over);
    }

    /// <summary>
    /// 只切"箭头图样"的显隐——Thumb 自己那层 alpha=1 的底始终留着。
    /// 不能改用 ResizeGrip.Opacity 来隐藏：0 = 整块像素变全透明 = 又被鼠标穿透了，
    /// 手柄会陷入"藏起来就再也摸不到、也就再也不会显形"的死循环（见 XAML 里的说明）。
    /// </summary>
    private void SetGripArt(bool on)
    {
        var art = _gripArt ??= TryGetGripArt();
        if (art is null) ResizeGrip.Opacity = on ? 0.9 : 0;   // 兜底：取不到模板元素就退回旧行为
        else art.Opacity = on ? 0.9 : 0;
    }

    private FrameworkElement? TryGetGripArt()
    {
        try
        {
            ResizeGrip.ApplyTemplate();
            return ResizeGrip.Template.FindName("GripArt", ResizeGrip) as FrameworkElement;
        }
        catch { return null; }
    }

    private void EnsureGripTimer()
    {
        if (_gripTimer != null) return;
        _gripTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _gripTimer.Tick += (_, _) => { _gripTimer!.Stop(); SyncGripVisibility(); };
    }

    private bool _resizeDragging;
    private DispatcherTimer? _gripTimer;      // 手柄"再确认"计时器（延迟收，见 OnPetMouseLeave）
    private FrameworkElement? _gripArt;       // 模板里那层箭头图样（显隐切它，不切 Thumb 自己）

    private void OnResizeStarted(object sender, DragStartedEventArgs e)
    {
        if (_cfg.Locked) return;
        _resizeDragging = true;
        _resizeStartScale = _cfg.Scale;
        _resizeOrigin = CursorScreenPoint();     // 记下按下那一刻的鼠标屏幕位置，全程以它为原点
        PetLog.Info($"手柄按下：起始缩放 {_cfg.Scale:0.00}（{PetImage.Width:0}x{PetImage.Height:0}）");
    }

    /// <summary>
    /// 鼠标的屏幕坐标（换算成 WPF 逻辑像素）——**与窗口自己的位置和大小无关**。
    /// 拖动缩放期间窗口会长大/缩小，任何"相对窗口或相对手柄"的坐标都会被这件事污染。
    /// </summary>
    private Point CursorScreenPoint()
    {
        var p = PointToScreen(Mouse.GetPosition(this));      // → 设备像素
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget != null) p = src.CompositionTarget.TransformFromDevice.Transform(p);
        return p;                                            // → 逻辑像素
    }

    /// <summary>
    /// 拖动缩放手柄：**按比例**缩放——只改 _cfg.Scale，宽高由同一份素材尺寸同乘一个系数得到，
    /// 所以比例天然不会被拖歪。取"相对变化更大的那个轴"作为基准，斜着拖也跟手。
    ///
    /// ⚠ 别用 e.HorizontalChange/VerticalChange 累加：那是**相对 Thumb 当前位置**算的增量，
    /// 而 Thumb 钉在窗口右下角——窗口一变大它自己就往右下跑，鼠标一动不动也会产生
    /// （而且很大、方向相反的）增量。结果就是"缩放 → 窗口变大 → 手柄位移 → 增量反转 → 跳回最小",
    /// 自己喂自己：表现即大小来回闪烁、最后掉到最小值（0.2 钳位）。
    /// </summary>
    private void OnResizeDrag(object sender, DragDeltaEventArgs e)
    {
        if (_cfg.Locked || !_resizeDragging) return;

        var now = CursorScreenPoint();
        var dx = now.X - _resizeOrigin.X;
        var dy = now.Y - _resizeOrigin.Y;

        var startW = Math.Max(1, _naturalWidth * _resizeStartScale);
        var startH = Math.Max(1, _naturalHeight * _resizeStartScale);
        var factorX = (startW + dx) / startW;
        var factorY = (startH + dy) / startH;
        var factor = Math.Abs(factorX - 1) >= Math.Abs(factorY - 1) ? factorX : factorY;

        var next = Math.Clamp(_resizeStartScale * factor, 0.2, 4.0);
        if (Math.Abs(next - _cfg.Scale) < 0.005) return;

        _cfg.Scale = next;
        ApplyScale();
        _bubble?.Hide();     // 尺寸变了气泡位置会错位，先收掉（下次显示会自动重新定位）
    }

    private void OnResizeCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_resizeDragging) return;
        _resizeDragging = false;
        SyncGripVisibility();
        SnapToEdge(animate: false);   // 尺寸变了，原本贴着的边要重新对齐
        RememberPosition();
        _cfg.SaveUserConfig();
        PetLog.Info($"拖动缩放完成：{_cfg.Scale:0.00} → {PetImage.Width:0}x{PetImage.Height:0}");
    }

    private void OnPetMouseRightUp(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("说话…", ShowInputBox));
        menu.Items.Add(MenuItem("贴到最近的边", () => SnapToEdge()));
        menu.Items.Add(MenuItem(_cfg.Snap.Enabled ? "✓ 边缘吸附" : "　边缘吸附", ToggleSnap));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(_cfg.AlwaysOnTop ? "取消置顶" : "保持置顶", ToggleTopmost));
        menu.Items.Add(MenuItem("放大", () => ChangeScale(1.15)));
        menu.Items.Add(MenuItem("缩小", () => ChangeScale(1 / 1.15)));
        menu.Items.Add(MenuItem("重置大小", () => { _cfg.Scale = 1; ApplyScale(); SnapToEdge(animate: false); }));
        menu.Items.Add(MenuItem(_cfg.Locked ? "✓ 锁定位置和大小" : "　锁定位置和大小", ToggleLock));
        menu.Items.Add(MenuItem(_cfg.ClickThroughInGame ? "✓ 游戏模式防误触" : "　游戏模式防误触", ToggleClickThroughInGame));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("挥挥手（talk）", () => PlayAction("talk", 2)));
        menu.Items.Add(MenuItem("打开日志", OpenLog));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("退出", () => Application.Current.Shutdown()));
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static MenuItem MenuItem(string header, Action onClick)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    public void ToggleTopmost()
    {
        _cfg.AlwaysOnTop = !_cfg.AlwaysOnTop;
        Topmost = _cfg.AlwaysOnTop;
        PetLog.Info($"置顶 = {_cfg.AlwaysOnTop}");
    }

    public void ToggleSnap()
    {
        _cfg.Snap.Enabled = !_cfg.Snap.Enabled;
        _cfg.SaveUserConfig();
        PetLog.Info($"边缘吸附 = {_cfg.Snap.Enabled}");
        if (_cfg.Snap.Enabled) SnapToEdge();
        else ShowBubble(_cfg.Snap.Enabled ? "好～那我贴着边站。" : "……行，那我随便站了。");
    }

    /// <summary>锁定/解锁位置和大小（锁定后拖不动、缩放手柄也藏起来）</summary>
    public void ToggleLock()
    {
        _cfg.Locked = !_cfg.Locked;
        _cfg.SaveUserConfig();
        SyncGripVisibility();
        PetLog.Info($"锁定位置和大小 = {_cfg.Locked}");
        ShowBubble(_cfg.Locked ? "好，那我就在这儿不动了。" : "……行吧，你可以随便挪我了。");
    }

    /// <summary>游戏模式防误触（鼠标穿透）开关</summary>
    public void ToggleClickThroughInGame()
    {
        _cfg.ClickThroughInGame = !_cfg.ClickThroughInGame;
        _cfg.SaveUserConfig();
        PetLog.Info($"游戏模式防误触 = {_cfg.ClickThroughInGame}");
        if (!_cfg.ClickThroughInGame) SetClickThrough(false);
        ShowBubble(_cfg.ClickThroughInGame
            ? "嗯，你打游戏的时候我就不挡着你了。"
            : "……好吧，那我继续待在桌面上。");
    }

    /// <summary>当前要不要穿透（游戏模式 + 开了防误触）</summary>
    private bool _clickThrough;

    /// <summary>
    /// 应用/取消鼠标穿透。游戏模式下把宠物和气泡都设成穿透，点击直接落到游戏里，不会误触。
    /// 穿透期间她点不动也拖不动——要恢复就点托盘菜单（托盘的"说话…"仍然可用）。
    /// </summary>
    public void SetClickThrough(bool on)
    {
        if (_clickThrough == on) return;
        if (!ClickThrough.Set(this, on))
        {
            PetLog.Warn("设置鼠标穿透失败（窗口句柄可能还没就绪）");
            return;
        }
        _clickThrough = on;
        if (on)
        {
            _gripTimer?.Stop();
            SyncGripVisibility();      // 穿透中 → 手柄连带失效（见 SyncGripVisibility）
            _bubble?.SetClickThrough(true);
        }
        else
        {
            _bubble?.SetClickThrough(false);
            // 刚从穿透恢复：此刻 IsMouseOver 还是穿透期的旧值，等一拍再校准手柄显隐
            EnsureGripTimer();
            _gripTimer!.Stop();
            _gripTimer.Start();
        }
        PetLog.Info(on ? "游戏模式：宠物已鼠标穿透（防误触，点不到她）" : "已恢复可点击");
    }

    /// <summary>后端报来的活动模式（game/normal/away/""）</summary>
    private string _activityMode = "";

    /// <summary>按后端报来的活动模式决定要不要穿透</summary>
    private void ApplyActivityMode(string mode)
    {
        if (mode == _activityMode) return;
        _activityMode = mode;
        var want = _cfg.ClickThroughInGame && mode.Equals("game", StringComparison.OrdinalIgnoreCase);
        SetClickThrough(want);
        if (want) ShowBubble("（你打游戏呢，我先安静待着——点不到我是正常的）");
    }

    public void ChangeScale(double factor)
    {
        _cfg.Scale = Math.Clamp(_cfg.Scale * factor, 0.2, 4.0);
        ApplyScale();
        _bubble?.Hide();
        // 尺寸变了 → 原本贴着的边就不齐了，重新贴一次（不然会留下一条缝）
        SnapToEdge(animate: false);
        RememberPosition();
    }

    public void Say(string text) => ShowBubble(text);

    private static void OpenLog()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".jingjing-pet", "pet.log");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { PetLog.Warn($"打开日志失败：{ex.Message}"); }
    }

    /// <summary>双击/菜单唤出输入框（位置在宠物头顶）</summary>
    public void ShowInputBox()
    {
        _input ??= new InputWindow();
        _input.ShowFor(this, text =>
        {
            if (!string.IsNullOrWhiteSpace(text)) OnUserSaid(text.Trim());
        });
    }

    // ---------------- 说话（接后端） ----------------

    /// <summary>
    /// 主人对宠物说了一句话：先冒个"……"表示在想，然后把话发给 QQBot，
    /// 她回几条就逐条冒几个气泡（图片直接显示在气泡里）。
    /// </summary>
    private async void OnUserSaid(string text)
    {
        PetLog.Info($"主人说：{text}");

        if (_chatting)
        {
            // 连播多段时每段都要走完自己的停留时长，等待可能有点久——告诉他能点气泡跳过
            ShowBubble(_broadcasting ? "……等我把这几句说完。\n（点一下气泡就能跳到下一句）"
                                    : "……等我把上一句说完。");
            return;
        }

        if (!_cfg.Backend.Enabled)
        {
            ShowBubble($"（回显模式）你说：{text}\n\n后端没开：pet.json 的 backend.enabled 打开就能真聊了。");
            return;
        }

        _chatting = true;
        try
        {
            EnterThinking();                       // 等回复期间循环播 thinking（她要查记忆/调工具，可能要十几秒）
            StartToolPolling();                    // 她中途调了工具 → 插播绑定动作 → 播完回 thinking
            ShowBubble("……");                     // 提一句"我在想"
            ChatOutcome outcome;
            _awaitingReply = true;
            try { outcome = await _backend.ChatAsync(text); }
            finally { _awaitingReply = false; }    // 拿到回复（或出错）之后，thinking 才允许被换掉
            StopToolPolling();                     // 已经拿到回复，不用再盯工具了
            ExitThinking();                        // 接下来交给 talk（由 SayCore 点名）

            if (!outcome.Ok)
            {
                PlayError($"对话失败：{outcome.Error}");
                ShowBubble($"呜……{outcome.Error}\n\n（QQBot 那边开着吗？{_backend.BaseUrl}）");
                return;
            }
            if (outcome.Replies.Count == 0)
            {
                ShowBubble("……（她想了想，什么也没说。）");
                return;
            }

            _broadcasting = outcome.Replies.Count > 1;
            for (var i = 0; i < outcome.Replies.Count; i++)
            {
                var r = outcome.Replies[i];
                var seg = r.IsImage ? (r.Caption ?? "") : (r.DisplayText ?? "");
                if (r.IsImage && !string.IsNullOrEmpty(r.DataUrl))
                    ShowPicture(r.DataUrl, r.Caption);
                else
                    ShowBubble(r.DisplayText);

                if (i < outcome.Replies.Count - 1)
                {
                    await WaitTypingDoneAsync();                          // 先把字出完
                    await WaitBubbleStayedAsync(seg.Length, r.IsImage);   // 再让它把自己那一份停留时长走完
                    await WaitBubbleGapAsync(seg.Length);                 // 最后才喘口气（每段单独算）
                }
            }
        }
        finally
        {
            StopToolPolling();
            _thinking = false;
            _chatting = false;
            _broadcasting = false;
            // ⚠️ 别在"逐字说话"时把 talk 打成 idle：最后一条回复的气泡这会儿还在滚动，
            // 一覆盖就是"talk 只闪了一下"。收尾由 OnBubbleTypingFinished 负责。
            if (!_talkingLoop) PlayAction("idle", loop: true);
            _ = TryFlushHeldAsync();   // 说完了：把刚才攒下的主动消息补播
        }
    }

    // ---------------- thinking（等她回话）与"工具绑定动作" ----------------

    /// <summary>开始等她回话：循环播 thinking（皮肤里没这个动作就保持现状，不报错）</summary>
    private void EnterThinking()
    {
        _thinking = true;
        if (_frameCache.ContainsKey(SystemActions.Thinking))
            PlayAction(SystemActions.Thinking, loop: true);
    }

    /// <summary>
    /// 等待结束：松掉 thinking 标记，**但不动画面**——接下来交给真正要播的动作
    /// （长句马上会被 SayCore 的 talk 接管；短句走 <see cref="OnBubbleTypingFinished"/> 那一步回 idle）。
    /// 这样就不会出现 thinking→idle→talk 的瞬时跳变。
    /// </summary>
    private void ExitThinking() => _thinking = false;

    /// <summary>开始盯"她调了什么工具"（只在等回复期间；700ms 一次，纯读、不打扰后端）</summary>
    private void StartToolPolling()
    {
        StopToolPolling();
        _toolPollCts = new CancellationTokenSource();
        _ = PollToolsAsync(_toolPollCts.Token);
    }

    private void StopToolPolling()
    {
        var cts = _toolPollCts;
        _toolPollCts = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch { /* 已释放 */ }
        cts.Dispose();
    }

    private async Task PollToolsAsync(CancellationToken ct)
    {
        // 先对齐基线：别把上一轮对话的工具调用当成这一轮的
        try
        {
            var baseline = await _backend.PetStatusAsync(ct);
            if (baseline is not null) _toolHintSeq = baseline.Seq;
        }
        catch { /* 对齐失败也无所谓，大不了这次不播 */ }

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(700, ct); } catch { return; }
            if (ct.IsCancellationRequested) return;

            var st = await _backend.PetStatusAsync(ct);
            if (st is null || st.Seq == _toolHintSeq) continue;
            _toolHintSeq = st.Seq;
            PlayToolAction(st.Tool);
        }
    }

    /// <summary>她调了某个工具 → 播绑定到它的动作（播完回到 thinking，因为这会儿还在等她说话）</summary>
    private void PlayToolAction(string? toolName)
    {
        var action = EmotionRules.ActionForTool(toolName, _frameCache.Keys, _cfg);
        if (action is null) return;
        PetLog.Info($"她调用了工具 {toolName} → 播放绑定动作 {action}");
        PlayOnce(action, _thinking ? SystemActions.Thinking : null);
    }

    // ---------------- 气泡 ----------------

    /// <summary>在宠物头顶显示气泡（逐字出字，出完才开始算停留时长；详见 <see cref="SayCore"/>）</summary>
    public void ShowBubble(string text) => SayCore(text, null);

    /// <summary>在气泡里显示一张图（她当场画给你的画）；图片不逐字，图注里的情绪照样认</summary>
    public void ShowPicture(string dataUrl, string? caption) => SayCore(caption ?? "", dataUrl);

    /// <summary>
    /// 她说一句话（或甩一张图）时的**动作编排**：
    ///   ① 逐字说话期间 → 把 talk 设成循环播放（嘴一直在动，比闪一下强多了）；
    ///   ② 字出完了 → 播这句话的**情绪动作**（见 <see cref="EmotionRules"/>）→ 之后它自己回 idle；
    ///   ③ 气泡被点掉而字还没出完 → 也得把循环收回来，不然她会一直"说话"。
    /// talk 只在这里被驱动（别在别处再 PlayAction("talk")，会跟这套编排打架）。
    /// </summary>
    private void SayCore(string text, string? imageSource)
    {
        _bubble ??= new BubbleWindow();
        _bubble.TypingFinished -= OnBubbleTypingFinished;   // 同一个气泡窗口复用 → 先退订再订，免得重复触发
        _bubble.Hidden -= OnBubbleHidden;
        _bubble.TypingFinished += OnBubbleTypingFinished;
        _bubble.Hidden += OnBubbleHidden;

        var known = _frameCache.Keys;
        _pendingEmotion = EmotionRules.Detect(text, known, _cfg);
        var shown = EmotionRules.StripMarkers(text, known);
        if (_pendingEmotion is not null)
            PetLog.Info($"这句话的情绪：{_pendingEmotion}");

        // 连播多条回复时，下一句要等上一句"出完字"——不然下一句上来就把上一句的逐字掐断，
        // 上一句永远走不到"出完 → 停留计时 → 表情"（真机上就是这么被截断的）。
        _typingDone = new TaskCompletionSource();

        if (imageSource is null) _bubble.ShowMessage(this, shown);
        else _bubble.ShowPicture(this, imageSource, shown);

        // 太短的话（"……"这种）就别循环了，否则只是抽一下嘴角，不如安静站着
        _talkingLoop = imageSource is null && _cfg.Bubble.TypeMs(shown.Length) >= 300;
        // 报错动画正在播 → 让「停止工作」播完再说（气泡照常出字，只是不抢她的动作）
        if (_talkingLoop && !_errorPlaying) PlayAction("talk", loop: true);
    }

    /// <summary>等当前这句话"字出完"（被点掉也算说完，免得连播卡死）</summary>
    private Task WaitTypingDoneAsync() => _typingDone?.Task ?? Task.CompletedTask;

    /// <summary>
    /// 等这个气泡**把自己的停留时长走完**（每个气泡按自己的字数单独算：m + 字数 × n）。
    /// 以前连播只等"出完字"就切下一句 → 上一句刚显示就被覆盖，等于停留公式在多段时形同虚设。
    /// · 停留时长算出来是 0（m ≤ 0 = 不自动消失）→ **不等**，否则要等他点一下才继续，会把后续段全卡住。
    /// · 他中途点掉气泡 → 算"看完了"，立刻往下走。
    /// </summary>
    private async Task WaitBubbleStayedAsync(int charCount, bool isImage)
    {
        var ms = _cfg.Bubble.DurationMs(charCount, isImage);
        if (ms <= 0) return;
        PetLog.Info($"连播：等这一句走完它自己的停留 {ms / 1000.0:0.#} 秒（{charCount} 字）"
                    + (isImage ? "，图片按 12 秒保底" : ""));
        _bubbleGone = new TaskCompletionSource();
        await Task.WhenAny(_bubbleGone!.Task, Task.Delay(ms + 300));   // +300ms 兜底：计时器误差/隐藏动画
    }

    /// <summary>这个气泡说完之后的"喘气时间"：按间隔公式 g + 字数 × h 单独算</summary>
    private async Task WaitBubbleGapAsync(int charCount)
    {
        var gap = _cfg.Bubble.GapMs(charCount);
        if (gap <= 0) gap = _cfg.Backend.ReplyGapMs;      // 间隔公式被他设成 0 时，退回老的那个固定值
        if (gap > 0)
        {
            PetLog.Info($"连播：留 {gap}ms 间隔再出下一句（g={_cfg.Bubble.GapSeconds:0.##}s + {charCount} 字 × h={_cfg.Bubble.GapSecondsPerChar:0.###}s）");
            await Task.Delay(gap);
        }
    }

    private void OnBubbleTypingFinished()
    {
        var wasTalking = _talkingLoop;
        _talkingLoop = false;
        // 还在等她回话：这句只是"……"占位气泡，出完字也不能打断 thinking
        if (_awaitingReply)
        {
            _typingDone?.TrySetResult();
            return;
        }
        // 「停止工作」还没播完 → 别把它换成 idle/表情动作，等它自己播完回 idle
        if (_errorPlaying)
        {
            _typingDone?.TrySetResult();
            return;
        }
        // 话说完了 → 播这句话的"触发词动作"（没有就回 idle）
        if (_pendingEmotion is not null) PlayOnce(_pendingEmotion, SystemActions.Idle);
        else if (wasTalking) PlayAction(SystemActions.Idle, loop: true);
        else if (string.Equals(_currentAction, SystemActions.Thinking, StringComparison.OrdinalIgnoreCase))
            PlayAction(SystemActions.Idle, loop: true);      // 短句、又没命中动作：把 thinking 收掉
        _pendingEmotion = null;
        _typingDone?.TrySetResult();
    }

    private void OnBubbleHidden()
    {
        if (_talkingLoop)
        {
            _talkingLoop = false;
            PlayAction(_thinking ? SystemActions.Thinking : SystemActions.Idle, loop: true);
        }
        _typingDone?.TrySetResult();               // 字还没出完就被点掉了：也算"说完"，别让连播一直等
        _bubbleGone?.TrySetResult();               // 被点掉也算"看完了"，连播继续走下一段
    }

    // ---------------- 边缘吸附 ----------------

    /// <summary>
    /// 松手后贴到最近的屏幕边缘（阈值见 pet.json 的 snap.threshold）。
    /// 四条边都算：左边/右边/工作区顶部/工作区底部（底部是任务栏上沿，站上去最自然）。
    /// </summary>
    public void SnapToEdge(bool animate = true)
    {
        if (!_cfg.Snap.Enabled) return;
        try
        {
            var target = WindowSnap.Target(this, _cfg.Snap.Threshold);
            if (target is null) return;   // 不在任何边缘附近：原地不动，别硬拽

            var (x, y, edge) = target.Value;
            if (Math.Abs(x - Left) < 0.5 && Math.Abs(y - Top) < 0.5) return;   // 已经贴住了

            PetLog.Info($"边缘吸附：贴到 {edge}（{x:0},{y:0}）");
            _cfg.LastX = x;               // 记得是"最终落点"，不是动画中途的位置
            _cfg.LastY = y;
            _cfg.SaveUserConfig();

            if (animate && _cfg.Snap.Animate) AnimateTo(x, y);
            else { _snapTimer?.Stop(); Left = x; Top = y; }
        }
        catch (Exception ex)
        {
            PetLog.Warn($"边缘吸附失败：{ex.Message}");
        }
    }

    /// <summary>贴边滑动（Left/Top 不是依赖属性，动画得自己走）</summary>
    private void AnimateTo(double x, double y)
    {
        _snapTimer?.Stop();
        var fromX = Left;
        var fromY = Top;
        const int steps = 9;
        var i = 0;

        _snapTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(14) };
        _snapTimer.Tick += (_, _) =>
        {
            i++;
            var t = Math.Min(1.0, (double)i / steps);
            var eased = 1 - Math.Pow(1 - t, 3);          // easeOutCubic：先快后慢，贴上去有"啪"的一下的感觉
            Left = fromX + (x - fromX) * eased;
            Top = fromY + (y - fromY) * eased;
            if (i >= steps)
            {
                Left = x;
                Top = y;
                _snapTimer!.Stop();
            }
        };
        _snapTimer.Start();
    }

    // ---------------- 位置记忆 ----------------

    private void RestorePosition()
    {
        if (_cfg.LastX is null || _cfg.LastY is null)
        {
            // 首次启动：默认蹲在右下角（工作区内，不被任务栏压住）——不然窗口可能开在某个莫名其妙的地方
            var wa = SystemParameters.WorkArea;
            var w = ActualWidth > 0 ? ActualWidth : 260;
            var h = ActualHeight > 0 ? ActualHeight : 360;
            Left = wa.Right - w - 24;
            Top = wa.Bottom - h - 8;
            PetLog.Info($"首次启动，默认位置：右下角 ({Left:0},{Top:0})");
            return;
        }
        var (x, y) = ClampToScreen(_cfg.LastX.Value, _cfg.LastY.Value, out var clamped);
        Left = x;
        Top = y;
        PetLog.Info($"恢复位置：({x:0},{y:0}){(clamped ? "（原位置不在屏幕内，已夹紧）" : "")}");
    }

    private void RememberPosition()
    {
        _cfg.LastX = Left;
        _cfg.LastY = Top;
        _cfg.SaveUserConfig();
    }

    /// <summary>把坐标夹进可视桌面范围（换屏/拔显示器后不至于飘到看不见的地方）</summary>
    private (double X, double Y) ClampToScreen(double x, double y, out bool clamped)
    {
        clamped = false;
        var vl = SystemParameters.VirtualScreenLeft;
        var vt = SystemParameters.VirtualScreenTop;
        var vw = SystemParameters.VirtualScreenWidth;
        var vh = SystemParameters.VirtualScreenHeight;
        var w = ActualWidth > 0 ? ActualWidth : 240;
        var h = ActualHeight > 0 ? ActualHeight : 320;

        var nx = Math.Clamp(x, vl - w * 0.5, vl + vw - w * 0.5);
        var ny = Math.Clamp(y, vt, vt + vh - h * 0.3);
        clamped = Math.Abs(nx - x) > 0.5 || Math.Abs(ny - y) > 0.5;
        return (nx, ny);
    }

    protected override void OnClosed(EventArgs e)
    {
        RememberPosition();
        _frameTimer?.Stop();
        _breathTimer?.Stop();
        _snapTimer?.Stop();
        _inboxTimer?.Stop();
        // 退出前最后报一次"我不在了"：丢给线程池等一小会儿，避免在 UI 线程上等异步造成死锁；
        // 就算没报成功也没关系，后端 15 秒心跳超时会自动判离线
        try { Task.Run(() => _backend.MarkOfflineAsync()).Wait(1500); }
        catch { /* 退出路径，失败无所谓 */ }
        _bubble?.Close();
        _input?.Close();
        base.OnClosed(e);
    }
}
