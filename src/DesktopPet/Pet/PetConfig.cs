using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopPet.Pet;

/// <summary>
/// 宠物外观/行为配置（assets/&lt;皮肤&gt;/pet.json）——换皮不改代码：
/// 换一个目录 + 改 pet.json 就能变成另一只宠物。
/// </summary>
public sealed class PetConfig
{
    /// <summary>宠物名字（托盘菜单/气泡标题用）</summary>
    public string Name { get; set; } = "静静";

    /// <summary>素材目录（相对程序目录，或绝对路径）</summary>
    public string AssetDir { get; set; } = "assets/placeholder";

    /// <summary>整体缩放（1.0 = 原图大小）</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>是否置顶浮在桌面上</summary>
    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>呼吸动画：静止时轻微上下浮动，看起来"活着"</summary>
    public bool Breath { get; set; } = true;

    /// <summary>动作定义：动作名 → 帧序列（帧路径相对皮肤目录）</summary>
    public Dictionary<string, PetAction> Actions { get; set; } = new();

    /// <summary>
    /// 表情关键词规则（动作名 → 关键词数组）：从她说的句子里猜该播哪个动作。
    /// 留空就用内置的一套保守默认值（见 <see cref="EmotionRules.Defaults"/>）。
    /// 皮肤里没有那个动作时，规则命中也不会播。
    /// </summary>
    public Dictionary<string, string[]> Emotions { get; set; } = new();

    /// <summary>气泡外观</summary>
    public BubbleConfig Bubble { get; set; } = new();

    /// <summary>后端（QQBot 的 /api/pet/*）——她的"大脑"在这儿，桌面端只是个壳</summary>
    public BackendConfig Backend { get; set; } = new();

    /// <summary>屏幕边缘吸附</summary>
    public SnapConfig Snap { get; set; } = new();

    /// <summary>上次窗口位置（运行时回写，换屏/重启不飘）</summary>
    public double? LastX { get; set; }
    public double? LastY { get; set; }

    /// <summary>锁定位置和大小：拖动不了、右下角的缩放手柄也藏起来（防手滑把她挪走或改大小）</summary>
    public bool Locked { get; set; }

    /// <summary>
    /// 游戏模式鼠标穿透：主人切到游戏模式（主机活动监测判定）时，整只宠物不接收任何鼠标点击——
    /// 防误触挡了游戏操作。要说话走托盘菜单，退出穿透也走托盘。
    /// </summary>
    public bool ClickThroughInGame { get; set; } = true;

    // ---------------- "这个值本机自己定过" 的标记（不写进 JSON） ----------------
    // 用途：面板（后台配置）每次开机都会被套用一遍，若它把下面这些字段一起覆盖，
    // 主人在本机拖出来的尺寸、按过的托盘开关就会"每次打开都变回默认"。
    // 所以只有"用户配置文件里明确写过"的字段才允许拒绝面板的旧值（面板真的改了才让路，见 ApplyRemote）。

    /// <summary>用户配置里明确写过缩放（拖过手柄，或已经被面板的下发套用过）</summary>
    [JsonIgnore] public bool UserSetScale { get; set; }

    /// <summary>这次套用面板配置时，动作部分被"纠正"过的地方（系统动作被删、工具重复绑定）——供上层打日志</summary>
    [JsonIgnore] public List<string> LastActionWarnings { get; set; } = new();

    /// <summary>用户配置里明确写过"锁定位置和大小"</summary>
    [JsonIgnore] public bool UserSetLocked { get; set; }

    /// <summary>用户配置里明确写过"游戏模式防误触"</summary>
    [JsonIgnore] public bool UserSetClickThrough { get; set; }

    /// <summary>用户配置里明确写过"边缘吸附"</summary>
    [JsonIgnore] public bool UserSetSnap { get; set; }

    // ---------------- 读写 ----------------

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,   // pet.json 用 camelCase 写，属性是 PascalCase，必须忽略大小写
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>用户目录下的配置覆盖（位置记忆写这里，不动素材目录里的原始 pet.json）</summary>
    public static string UserConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jingjing-pet", "pet.json");

    /// <summary>加载：优先内置素材目录的 pet.json，再套用用户目录里的覆盖</summary>
    public static PetConfig Load(string appDir)
    {
        var basePath = Path.Combine(appDir, "pet.json");
        PetConfig cfg;
        if (File.Exists(basePath))
        {
            cfg = JsonSerializer.Deserialize<PetConfig>(File.ReadAllText(basePath), JsonOpts) ?? new PetConfig();
        }
        else
        {
            cfg = new PetConfig();
        }

        // 用户覆盖：只合并"用户级"字段（位置/缩放/皮肤/几个本机开关），保证换素材包时不被旧配置污染。
        // ⚠ 必须用"全可空"的 DTO 读：直接反序列化成 PetConfig 的话，文件里**没写**的键会拿到 C# 默认值
        // （bool 一律 false、double 一律 0），于是"没配过"被误当成"用户设成了 false"，连内置 pet.json 的默认值也一起踩掉。
        if (File.Exists(UserConfigPath))
        {
            try
            {
                var user = JsonSerializer.Deserialize<UserPrefs>(File.ReadAllText(UserConfigPath), JsonOpts);
                if (user is not null)
                {
                    if (!string.IsNullOrWhiteSpace(user.AssetDir)) cfg.AssetDir = user.AssetDir!;
                    if (user.Scale is > 0) { cfg.Scale = user.Scale.Value; cfg.UserSetScale = true; }
                    if (user.AlwaysOnTop is { } aot) cfg.AlwaysOnTop = aot;
                    if (user.Locked is { } lk) { cfg.Locked = lk; cfg.UserSetLocked = true; }
                    if (user.ClickThroughInGame is { } ct) { cfg.ClickThroughInGame = ct; cfg.UserSetClickThrough = true; }
                    if (user.Snap?.Enabled is { } se) { cfg.Snap.Enabled = se; cfg.UserSetSnap = true; }
                    if (user.LastX is { } lx) cfg.LastX = lx;
                    if (user.LastY is { } ly) cfg.LastY = ly;
                }
            }
            catch { /* 用户配置坏了就用默认，不影响启动 */ }
        }
        return cfg;
    }

    /// <summary>从 JSON 文本解析一份配置（后台面板下发的配置用；解析失败返回 null）</summary>
    public static PetConfig? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<PetConfig>(json, JsonOpts); }
        catch { return null; }
    }

    /// <summary>面板这次改了哪些"本机也管得着"的字段——只有**真的改了**才允许覆盖本机值</summary>
    public readonly record struct RemoteOverrides(bool Scale, bool Locked, bool ClickThrough, bool Snap)
    {
        public bool Any => Scale || Locked || ClickThrough || Snap;
    }

    /// <summary>
    /// 面板这个值是不是主人这次主动改的（和上次看到的不一样）；顺带把新值记下来当下次基准。
    /// </summary>
    public static bool PanelChanged<T>(ref T? last, T now) where T : struct, IEquatable<T>
    {
        var changed = last.HasValue && !last.Value.Equals(now);
        last = now;
        return changed;
    }

    /// <summary>
    /// 用后台下发的配置覆盖本地配置（外观/行为部分）——面板里改了精灵配置，这里负责套用。
    ///  - 位置（LastX/LastY）是纯本机状态，不在这份配置里，不会被动到
    ///  - **backend.baseUrl / token / enabled 不覆盖**：那是桌面端连后端的参数，
    ///    被远端改错就会连不上、连不上就再也拿不到正确的配置——本机自己管
    ///  - `ov` 里为 false 的字段**保留本机值**：这几个字段本机也管得着（拖手柄改尺寸、托盘按开关，
    ///    都存进了用户配置），而面板里存的是"上次保存时的旧值"——无条件套用就会把主人的本机调整冲掉，
    ///    表现就是"我调过的大小，每次打开又变回默认"。
    /// </summary>
    public void ApplyRemote(PetConfig remote, RemoteOverrides ov)
    {
        if (!string.IsNullOrWhiteSpace(remote.Name)) Name = remote.Name;
        if (!string.IsNullOrWhiteSpace(remote.AssetDir)) AssetDir = remote.AssetDir;
        AlwaysOnTop = remote.AlwaysOnTop;
        Breath = remote.Breath;

        if (ov.Scale && remote.Scale > 0) { Scale = remote.Scale; UserSetScale = true; }
        if (ov.Locked) { Locked = remote.Locked; UserSetLocked = true; }
        if (ov.ClickThrough) { ClickThroughInGame = remote.ClickThroughInGame; UserSetClickThrough = true; }
        if (ov.Snap) { Snap.Enabled = remote.Snap.Enabled; UserSetSnap = true; }

        if (remote.Snap.Threshold > 0) Snap.Threshold = remote.Snap.Threshold;
        Snap.Animate = remote.Snap.Animate;

        if (remote.Actions.Count > 0)
        {
            // 面板是动作的编辑入口，但它**不允许删掉系统动作**：缺了就从本机当前这份补回来
            // （万一面板存的是旧配置、或者手改 JSON 删了 idle/talk/thinking）
            var local = Actions;
            Actions = remote.Actions;
            foreach (var sys in SystemActions.All)
            {
                if (Actions.Keys.Any(k => k.Equals(sys, StringComparison.OrdinalIgnoreCase))) continue;
                var fallback = local.FirstOrDefault(kv => kv.Key.Equals(sys, StringComparison.OrdinalIgnoreCase));
                if (fallback.Key is not null)
                {
                    Actions[sys] = fallback.Value;
                    LastActionWarnings.Add($"面板配置里没有系统动作「{sys}」，已保留本机的（系统动作不可删除）");
                }
            }
        }
        Bubble = remote.Bubble;

        // 一个工具只能绑一个动作：面板和后端都去过重，这里再兜一层（内置 pet.json 手写也不怕）
        foreach (var kv in Actions.ToList())
        {
            if (kv.Value?.Tools is not { Count: > 0 }) continue;
            foreach (var t in kv.Value.Tools)
            {
                var owner = Actions.FirstOrDefault(o =>
                    !ReferenceEquals(o.Value, kv.Value) &&
                    o.Value?.Tools is { Count: > 0 } ts &&
                    ts.Any(x => x.Equals(t, StringComparison.OrdinalIgnoreCase)));
                if (owner.Key is not null)
                {
                    kv.Value.Tools.RemoveAll(x => x.Equals(t, StringComparison.OrdinalIgnoreCase));
                    LastActionWarnings.Add($"工具 {t} 同时绑在了「{owner.Key}」和「{kv.Key}」上——已从「{kv.Key}」移除（每个工具只能绑一个动作）");
                }
            }
        }

        // 只套用"安全"的连接参数（超时/节奏），地址令牌不动
        if (remote.Backend.TimeoutSeconds > 0) Backend.TimeoutSeconds = remote.Backend.TimeoutSeconds;
        if (remote.Backend.ReplyGapMs > 0) Backend.ReplyGapMs = remote.Backend.ReplyGapMs;
        if (remote.Backend.InboxPollMs > 0) Backend.InboxPollMs = remote.Backend.InboxPollMs;
    }

    /// <summary>保存用户级设置（位置/缩放/置顶/皮肤/几个本机开关）</summary>
    public void SaveUserConfig()
    {
        try
        {
            var dir = Path.GetDirectoryName(UserConfigPath)!;
            Directory.CreateDirectory(dir);
            var user = new PetConfig
            {
                AssetDir = AssetDir,
                Scale = Scale,
                AlwaysOnTop = AlwaysOnTop,
                Locked = Locked,                     // 必须写：不然重启后 Load 拿不到，面板一覆盖就回到默认
                ClickThroughInGame = ClickThroughInGame,
                LastX = LastX,
                LastY = LastY,
                Snap = new SnapConfig { Enabled = Snap.Enabled },
            };
            File.WriteAllText(UserConfigPath, JsonSerializer.Serialize(user, JsonOpts), new UTF8Encoding(false));
        }
        catch { /* 保存失败不影响使用 */ }
    }

    /// <summary>解析出动作帧的绝对路径（过滤掉不存在的文件）</summary>
    public List<string> ResolveFrames(string appDir, string action)
    {
        var frames = new List<string>();
        if (!Actions.TryGetValue(action, out var a) || a.Frames is null) return frames;
        var skinDir = Path.IsPathRooted(AssetDir) ? AssetDir : Path.Combine(appDir, AssetDir);
        foreach (var f in a.Frames)
        {
            var full = Path.Combine(skinDir, f.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full)) frames.Add(full);
        }
        return frames;
    }

    public string SkinDir(string appDir) =>
        Path.IsPathRooted(AssetDir) ? AssetDir : Path.Combine(appDir, AssetDir);
}

/// <summary>
/// 用户配置文件（~/.jingjing-pet/pet.json）里"用户级字段"的读取视图。
/// **全部可空**是关键：null = 文件里根本没这个键（= 用户没配过），
/// 于是可以区分"没配过"和"配成了 false"，不会用 C# 默认值把内置 pet.json 覆盖掉。
/// </summary>
internal sealed class UserPrefs
{
    public string? AssetDir { get; set; }
    public double? Scale { get; set; }
    public bool? AlwaysOnTop { get; set; }
    public bool? Locked { get; set; }
    public bool? ClickThroughInGame { get; set; }
    public double? LastX { get; set; }
    public double? LastY { get; set; }
    public SnapPrefs? Snap { get; set; }
}

internal sealed class SnapPrefs
{
    public bool? Enabled { get; set; }
}

/// <summary>一个动作的帧序列</summary>
/// <summary>
/// **系统动作**：程序按名字直接调用的动作，面板里删不掉。
///   · <c>idle</c>     待机（没别的事时的底噪）
///   · <c>talk</c>     正在说话（气泡逐字出字期间循环播放）
///   · <c>thinking</c> 正在等回复（主人发话到她说第一句之间循环播放）
///   · <c>startup</c>  登场（开机播一遍，播完自动进 idle）
///   · <c>error</c>    出错了（后端连不上 / 回复失败时播，提醒主人她卡住了）
/// 删掉任何一个，她就会缺一个状态——所以面板不给删除按钮，后端保存时也会把它们补回来。
/// </summary>
public static class SystemActions
{
    public const string Idle = "idle";
    public const string Talk = "talk";
    public const string Thinking = "thinking";
    public const string Startup = "startup";
    public const string Error = "error";

    public static readonly string[] All = { Idle, Talk, Thinking, Startup, Error };

    /// <summary>
    /// **"必须有"的三个状态**：idle / talk / thinking。
    /// 缺了她就没法正常待机说话，所以没配帧路径时要喊出来（startup / error 属可选，缺了只是没那个动画）。
    /// </summary>
    public static readonly string[] Core = { Idle, Talk, Thinking };

    public static bool Is(string? name) =>
        !string.IsNullOrWhiteSpace(name) && All.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static bool IsCore(string? name) =>
        !string.IsNullOrWhiteSpace(name) && Core.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
}

public sealed class PetAction
{
    /// <summary>帧图片（相对皮肤目录，如 idle/1.png）</summary>
    public List<string> Frames { get; set; } = new();

    /// <summary>播放帧率（帧/秒）；GIF 自带的逐帧延时优先，这里只对 PNG 序列生效</summary>
    public double Fps { get; set; } = 4;

    /// <summary>是否循环（false = 播完停在最后一帧）</summary>
    public bool Loop { get; set; } = true;

    /// <summary>
    /// **触发词**（非系统动作才有意义）：她说的句子里出现这些词，就在 talk 结束后播这个动作。
    /// 留空则该动作只能被 [动作名] 标记 / 工具绑定触发（也会回落到内置默认表）。
    /// </summary>
    public List<string> Triggers { get; set; } = new();

    /// <summary>
    /// **权重**：多个动作的触发词同时命中时，权重高的播；权重相同则「在句子里出现更晚」的那个播。
    /// </summary>
    public double Weight { get; set; } = 1;

    /// <summary>
    /// **绑定的工具**（工具名，见面板的 Tools 列表）：她调用这个工具时就播本动作。
    /// ⚠️ 一个工具只能绑到一个动作上——面板和后端都会去重（先到先得）。
    /// </summary>
    public List<string> Tools { get; set; } = new();
}

/// <summary>后端配置：连 QQBot 的宠物接口（她的"大脑"在那边）</summary>
public sealed class BackendConfig
{
    /// <summary>是否接入后端；false = 回显模式（不连 QQBot，只验证交互手感）</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>QQBot 后台地址（就是管理面板那个端口）</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:7088";

    /// <summary>访问令牌（QQBot 的 Admin.Token）</summary>
    public string Token { get; set; } = "change-me";

    /// <summary>单次对话超时（秒）；她在后端可能要规划/查记忆/调工具，别太短</summary>
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>多条回复之间的播放间隔（毫秒）——她一次说好几句话时，逐条冒气泡</summary>
    public int ReplyGapMs { get; set; } = 900;

    /// <summary>
    /// 轮询她有没有主动找你说话的间隔（毫秒，默认 3 秒）。
    /// 这个轮询同时是**心跳**：后端据此判断"精灵在不在"，她主动找你时才知道该弹气泡还是发 QQ。
    /// </summary>
    public int InboxPollMs { get; set; } = 3000;
}

/// <summary>屏幕边缘吸附配置</summary>
public sealed class SnapConfig
{
    /// <summary>是否启用（松手后自动贴到最近的屏幕边缘）</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>吸附触发距离（像素）：离边缘多近才吸</summary>
    public double Threshold { get; set; } = 36;

    /// <summary>是否用动画滑过去（false = 直接跳）</summary>
    public bool Animate { get; set; } = true;
}

/// <summary>气泡外观配置</summary>
public sealed class BubbleConfig
{
    /// <summary>文字颜色</summary>
    public string Foreground { get; set; } = "#FF2B2B33";

    /// <summary>气泡背景（支持 #AARRGGBB 半透明）</summary>
    public string Background { get; set; } = "#F2FFFFFF";

    /// <summary>边框颜色</summary>
    public string Border { get; set; } = "#552B2B33";

    /// <summary>字体（系统字体名）</summary>
    public string FontFamily { get; set; } = "Microsoft YaHei UI";

    /// <summary>字号</summary>
    public double FontSize { get; set; } = 14;

    /// <summary>气泡最大宽度（像素）</summary>
    public double MaxWidth { get; set; } = 320;

    /// <summary>
    /// 保底停留秒数（公式里的 m）：不管内容多短都至少显示这么久。
    /// **&lt;= 0 = 不自动消失**（点一下才关，跟原来的 autoHideMs=0 一个意思）。
    /// </summary>
    public double BaseSeconds { get; set; } = 4;

    /// <summary>每个字符追加的秒数（公式里的 n）：停留时长 = m + 字数 × n</summary>
    public double SecondsPerChar { get; set; } = 0.25;

    /// <summary>最长停留秒数（0 = 不限）——防超长文本把气泡钉死在屏幕上</summary>
    public double MaxSeconds { get; set; } = 0;

    /// <summary>
    /// 打字机速度（字/秒）：气泡里的文字逐字冒出来。**≤ 0 = 关掉，整段直接显示**。
    /// 逐字出完之后**才开始算**上面那条停留时长——所以不会出现"字还没看完就没了"。
    /// </summary>
    public double TypeSpeed { get; set; } = 28;

    /// <summary>圆角半径</summary>
    public double CornerRadius { get; set; } = 12;

    /// <summary>
    /// **气泡与气泡之间的间隔（保底秒数 g）**：连播多段回复时，上一段消失后先空这么久再出下一段。
    /// 用的还是停留那条公式的形态：<c>间隔 = g + 上一段字数 × h</c>，只是 g/h 可以比停留小得多
    /// （停留是"给你看的时间"，间隔是"喘口气"）。
    /// </summary>
    public double GapSeconds { get; set; } = 0.6;

    /// <summary>间隔公式里每个字符追加的秒数（h）</summary>
    public double GapSecondsPerChar { get; set; } = 0.03;

    /// <summary>最长间隔秒数（0 = 不限）</summary>
    public double GapMaxSeconds { get; set; } = 0;

    /// <summary>
    /// 按公式算"这一句说完之后要空多久"：<c>g + 字数 × h</c>。
    /// **每段单独算**——长句后面空久一点，短句就紧跟着来。
    /// 两个参数都 &lt;= 0 时返回 0（表示完全不留间隔）。
    /// </summary>
    public int GapMs(int charCount)
    {
        if (GapSeconds <= 0 && GapSecondsPerChar <= 0) return 0;
        var seconds = GapSeconds + Math.Max(0, charCount) * GapSecondsPerChar;
        if (GapMaxSeconds > 0) seconds = Math.Min(seconds, GapMaxSeconds);
        return (int)Math.Round(Math.Max(0, seconds) * 1000);
    }

    /// <summary>逐字出完整段话要多少毫秒（0 = 不逐字）</summary>
    public int TypeMs(int charCount)
        => TypeSpeed <= 0 ? 0 : (int)Math.Round(Math.Max(0, charCount) / TypeSpeed * 1000);

    /// <summary>
    /// 按公式算气泡停留多少毫秒：<c>m + 字数 × n</c>。
    /// 图片气泡再叠一个 12 秒保底（图片没有字数，得给够看的时间）；返回 0 = 不自动消失。
    /// </summary>
    public int DurationMs(int charCount, bool isImage)
    {
        if (BaseSeconds <= 0) return 0;
        var seconds = BaseSeconds + charCount * SecondsPerChar;
        if (isImage) seconds = Math.Max(seconds, 12);
        if (MaxSeconds > 0) seconds = Math.Min(seconds, MaxSeconds);
        return (int)Math.Round(Math.Max(1, seconds) * 1000);
    }
}
