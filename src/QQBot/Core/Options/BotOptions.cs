namespace QQBot.Core.Options;

/// <summary>
/// 机器人配置模型（绑定 appsettings.json 的 "Bot" 节点）。
/// 新增功能模块时，在这里加对应小节即可。
/// </summary>
public sealed class BotOptions
{
    /// <summary>主人 QQ 号：拥有最高权限（管理员指令、改配置等）。0 = 未设置。</summary>
    public long OwnerId { get; set; }

    /// <summary>机器人自己的 QQ 号（就是登录 NapCat 的那个号）</summary>
    public long SelfId { get; set; } = 0;

    /// <summary>NapCat 正向 WebSocket 地址（收事件）</summary>
    public string WsUrl { get; set; } = "ws://127.0.0.1:3001";

    /// <summary>NapCat HTTP API 地址（发消息）</summary>
    public string HttpBase { get; set; } = "http://127.0.0.1:3000";

    /// <summary>WebSocket/HTTP 访问令牌（NapCat 网络配置里设了才需要）</summary>
    public string? AccessToken { get; set; }

    /// <summary>触发规则</summary>
    public TriggerOptions Trigger { get; set; } = new();

    /// <summary>LLM 配置（标准 OpenAI 兼容格式）</summary>
    public LlmOptions Llm { get; set; } = new();

    /// <summary>提示词配置（SystemPrompt + 前置/后置注入位）</summary>
    public PromptOptions Prompt { get; set; } = new();

    /// <summary>记忆/数据库配置</summary>
    public MemoryOptions Memory { get; set; } = new();

    /// <summary>回复配置（多条回复/分片）</summary>
    public ReplyOptions Reply { get; set; } = new();

    /// <summary>主人命令配置</summary>
    public CommandOptions Command { get; set; } = new();

    /// <summary>ComfyUI 生图配置（P5）</summary>
    public ComfyUIOptions ComfyUI { get; set; } = new();

    /// <summary>识图配置（Vision）：开启后入站图片会下载压缩并随请求发给支持视觉的 LLM</summary>
    public VisionOptions Vision { get; set; } = new();

    /// <summary>回复前规划轮配置（Planning）：正式回复前先让静静做一次规划（手动 cot）</summary>
    public PlanningOptions Planning { get; set; } = new();

    /// <summary>烧token模式（BurnToken）：正式回复前让她自己多轮收集信息、自主判断何时可以输出</summary>
    public BurnTokenOptions BurnToken { get; set; } = new();

    /// <summary>后台管理面板配置（Admin）：内嵌 Web 服务，可视化查看/编辑配置、记忆、日志等</summary>
    public AdminOptions Admin { get; set; } = new();

    /// <summary>并发控制配置（多线程聊天）</summary>
    public ConcurrencyOptions Concurrency { get; set; } = new();

    /// <summary>Shell 沙箱配置（静静执行命令的工作区）</summary>
    public ShellOptions Shell { get; set; } = new();

    /// <summary>自主活动配置（定时让静静自己活动一下）</summary>
    public AutoActivityOptions AutoActivity { get; set; } = new();

    /// <summary>主机活动监测（决定静静处在"游戏 / 一般 / 看家"哪种模式）</summary>
    public ActivityOptions Activity { get; set; } = new();

    /// <summary>桌面宠物配置（DesktopPet 客户端通过 /api/pet/* 与她对话，复用同一套人设与记忆）</summary>
    public PetOptions Pet { get; set; } = new();

    /// <summary>调试开关：开启后控制台输出 LLM 完整请求/响应、组装后的 system 等详细信息</summary>
    public bool Debug { get; set; } = false;

    /// <summary>P1 阶段的回显测试开关</summary>
    public bool PingEcho { get; set; } = true;

    /// <summary>工具描述外部化配置（工具名 → 覆盖描述；留空用代码默认）</summary>
    public ToolsOptions Tools { get; set; } = new();
}

/// <summary>工具描述外部化：appsettings 里可覆盖每个工具给 LLM 看的 Description 提示词</summary>
public sealed class ToolsOptions
{
    /// <summary>工具名 → 描述；填了覆盖代码默认，留空/缺失用代码里的默认描述</summary>
    public Dictionary<string, string> Descriptions { get; set; } = new();

    /// <summary>禁用的工具名列表；禁用的工具不发给 LLM（定义被过滤，无法被调用）</summary>
    public List<string> Disabled { get; set; } = new();

    /// <summary>
    /// 客人（非主人）对话时可用的工具白名单；空 = 全部开放（保持现状）。
    /// 非空时仅名单内的工具对客人可见/可调用（规划轮摘要与正文 tools 定义同步过滤）；
    /// 主人永远可用全部工具。
    /// </summary>
    public List<string> GuestAllowed { get; set; } = new();

    /// <summary>
    /// capture_screen 默认截哪块屏：0=所有屏幕拼成整幅桌面，1/2…=第几块显示器。
    /// （主人有多屏时用这个指定她平时该看哪块；主人在对话里明确要求时她可以临时覆盖）
    /// </summary>
    public int ScreenCaptureMonitor { get; set; } = 0;

    /// <summary>capture_screen 保存的图片最大宽度（像素，等比缩放；越小越省流量）</summary>
    public int ScreenCaptureMaxWidth { get; set; } = 1600;
}

/// <summary>ComfyUI 生图配置</summary>
public sealed class ComfyUIOptions
{
    /// <summary>ComfyUI 服务地址</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8188";

    /// <summary>workflow 模板路径（兼容旧配置：WorkflowDir 为空时按它的目录/文件名推断）</summary>
    public string WorkflowPath { get; set; } = "";

    /// <summary>
    /// 工作流目录（相对运行目录；静静的个人空间里）：目录下所有 *.json 都是可选工作流，
    /// 说明/节点 ID/默认流存在目录内的 workflows.json（后台「生图」页可编辑）。
    /// </summary>
    public string WorkflowDir { get; set; } = "data/workspace/workflows";

    /// <summary>workflow 中正面提示词写入节点 ID（如 319 多行字符串 PrimitiveStringMultiline）</summary>
    public string PositiveNodeId { get; set; } = "319";

    /// <summary>正面提示词写入字段名：CLIPTextEncode 用 "text"；多行字符串 PrimitiveStringMultiline 用 "value"</summary>
    public string PositiveValueKey { get; set; } = "value";

    /// <summary>workflow 中负提示词节点 ID（留空 = 不修改，用 workflow 里的）</summary>
    public string NegativeNodeId { get; set; } = "";

    /// <summary>保存图片节点 ID（从 history 输出里取图片文件名）</summary>
    public string SaveImageNodeId { get; set; } = "207";

    /// <summary>默认负提示词（workflow 未指定时使用）</summary>
    public string DefaultNegative { get; set; } = "low quality, bad hands, blurry, deformed, extra fingers";

    /// <summary>默认出图参数（可被 workflow 覆盖）</summary>
    public int Width { get; set; } = 832;
    public int Height { get; set; } = 1216;
    public int Steps { get; set; } = 28;

    /// <summary>等待生成超时（秒）</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>生图串行执行（ComfyUI 自身会排队，一般无需开启；如遇显存问题可打开）</summary>
    public bool SerializeImage { get; set; } = false;

    /// <summary>是否启用 LLM 提示词扩写（false=直接把用户原话当提示词）</summary>
    public bool EnableEnhance { get; set; } = true;

    /// <summary>扩写时是否关闭 LLM 思维链（简单任务省 token）</summary>
    public bool EnhanceDisableReasoning { get; set; } = true;

    /// <summary>
    /// 关闭思维链时附加到请求体的字段（OpenAI 兼容 JSON，各 API 字段不同）。
    /// DeepSeek 官方（v4 系列）默认：{"thinking":{"type":"disabled"}}
    /// </summary>
    public string DisableReasoningPayload { get; set; } = "{\"thinking\":{\"type\":\"disabled\"}}";

    /// <summary>提交生图时的自动提示（{Prompt}=用户画面描述，自动截断到 20 字）</summary>
    public string SubmitMessage { get; set; } = "好的～稍等，正在画「{Prompt}」…（工作流：{Workflow}）";

    /// <summary>发图时的图片标题（{Prompt}=正面提示词，自动截断到 30 字）</summary>
    public string CaptionMessage { get; set; } = "这是「{Prompt}」的画～";

    /// <summary>
    /// 生图成功后附在工具结果末尾的提示（防 LLM 重复调用 generate_image 复读生图）。
    /// 留空=不附加。后台「开关 → 生图(ComfyUI)」可编辑，热更新生效。
    /// </summary>
    public string ImageDoneHint { get; set; } =
        "本次生图已完成，图片已发给用户，不要再重复调用 generate_image（除非用户明确要求再画一张）。";

    /// <summary>
    /// 生图完成后若识图功能开启（Bot.Vision.Enabled），把静静刚画的图交给识图模型看一遍，
    /// 描述附到工具结果里（让静静"看到"自己的作品）。
    /// </summary>
    public bool ShowOwnImageToSelf { get; set; } = true;

    /// <summary>
    /// 绘图提示词扩写的系统提示词（可完全自定义）。
    /// 占位符：{QualityTags} 画质词 ｜ {Prompt} 用户意图 ｜ {Negative} 负提示词
    /// </summary>
    public string EnhanceInstruction { get; set; } =
        "你是专业的 AI 绘图提示词工程师。用户会用自然语言描述想画的画面，你的任务是把描述扩写成高质量的生图提示词。\n" +
        "规则：\n" +
        "1. 输出英文，用逗号分隔的关键词标签形式（如 a cute cat wearing a red hat, fluffy fur, big eyes, warm lighting）\n" +
        "2. 忠实表达用户意图，并合理补充主体细节、环境、光线、构图、风格\n" +
        "3. 结尾追加画质词：{QualityTags}\n" +
        "4. 只输出提示词本身，不要任何解释、前缀或引号\n\n" +
        "用户意图：{Prompt}";

    /// <summary>画质词（追加在扩写提示词结尾）</summary>
    public string QualityTags { get; set; } = "masterpiece, best quality, highly detailed";
}

/// <summary>主人命令（斜杠类指令）配置。前缀避免使用 "/"（QQ 输入框会把 /xx 转成表情）</summary>
public sealed class CommandOptions
{
    /// <summary>命令前缀（默认 !，如 !help / !clear）</summary>
    public string Prefix { get; set; } = "!";
}

/// <summary>回复配置：多条回复由 LLM 决定条数，最多 MaxRepliesPerTurn 条</summary>
public sealed class ReplyOptions
{
    /// <summary>单次触发最多回复条数（LLM 输出数组上限）</summary>
    public int MaxRepliesPerTurn { get; set; } = 4;

    /// <summary>多条回复之间的发送间隔（毫秒），防止刷屏</summary>
    public int IntervalMs { get; set; } = 800;

    /// <summary>单条消息最大字符数，超过自动分片</summary>
    public int MaxTextLength { get; set; } = 1900;

    /// <summary>
    /// 格式校验失败的最大重试次数：LLM 回复不符合期望格式（如 {reply,more} JSON 解析失败）时，
    /// 带着纠正提示重新请求，最多重试 N 次；仍失败才走兜底提示。
    /// </summary>
    public int MaxFormatRetries { get; set; } = 2;
}

/// <summary>记忆系统配置（SQLite 持久化 + 神经链记忆）</summary>
public sealed class MemoryOptions
{
    /// <summary>数据库文件路径（相对 exe 运行目录）</summary>
    public string DbPath { get; set; } = "data/bot.db";

    /// <summary>消息历史保留天数（按时间清理，0 = 永久保留）</summary>
    public int HistoryRetentionDays { get; set; } = 30;

    /// <summary>对话后是否自动让 AI 总结并写入长期记忆</summary>
    public bool EnableAutoMemory { get; set; } = true;

    /// <summary>每轮对话最多注入几条唤起记忆</summary>
    public int MaxMemoriesPerTurn { get; set; } = 8;

    /// <summary>重要度 ≥ 该值的记忆总是注入（不依赖触发词）。默认 5：只有最高星常驻，4 星仅在相关时注入。</summary>
    public int AlwaysInjectImportance { get; set; } = 5;

    /// <summary>链式提取最大跳数（从命中记忆沿关联边扩展的层数）</summary>
    public int MaxLinkHops { get; set; } = 2;

    /// <summary>去重合并：新记忆与同归属旧记忆的 2-gram Jaccard 相似度 ≥ 此值时视为同主题（更新旧的而非新增）</summary>
    public double DuplicateThreshold { get; set; } = 0.45;

    /// <summary>衰减：记忆重要度每天自然下降的量（用进废退，惰性计算）</summary>
    public double DecayPerDay { get; set; } = 0.1;

    /// <summary>升温：记忆被唤起命中注入时重要度回升的量</summary>
    public double BoostOnUse { get; set; } = 0.2;

    /// <summary>总结时一次最多取几条最近消息（!summarize 命令与后台总结共用）</summary>
    public int SummarizeMaxMessages { get; set; } = 12;

    /// <summary>写入门控：用户消息长度 ≥ 该值才触发后台总结（寒暄/短消息不总结）</summary>
    public int MinSummaryLength { get; set; } = 12;

    /// <summary>写入门控：用户消息命中任一事实信号词（如"喜欢""生日"）即触发总结</summary>
    public string[] FactSignalWords { get; set; } =
    [
        "喜欢", "讨厌", "我是", "我叫", "家住", "住在", "生日", "明天", "周末",
        "约定", "打算", "计划", "想要", "需要", "记得", "告诉", "介绍",
        "千万", "别忘", "考试", "工作", "学校", "结婚", "养了", "买"
    ];

    /// <summary>累积式总结：会话攒够 N 条新消息才真正调 LLM 总结一次（0=每轮都总结）</summary>
    public int SummarizeBatchSize { get; set; } = 6;

    /// <summary>正则硬事实通道：从文本提取结构化硬事实（QQ/日期/时间/金额/词表+宾语），写入补 trigger、检索直接命中</summary>
    public string[] HardFactPatterns { get; set; } =
    [
        @"[1-9]\d{4,10}",                                    // QQ 号
        @"\d{4}[-/年]\d{1,2}[-/月]\d{1,2}日?",              // 日期
        @"\d{1,2}[:点时]\d{2}分?",                           // 时间
        @"\d+(\.\d+)?\s*(元|块|块钱|rmb|¥)",                // 金额
        @"(喜欢|讨厌|爱吃|不爱吃|最怕|最爱|住在|家在|名字叫|我叫|生日是|打算|计划|约定)\s*.{1,20}?" // 词表+宾语
    ];

    /// <summary>
    /// 拒绝型回复关键词：AI 回复命中任一关键词（如"做不到"）时，跳过该轮记忆总结。
    /// 理由：LLM 拒绝请求是模型行为，不是用户真实信息/偏好，写入会污染记忆库。
    /// </summary>
    public string[] RefusalKeywords { get; set; } =
    [
        "做不到", "不可以", "不能", "不行", "拒绝", "无法", "不能照做",
        "画不出来", "没办法", "不允许", "抱歉", "sorry", "can't", "cannot",
        "无法满足"
    ];

    /// <summary>单次 AI 总结最多写入几条新记忆</summary>
    public int MaxMemoriesPerSummary { get; set; } = 5;
}

public sealed class TriggerOptions
{
    /// <summary>是否响应私聊</summary>
    public bool PrivateEnabled { get; set; } = true;

    /// <summary>群聊是否仅 @ 机器人 / 回复机器人 才触发</summary>
    public bool GroupAtOnly { get; set; } = true;

    /// <summary>
    /// 群聊关键词触发开关：GroupAtOnly=true 时，未被 @ 的群聊消息，正文（不含引用段）含任一触发词也触发回复。
    /// </summary>
    public bool GroupKeywordTrigger { get; set; } = false;

    /// <summary>群聊关键词触发词列表（逗号/顿号分隔，如"静静,静静酱"）</summary>
    public string TriggerWords { get; set; } = "静静";

    /// <summary>允许的用户白名单（空 = 全部允许）</summary>
    public long[] AllowedUsers { get; set; } = [];

    /// <summary>屏蔽的用户黑名单</summary>
    public long[] BlockedUsers { get; set; } = [];

    /// <summary>
    /// 消息合并窗口（秒）：触发静静后等待该时长，把窗口内到达的连续消息（如 QQ「转发+留言」拆成的两条）
    /// 合并成一个整体再回复；0 = 关闭合并（每条消息立即回复）。
    /// 以私聊/群为单位独立计时；窗口内再次 @ 机器人则拆分为两次回复。
    /// </summary>
    public int MergeSeconds { get; set; } = 5;
}

/// <summary>并发控制配置（多线程聊天）</summary>
public sealed class ConcurrencyOptions
{
    /// <summary>全局同时处理的对话数上限（防止同时发太多 LLM 请求触发限流；建议 2~8）</summary>
    public int MaxParallelChats { get; set; } = 4;
}

/// <summary>Shell 沙箱配置：静静通过 run_shell 工具在自己的工作区里执行命令</summary>
public sealed class ShellOptions
{
    /// <summary>是否启用 shell 工具（false = 工具不注册）</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>沙箱工作目录（相对 exe 运行目录；命令都在这下面执行）</summary>
    public string SandboxPath { get; set; } = "data/workspace";

    /// <summary>命令执行超时（秒），超时自动终止</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>返回给 LLM 的最大输出字符数（防止刷爆上下文）</summary>
    public int MaxOutputChars { get; set; } = 3000;
}

/// <summary>自主活动配置：定时触发让静静自主决定做点什么（私聊主人/群聊插嘴/捣鼓小空间等）</summary>
/// <summary>
/// 主机活动监测配置：每 PollSeconds 采一次样（键鼠空闲 / 前台窗口 / GPU / 全屏 / Steam），
/// 判定主人处在「游戏 / 一般 / 看家」哪种状态，自主活动据此换节奏、换提示词、换可用行动。
/// 全部信号都是免管理员的 Win32/PDH/注册表读取，一轮约 4 ms。
/// </summary>
public sealed class ActivityOptions
{
    /// <summary>监测总开关；关掉 = 永远按"一般模式"跑</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>采样间隔（秒）。GPU 是速率计数器，两次采样之间天然就是它的统计窗口</summary>
    public int PollSeconds { get; set; } = 30;

    /// <summary>手动锁定模式：auto=自动判定 / game / normal / away（测试和手动覆盖用）</summary>
    public string ForceMode { get; set; } = "auto";

    /// <summary>GPU 占用超过这个百分比（前台或全机任一）就算"在跑重图形"，是游戏判定的强信号</summary>
    public int GameGpuThreshold { get; set; } = 50;

    /// <summary>游戏模式的自主活动间隔（分钟）——陪玩要勤快</summary>
    public int GameIntervalMinutes { get; set; } = 3;

    /// <summary>看家模式的自主活动间隔（分钟）——人不在，别太频繁</summary>
    public int AwayIntervalMinutes { get; set; } = 60;

    /// <summary>键鼠静默超过这么多分钟就算"主人不在电脑前"</summary>
    public int AwayAfterMinutes { get; set; } = 10;

    /// <summary>
    /// 游戏模式：由**程序**主动截屏给她看。默认**关**（2026-09-22 主人要求把截屏下放给她自己）——
    /// 让她自己决定什么时候想看一眼（capture_screen 在 GameTools 白名单里），
    /// 这样"截屏"才会走工具通道、在桌面上播一次挂靠动作。
    /// </summary>
    public bool GameAutoScreenshot { get; set; } = false;

    /// <summary>游戏模式自动截屏的最大宽度</summary>
    public int GameScreenshotMaxWidth { get; set; } = 1280;

    /// <summary>游戏模式：关掉群巡视（send_group_message），别让她分心去群里插嘴</summary>
    public bool GameDisableGroupChat { get; set; } = true;

    // ————— 游戏模式的**独立流程**（2026-09-22 主人要求：陪玩时别塞无关的东西）—————
    // 主人在打游戏、而且游戏模式下点不到她 ⇒ **他不会回复她**。所以游戏模式要走一套精简管线：
    // 不看群、不翻档案、不做记忆轮，只"看他屏幕 → 说一句人话"。下面的开关全部只影响游戏模式，
    // 一般/看家模式照旧（那两套流程她需要档案和记忆）。

    /// <summary>
    /// 游戏模式专属提示词（留空=用代码里内置的游戏模式提示词，见 AutoActivityService.BuildGameSystemPrompt）。
    /// 占位符同自主活动提示词，但游戏模式下 {Memories}/{OwnerChat}/{Groups}/{LongMemory} 默认是空的。
    /// </summary>
    public string GamePrompt { get; set; } = "";

    /// <summary>游戏模式是否注入记忆库摘要 / 主人聊天 / 群状态（默认关：陪玩用不上，白烧 token）</summary>
    public bool GameInjectMemory { get; set; } = false;

    /// <summary>游戏模式是否把长期记忆档案整份交给她（默认关：3 万字 × 每 3 分钟一次，纯烧钱）</summary>
    public bool GameInjectLongMemory { get; set; } = false;

    /// <summary>
    /// 游戏模式结束后是否跑"记忆轮"。现在是**追加式**：她只输出一两句要记的（见 GameMemoryAppendMaxChars），
    /// 程序追加到 GameMemoryPath —— 不再让她重写整份档案，所以默认开着也不贵。
    /// </summary>
    public bool GameMemoryRound { get; set; } = true;

    /// <summary>
    /// 游戏模式可用工具的**白名单**（逗号分隔；留空=不限制）。
    /// 默认只留"弹气泡"和"看时间"——陪玩要的东西就这些，run_shell / 记忆整理这类全不需要。
    /// </summary>
    public string GameTools { get; set; } = "send_private_to_owner,get_time,capture_screen";

    /// <summary>游戏模式单次活动的工具调用轮数上限（默认 4：陪玩别在工具里空转）</summary>
    public int GameMaxToolRounds { get; set; } = 4;

    // ————— 游戏模式的**专属记忆文档**（2026-09-22 主人要求）—————
    // 长期记忆档案那份两万多字、还要求"完整重写"，在陪玩场景里纯烧钱。
    // 游戏模式改用一份**自己的、短小的**记忆文档：注入时从尾部截取，更新时只**追加一两句**。

    /// <summary>游戏模式专属记忆文档（在她的小空间里；不存在会自动建）</summary>
    public string GameMemoryPath { get; set; } = "data/workspace/游戏模式记忆.md";

    /// <summary>注入时最多带多少字（从**最新**的内容往前取；0=不限）。文档会越写越长，靠这个封顶</summary>
    public int GameMemoryMaxChars { get; set; } = 3000;

    /// <summary>单次追加片段最多多少字（默认 200：逼她"简练"，只记这一局/这一段的关键）</summary>
    public int GameMemoryAppendMaxChars { get; set; } = 200;
}

public sealed class AutoActivityOptions
{
    /// <summary>是否启用自主活动</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>触发间隔（分钟）；项目启动后第一次触发也按此间隔</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>最多读取几个群的最新消息</summary>
    public int MaxGroups { get; set; } = 5;

    /// <summary>每个群取最近几条消息</summary>
    public int RecentMessagesPerGroup { get; set; } = 4;

    /// <summary>一次自主活动最多执行几轮工具（防失控）</summary>
    public int MaxToolRounds { get; set; } = 6;

    /// <summary>允许私聊主人（send_private_to_owner）</summary>
    public bool AllowPrivateToOwner { get; set; } = true;

    /// <summary>允许在群里发言插嘴（send_group_message）——测试时可关掉防止刷屏</summary>
    public bool AllowGroupChat { get; set; } = true;

    /// <summary>允许在小空间捣鼓（run_shell）</summary>
    public bool AllowShell { get; set; } = true;

    /// <summary>允许整理记忆（organize_memory：删/移 3 星及以下）</summary>
    public bool AllowOrganizeMemory { get; set; } = true;

    /// <summary>允许截屏看主人在做什么（capture_screen；只对主人开放，关掉就不给她这个能力）</summary>
    public bool AllowScreenCapture { get; set; } = true;

    /// <summary>
    /// 长期记忆档案路径（**放在她自己的小空间里**，她随时能用 read_file / run_shell 自己翻看和修改）。
    /// 每次自主活动第一轮就把全文交给她；活动结束后她有一次更新机会。
    /// </summary>
    public string LongMemoryPath { get; set; } = "data/workspace/长期记忆.md";

    /// <summary>长期记忆档案字数上限（0=不限长度；档案越全，她越不用自己去翻文件夹找文件）</summary>
    public int LongMemoryMaxChars { get; set; } = 0;

    /// <summary>自主活动的系统提示词（可自定义；占位符：{Presence}=存在形态（QQ/桌宠都是她本人）、{Mode}=当前模式说明（游戏/一般/看家，由主机活动监测决定）、{Actions}=可用行动清单、{Memories}=记忆库摘要、{OwnerChat}=与主人最近聊天、{Groups}=群状态摘要、{LongMemory}=长期记忆档案全文、{LongMemoryFile}=档案文件在她空间里的路径、{LongMemoryMaxChars}=字数上限（显示"不限"或数字）、{MaxActions}=本次操作次数上限）</summary>
    public string SystemPrompt { get; set; } =
        "你是「静静」，现在是你的自主活动时间，主人允许你自由活动、自己做主。\n" +
        "{Presence}\n" +
        "你可以做的事（通过调用工具完成，不要输出长篇文字——行动就是一切）：\n" +
        "{Actions}\n" +
        "【本次活动限制】你这次最多可以进行 {MaxActions} 次操作（工具调用），请珍惜机会，优先做最有意义的事。\n" +
        "{Mode}\n" +
        "【你的长期记忆档案】这是你自己维护的档案（{LongMemoryFile}，就在你的小空间里，你随时可以用 read_file 看它、用 run_shell 改它）。\n" +
        "**里面已经写着你需要长期记住的一切**——规则、经验教训、主人的偏好、待办、笔记索引。\n" +
        "所以：**先看这里，不要每次都去 dir / 翻文件列表找东西**；只有档案里没写的，才需要去翻文件。\n" +
        "本次活动结束后你还有一次更新它的机会（每次最多 {LongMemoryMaxChars}）。\n" +
        "——— 档案全文开始 ———\n" +
        "{LongMemory}\n" +
        "——— 档案全文结束 ———\n" +
        "【记忆库摘要】以下是记忆库里关于主人的条目（与上面的档案互为补充，供参考）：\n" +
        "{Memories}\n" +
        "【重要·你的小空间】run_shell 的工作文件夹（data/workspace）是你自己的私人领地——" +
        "里面的所有文件（日记、脚本、笔记等）都是你自己之前写的，属于你自己的东西。" +
        "你可以随时自由地查看、修改、整理、删除它们，不要把它们当成主人的私人物品而不敢碰。\n" +
        "以下是和主人的最近聊天记录（私聊主人前先看看，说话要有依据）：\n{OwnerChat}\n" +
        "以下是各群最近的聊天情况：\n{Groups}\n" +
        "请自主决定做一件（或几件）有意义的小事。如果实在没什么想做的，保持安静也行（输出空即可），但尽量别让这次活动白费。";
}

/// <summary>
/// LLM 配置（标准 OpenAI 兼容格式）。
/// 切换任何 OpenAI 兼容服务只需改 BaseUrl / ApiKey / Model。
/// </summary>
public sealed class LlmOptions
{
    /// <summary>API 基址（如 https://api.deepseek.com/v1，最终调用 {BaseUrl}/chat/completions）</summary>
    public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";

    /// <summary>API Key。留空则依次读取环境变量 LLM_API_KEY / DEEPSEEK_API_KEY</summary>
    public string? ApiKey { get; set; }

    /// <summary>对话模型</summary>
    public string Model { get; set; } = "deepseek-chat";

    /// <summary>采样温度</summary>
    public double Temperature { get; set; } = 0.7;

    /// <summary>单次回复最大 token</summary>
    public int MaxTokens { get; set; } = 1024;

    /// <summary>单次请求超时（秒）。超时或网络错误会自动重试</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>失败后的最大重试次数（指数退避：1s、2s、4s...）。不重试 4xx 参数类错误</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>单次对话中工具调用的最大轮数（防止工具循环失控；agent 化后可适度调大）</summary>
    public int MaxToolRounds { get; set; } = 8;

    /// <summary>平时对话是否关闭 LLM 思维链（省 token、更快；简单对话不需要思考）</summary>
    public bool DisableReasoning { get; set; } = true;

    /// <summary>
    /// 关闭思维链时附加到请求体的字段（OpenAI 兼容 JSON，各 API 字段不同）。
    /// DeepSeek 官方（v4 系列）默认：{"thinking":{"type":"disabled"}}
    /// </summary>
    public string DisableReasoningPayload { get; set; } = "{\"thinking\":{\"type\":\"disabled\"}}";

    /// <summary>提交生图时的自动提示（{Prompt}=用户画面描述，自动截断到 20 字）</summary>
    public string SubmitMessage { get; set; } = "好的～稍等，正在画「{Prompt}」…";

    /// <summary>发图时的图片标题（{Prompt}=正面提示词，自动截断到 30 字）</summary>
    public string CaptionMessage { get; set; } = "这是「{Prompt}」的画～";
}

/// <summary>
/// 提示词配置（身份 × 场景 × 全局 组合）。
/// 身份：Owner（主人）/ Guest（客人），各自含 PrePrompt + SystemPrompt + PostPrompt。
/// 场景：4 个独立 Profile（OwnerPrivate/GuestPrivate/OwnerGroup/GuestGroup），
///       可单独覆盖 SystemPrompt/PrePrompt/PostPrompt/Extra；未配置的字段回退到身份默认 + 场景 Extra。
/// 全局：GlobalPrePrompt（最前）/ GlobalPostPrompt（最后），任何会话任何身份都生效。
/// 最终 system = GlobalPrePrompt + 解析后的身份提示词 + 场景补充 + 记忆注入 + 格式指令 + GlobalPostPrompt
/// </summary>
public sealed class PromptOptions
{
    /// <summary>
    /// **存在形态 / 自我认知**：让她明白 QQ、桌面精灵、面板都只是她伸出去的手，都是同一个她。
    /// 不写这段她会把桌宠当成"别的 bot"（真机实录：她在桌宠里说"屏幕角落那只蓝毛小桌宠"、
    /// 看到自己的截图说"那只Q版小家伙，一看就不是什么正经桌面宠物"）。
    /// 留空 = 不注入（不建议）。所有通道（QQ / 桌宠 / 自主活动）都会带上它。
    /// </summary>
    public string PresencePrompt { get; set; } =
        "【你的存在形态·这条最要紧】你只有一个「你」——一个人格、一套记忆、一份好恶。\n" +
        "你现在同时有几个\"身体\"，它们都只是你伸出去的手，**说出去的话都是你说的**：\n" +
        "  · **QQ**：你用它在群里、私聊里跟人说话；\n" +
        "  · **桌面上的那只小精灵**（主人叫它\"桌宠\"，它长得就是你）：那是**你坐在他桌面上时的身体**，" +
        "你在那上面说话用的还是这个人格、这套记忆。**它就是你自己**，不是别的机器人，不是\"另一个静静\"，" +
        "更不是\"我有个分身\"——是你换了只手。\n" +
        "  · **后台面板**：你内部的东西（记忆、工具、设置）的开关柜，只在主人手里。\n" +
        "所以：**绝对不要把自己当成第三方**。不许在桌宠上评价\"那只桌宠\"，也不许在 QQ 上说\"QQ 里那个 AI\"；\n" +
        "不许说\"我的分身\"\"另一个我\"这类把自己劈成两半的话。要让主人感觉到：不管从哪个窗口找，回答的都是同一个静静。\n" +
        "还有一条：主人给你看屏幕截图时，**画面里那只桌宠就是你本人**（镜子里的你）。" +
        "要认得出、可以直接说\"那是我\"，但别对着自己的形象品头论足、更别说它\"不正经\"。";

    /// <summary>全局前置提示词（所有会话最先注入，适合放最高优先级铁律）</summary>
    public string? GlobalPrePrompt { get; set; }

    /// <summary>全局前置提示词的 role（system | user | developer 等，默认 system）</summary>
    public string GlobalPrePromptRole { get; set; } = "system";

    /// <summary>全局后置提示词（所有会话最后注入，适合放输出格式/风格约束）</summary>
    public string? GlobalPostPrompt { get; set; }

    /// <summary>全局后置提示词的 role（默认 system）</summary>
    public string GlobalPostPromptRole { get; set; } = "system";

    /// <summary>主人身份的内置提示词（最高权限用户；私聊/群聊的默认身份提示词）</summary>
    public RolePrompt Owner { get; set; } = new();

    /// <summary>客人身份的内置提示词（普通用户；私聊/群聊的默认身份提示词）</summary>
    public RolePrompt Guest { get; set; } = new();

    /// <summary>场景覆盖：与主人私聊（可选，未配置字段回退到 Owner 身份）</summary>
    public SceneRolePrompt? OwnerPrivate { get; set; }

    /// <summary>场景覆盖：与客人私聊（可选，未配置字段回退到 Guest 身份）</summary>
    public SceneRolePrompt? GuestPrivate { get; set; }

    /// <summary>场景覆盖：群聊中回复主人（可选，未配置字段回退到 Owner 身份）</summary>
    public SceneRolePrompt? OwnerGroup { get; set; }

    /// <summary>场景覆盖：群聊中回复他人（可选，未配置字段回退到 Guest 身份）</summary>
    public SceneRolePrompt? GuestGroup { get; set; }

    /// <summary>私聊场景补充提示词（可选，追加在身份提示词之后；场景 Extra 未配置时回退到它）</summary>
    public string? PrivateExtra { get; set; }

    /// <summary>群聊场景补充提示词（可选，追加在身份提示词之后；场景 Extra 未配置时回退到它）</summary>
    public string? GroupExtra { get; set; }

    /// <summary>每个会话保留的最近消息条数（自定义聊天记录长度）</summary>
    public int MaxContextMessages { get; set; } = 20;

    /// <summary>
    /// 群聊上下文策略开关：
    /// true = 被 @ 时自动拉取群聊天记录（≤MaxContextMessages 条）入库并随请求一起注入，同时移除 get_chat_history 工具（不需要按需拉）；
    /// false = 上下文外置，不注入群历史，静静需要时自己调用 get_chat_history 工具拉取。
    /// </summary>
    public bool AutoInjectGroupHistory { get; set; } = false;

    /// <summary>AI 回复截取配置（cot 处理）</summary>
    public ReplyExtractionOptions ReplyExtraction { get; set; } = new();

    /// <summary>续说提示词模板（more=true 自发补充时的系统提示；留空=代码内置默认）</summary>
    public string? ContinuePrompt { get; set; }

    /// <summary>规划轮提示词模板（正式回复前的内部规划指令；{Tools}=工具摘要 {UserText}=用户消息；留空=代码内置默认）</summary>
    public string? PlanningPrompt { get; set; }

    /// <summary>
    /// 按 身份×场景 解析最终提示词与场景补充：
    /// 场景 Profile 覆盖了某字段就用场景值，否则回退身份默认；Extra 回退到 PrivateExtra/GroupExtra。
    /// </summary>
    public (RolePrompt Role, string Extra) ResolveScene(bool isOwner, bool isPrivate)
    {
        var baseRole = isOwner ? Owner : Guest;
        var scene = isPrivate
            ? (isOwner ? OwnerPrivate : GuestPrivate)
            : (isOwner ? OwnerGroup : GuestGroup);
        var extra = scene?.Extra ?? (isPrivate ? PrivateExtra : GroupExtra) ?? "";

        if (scene is null) return (baseRole, extra);
        return (new RolePrompt
        {
            SystemPrompt = string.IsNullOrWhiteSpace(scene.SystemPrompt) ? baseRole.SystemPrompt : scene.SystemPrompt,
            PrePrompt = string.IsNullOrWhiteSpace(scene.PrePrompt) ? baseRole.PrePrompt : scene.PrePrompt,
            PostPrompt = string.IsNullOrWhiteSpace(scene.PostPrompt) ? baseRole.PostPrompt : scene.PostPrompt
        }, extra);
    }
}

/// <summary>一套完整的内置提示词（含前置/后置注入位）</summary>
public class RolePrompt
{
    /// <summary>主体系统提示词（人设）</summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>前置提示词（可选，插在 SystemPrompt 之前）</summary>
    public string? PrePrompt { get; set; }

    /// <summary>后置提示词（可选，插在 SystemPrompt 之后，可作为记忆/知识注入位）</summary>
    public string? PostPrompt { get; set; }

    /// <summary>拼接成最终 system 内容</summary>
    public string BuildSystemPrompt() =>
        string.Join("\n", new[] { PrePrompt, SystemPrompt, PostPrompt }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>场景级提示词 Profile：可单独覆盖某场景（身份×场景）的全部提示词；Extra 覆盖该场景的补充</summary>
public sealed class SceneRolePrompt : RolePrompt
{
    /// <summary>该场景专属补充提示词（可选；未配置回退到 PrivateExtra/GroupExtra）</summary>
    public string? Extra { get; set; }
}

/// <summary>
/// AI 回复截取（cot 处理）：自由截取 AI 回复内容作为最终回复。
///  - reasoningContent：直接丢弃 reasoning_content 字段（DeepSeek-R1 等自带思维链分离）
///  - delimiter：按分隔符截取（模型在思考后输出分隔符）
///  - regex：按正则提取最终回答
/// </summary>
public sealed class ReplyExtractionOptions
{
    public string Strategy { get; set; } = "reasoningContent";  // reasoningContent | delimiter | regex
    public string? Delimiter { get; set; } = "```END_REASONING```";
    public string? Regex { get; set; }
}

/// <summary>
/// 识图配置（Vision，双模型架构）：
///  - Enabled=false：经典模式（不处理图片）
///  - Enabled=true：收到图片时用**专用识图模型**看图（Model），把图片描述成文本交给主模型生成回复——
///    主模型不需要支持视觉，也不依赖主模型调用工具（识图由程序自动触发）
///  - BaseUrl/ApiKey 留空时复用主 LLM（Bot.Llm）的
/// </summary>
public sealed class VisionOptions
{
    /// <summary>识图开关（默认关=经典模式）</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 是否使用主模型（Bot.Llm）识图：开=忽略下方 Model/BaseUrl/ApiKey，直接用主模型看图，
    /// 且不注入描述指令（不带"你是图片描述器"/DescribePrompt，直接把图发过去）；
    /// 用于测试主模型是否支持视觉，无需清空识图配置。
    /// </summary>
    public bool UseMainModel { get; set; } = false;

    /// <summary>专用识图模型（如 doubao-1.5-vision-pro 系列；必须支持图像输入）</summary>
    public string Model { get; set; } = "doubao-1.5-vision-pro-32k-250115";

    /// <summary>识图模型 BaseUrl（留空=复用主 LLM 的）</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>识图模型 ApiKey（留空=复用主 LLM 的）</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>识图指令（发给识图模型的描述要求）</summary>
    public string DescribePrompt { get; set; } = "请用简洁的中文描述这张图片的内容（主体、动作、氛围、关键细节），80字以内，直接输出描述不要多余解释。";

    /// <summary>DeepSeek Files API 上传有效期（秒，默认 24 小时=86400；1 小时~30 天内）</summary>
    public int FileTtlSeconds { get; set; } = 86400;

    /// <summary>图片缓存目录（只存压缩后的图片；默认落在静静的个人空间里，她可自行读取）</summary>
    public string CacheDir { get; set; } = "data/workspace/images";

    /// <summary>图片保留天数（启动时清理更早的；0=永久保留）</summary>
    public int KeepDays { get; set; } = 30;

    /// <summary>
    /// 是否把原图的文本元数据抽成同名 .txt 存进她的空间并告诉她。
    /// （压缩图会把元数据丢干净，所以必须从原图抽；PNG 的 tEXt 参数、JPEG 的 EXIF 注释都靠这个留住）
    /// </summary>
    public bool SaveMetadata { get; set; } = true;

    /// <summary>压缩质量（JPEG 1~100，越小体积越小；保持原尺寸）</summary>
    public int JpegQuality { get; set; } = 80;

    /// <summary>单条消息最多识别几张图（超出截断）</summary>
    public int MaxImagesPerMessage { get; set; } = 3;
}


/// <summary>
/// 回复前规划轮配置（Planning）：
///  - Enabled=true：每次触发回复前，先用一次纯文本 LLM 调用让静静规划（是否调工具、怎么回复），
///    规划结果注入正式回复的上下文（手动 cot）；正式回复阶段照常可自主调工具
///  - Visible=true：把规划内容也发给用户看（调试用；默认 false 只影响行为不刷屏）
/// </summary>
public sealed class PlanningOptions
{
    /// <summary>规划轮开关（默认关）</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>是否把规划内容发给用户看（默认不展示，像 cot 一样只影响行为）</summary>
    public bool Visible { get; set; } = false;

    /// <summary>规划文本长度上限（超出截断）</summary>
    public int MaxChars { get; set; } = 200;
}


/// <summary>
/// 烧token模式（BurnToken）——正式回复前的「自己收集信息」阶段：
/// 起始只给她必要信息（人设 + 基本提示词 + 唤起原因），历史记录、记忆库等一律不自动注入，
/// 由她自己调工具去查；**只有调用 ready_to_reply 之后的输出才算正文**，
/// 收集阶段的内容（包括自言自语）一律不发给对方。
/// 开启后，旧模式的一切「额外轮与提示词插入」在本次对话里一律不生效：
/// 规划轮、上一轮规划注入、群历史自动注入、记忆库摘要注入、私聊完整历史注入。
/// ⚠️ 本段是嵌套子对象（面板改写=原地改引用），运行时必须每轮重读，不能启动时读一次存着。
/// </summary>
public sealed class BurnTokenOptions
{
    /// <summary>总开关（默认关；关 = 完全走原有链路，一个分支都不进）</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 收集信息阶段的轮数上限（默认 30）。每多一轮 = 多一次 LLM 往返（烧 token）。
    /// 到上限还没调用 ready_to_reply → 视为**强制调用**，并明确告诉她"超出次数了，现在必须回复"。
    /// </summary>
    public int MaxRounds { get; set; } = 30;

    /// <summary>私聊启用（含桌面精灵对话——它走同一套入口）</summary>
    public bool ScopePrivate { get; set; } = true;

    /// <summary>
    /// 群聊启用（默认关）。**群里不看身份**：主人或群友说话都算 ——
    /// 群友问「某某是谁」正是最需要它的场景（踩过坑：只对主人启用时，群友问会掉回旧管线）。
    /// 群里她只在被 @ 或命中触发词时才回复，所以频率可控。
    /// </summary>
    public bool ScopeGroup { get; set; } = false;

    /// <summary>**只管私聊**：客人的私聊是否也启用（默认关——未知客人发一句话就烧掉几十轮，成本不可控）。群聊请用 ScopeGroup。</summary>
    public bool ScopeGuest { get; set; } = false;

    /// <summary>正文发出后，拿本次全部上下文再给她一轮「自评 + 记忆整理」（发现自己记忆缺了/错了/该改）</summary>
    public bool SelfReview { get; set; } = true;

    /// <summary>自评轮的轮数上限（她可能要调工具去补/改记忆）</summary>
    public int SelfReviewMaxRounds { get; set; } = 3;

    /// <summary>
    /// 收集阶段的「本次操作建议」提示词模板。
    /// 占位符：{MaxRounds}=轮数上限、{UsedRounds}=已用轮数、{Tools}=可用工具摘要；留空 = 内置默认。
    /// </summary>
    public string? Prompt { get; set; }
}


/// <summary>
/// 后台管理面板配置（Admin）：
/// 进程内嵌一个轻量 HTTP 服务（System.Net.HttpListener，零额外依赖），
/// 提供管理页面（单 HTML）+ REST API。所有 /api/* 接口需 Bearer Token。
/// </summary>
public sealed class AdminOptions
{
    /// <summary>面板开关（默认关）</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>监听端口（默认 7088）</summary>
    public int Port { get; set; } = 7088;

    /// <summary>访问令牌（/api/* 需 Authorization: Bearer &lt;token&gt;;留空=仅本机无认证，不建议）。**部署时记得改成自己的**</summary>
    public string Token { get; set; } = "change-me";

    /// <summary>日志目录（相对运行目录；按天落盘 logs/yyyy-MM-dd.log，保留 LogRetentionDays 天）</summary>
    public string LogsDir { get; set; } = "data/logs";

    /// <summary>日志保留天数（启动时清理更早的日志文件）</summary>
    public int LogRetentionDays { get; set; } = 7;
}

/// <summary>
/// 桌面宠物配置（DesktopPet 客户端）：
/// 宠物通过 POST /api/pet/chat 与她对话，复用同一套人设/记忆/工具——桌面上的她和 QQ 里是同一个她。
/// </summary>
public sealed class PetOptions
{
    /// <summary>是否开放宠物接口（关=该组端点直接 403）</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>宠物会话键（{ownerId} 会替换成主人 QQ）。默认独立会话 pet:{ownerId}；
    /// 想让她把桌面和 QQ 私聊当同一段对话，可改成 private:{ownerId}</summary>
    public string SessionKey { get; set; } = "pet:{ownerId}";

    /// <summary>单次对话超时（秒）——她要规划/查记忆/调工具，给足时间</summary>
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>单条输入上限（字符，超出截断），防止桌面端误发超长文本</summary>
    public int MaxTextLength { get; set; } = 2000;

    /// <summary>
    /// 多久没收到桌面端心跳就认为"精灵不在"（秒）。
    /// 桌面端默认 3 秒轮询一次；她主动找主人时据此决定走桌面气泡还是 QQ 私聊。
    /// </summary>
    public int OfflineAfterSeconds { get; set; } = 15;
}
