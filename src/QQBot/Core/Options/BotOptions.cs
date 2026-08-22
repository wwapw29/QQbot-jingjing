namespace QQBot.Core.Options;

/// <summary>
/// 机器人配置模型（绑定 appsettings.json 的 "Bot" 节点）。
/// 新增功能模块时，在这里加对应小节即可。
/// </summary>
public sealed class BotOptions
{
    /// <summary>主人 QQ 号：拥有最高权限（管理员指令、改配置等）。0 = 未设置。</summary>
    public long OwnerId { get; set; }

    /// <summary>机器人自己的 QQ 号（静静 2049592241）</summary>
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

    /// <summary>后台管理面板配置（Admin）：内嵌 Web 服务，可视化查看/编辑配置、记忆、日志等</summary>
    public AdminOptions Admin { get; set; } = new();

    /// <summary>并发控制配置（多线程聊天）</summary>
    public ConcurrencyOptions Concurrency { get; set; } = new();

    /// <summary>Shell 沙箱配置（静静执行命令的工作区）</summary>
    public ShellOptions Shell { get; set; } = new();

    /// <summary>自主活动配置（定时让静静自己活动一下）</summary>
    public AutoActivityOptions AutoActivity { get; set; } = new();

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
}

/// <summary>ComfyUI 生图配置</summary>
public sealed class ComfyUIOptions
{
    /// <summary>ComfyUI 服务地址</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8188";

    /// <summary>workflow 模板路径（ComfyUI 导出的 API 格式 JSON，相对 exe 运行目录）</summary>
    public string WorkflowPath { get; set; } = "ComfyUI/Workflows/standard.json";

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
    public string SubmitMessage { get; set; } = "好的～稍等，正在画「{Prompt}」…";

    /// <summary>发图时的图片标题（{Prompt}=正面提示词，自动截断到 30 字）</summary>
    public string CaptionMessage { get; set; } = "这是「{Prompt}」的画～";

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

    /// <summary>备忘录文件路径（相对 exe 运行目录；每次自主活动前展示、活动结束后可更新）</summary>
    public string MemoPath { get; set; } = "data/memo.txt";

    /// <summary>备忘录最大字数（超过自动截断）</summary>
    public int MemoMaxChars { get; set; } = 500;

    /// <summary>自主活动的系统提示词（可自定义；占位符：{Actions}=可用行动清单、{Memories}=长期记忆、{OwnerChat}=与主人最近聊天、{Groups}=群状态摘要、{Memo}=备忘录、{MemoMaxChars}=备忘录字数上限、{MaxActions}=本次操作次数上限）</summary>
    public string SystemPrompt { get; set; } =
        "你是「静静」，现在是你的自主活动时间，主人允许你自由活动、自己做主。\n" +
        "你可以做的事（通过调用工具完成，不要输出长篇文字——行动就是一切）：\n" +
        "{Actions}\n" +
        "【本次活动限制】你这次最多可以进行 {MaxActions} 次操作（工具调用），请珍惜机会，优先做最有意义的事。\n" +
        "【你的长期记忆】以下是你长期记住的信息（全局规则 + 关于主人的），自主行动时可参考：\n" +
        "{Memories}\n" +
        "【你的备忘录】以下是你自己维护的备忘录（长期提醒/信条/经验），每次自主活动都会带给你看，活动结束后你还有一次机会更新它（总字数不超过 {MemoMaxChars} 字）：\n" +
        "{Memo}\n" +
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

    /// <summary>图片缓存目录（只存压缩后的图片）</summary>
    public string CacheDir { get; set; } = "data/vision_cache";

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

    /// <summary>访问令牌（/api/* 需 Authorization: Bearer &lt;token&gt;;留空=仅本机无认证，不建议）</summary>
    public string Token { get; set; } = "jingjing-admin";

    /// <summary>日志目录（相对运行目录；按天落盘 logs/yyyy-MM-dd.log，保留 LogRetentionDays 天）</summary>
    public string LogsDir { get; set; } = "data/logs";

    /// <summary>日志保留天数（启动时清理更早的日志文件）</summary>
    public int LogRetentionDays { get; set; } = 7;
}
