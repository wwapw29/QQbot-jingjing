using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using QQBot.Core.Chat;
using QQBot.Core.Commands;
using QQBot.Core.Memory;
using QQBot.Core.OneBot;
using QQBot.Core.Options;
using QQBot.Core.Tools;
using QQBot.Core.Vision;

namespace QQBot.Core.Dispatcher;

/// <summary>
/// 事件分发器：触发过滤 → 主人命令 → Agent 循环（LLM 自主工具调用 + 自发多轮回复）+ 记忆持久化。
/// 并发模型：全局并发门（限制同时处理的对话数）→ 会话级串行锁（同一会话消息按序处理，防止上下文串扰）。
/// </summary>
public sealed class EventDispatcher
{
    private readonly BotOptions _options;
    private readonly OneBotClient _client;
    private readonly ChatContext _context;
    private readonly ChatEngine _engine;
    private readonly Database _users;
    private readonly MemoryService _memory;
    private readonly CommandRouter _commands;
    private readonly ToolRegistry _tools;
    private readonly VisionService _vision;
    private readonly ILogger<EventDispatcher> _logger;

    /// <summary>全局并发门：同一时刻最多 N 个消息在处理（防止 LLM 请求并发过多触发限流）</summary>
    private readonly SemaphoreSlim _globalGate;

    /// <summary>会话级串行锁：同一会话（private:{qq} / group:{群}）的消息按序处理，跨会话并行</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();

    /// <summary>ping 测试匹配：整条消息仅由 ping + 空白/标点/波浪号组成才回显 pong（防止子串误触发）</summary>
    private static readonly Regex PingPattern = new(
        @"^\s*ping[\s\p{P}~～]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>重新生成指令匹配（!regenerate / !regen，仅主人使用）</summary>
    private static readonly Regex RegeneratePattern = new(
        @"^\s*!regen(?:erate)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public EventDispatcher(
        BotOptions options,
        OneBotClient client,
        ChatContext context,
        ChatEngine engine,
        Database users,
        MemoryService memory,
        CommandRouter commands,
        ToolRegistry tools,
        VisionService vision,
        ILogger<EventDispatcher> logger)
    {
        _options = options;
        _client = client;
        _context = context;
        _engine = engine;
        _users = users;
        _memory = memory;
        _commands = commands;
        _tools = tools;
        _vision = vision;
        _logger = logger;
        _globalGate = new SemaphoreSlim(Math.Max(1, options.Concurrency.MaxParallelChats),
                                        Math.Max(1, options.Concurrency.MaxParallelChats));
    }

    /// <summary>处理一条原始事件（并发安全：全局门 → 会话锁 两级调度）</summary>
    public async Task HandleAsync(OneBotEvent evt, CancellationToken ct = default)
    {
        // 1. 只处理消息事件
        if (evt.PostType != "message") return;

        // 2. 触发过滤（无副作用，在锁外执行以降低锁竞争）
        if (!TryBuildIncoming(evt, out var msg))
        {
            _logger.LogDebug("消息未通过触发过滤: uid={Uid} {Type}", evt.UserId, evt.MessageType);
            return;
        }

        // 3. 两级调度：全局并发门（限制总并发）→ 会话串行锁（同会话按序）
        var sessionLock = _sessionLocks.GetOrAdd(msg!.SessionKey, _ => new SemaphoreSlim(1, 1));
        await _globalGate.WaitAsync(ct);
        try
        {
            await sessionLock.WaitAsync(ct);
            try
            {
                // 让出/重获锁的委托：长任务（生图等 ComfyUI）执行期间释放会话锁+全局门，
                // 让同一会话的其他普通消息不被阻塞；完成后重新获取继续收尾。
                async Task YieldLocks()
                {
                    _globalGate.Release();
                    sessionLock.Release();
                }
                async Task RegainLocks()
                {
                    await _globalGate.WaitAsync(ct);
                    await sessionLock.WaitAsync(ct);
                }

                await HandleCoreAsync(msg, ct, YieldLocks, RegainLocks);
            }
            finally
            {
                sessionLock.Release();
            }
        }
        finally
        {
            _globalGate.Release();
        }
    }

    /// <summary>消息实际处理逻辑（已处于全局门 + 会话锁保护内；长任务可让出锁）</summary>
    private async Task HandleCoreAsync(IncomingMessage msg, CancellationToken ct,
                                       Func<Task> yieldLocks, Func<Task> regainLocks)
    {
        // 刷新活动时钟：有消息被响应，自主活动倒计时重置（没人理超过设定时长才会触发）
        ActivityClock.Touch();

        _logger.LogInformation("[{Session}] {Name}({Uid}): {Text}",
            msg.SessionKey, msg.UserName ?? "?", msg.UserId, msg.PlainText);

        // 记录用户档案（昵称/活跃时间）
        _users.TouchUser(msg.UserId, msg.UserName);

        // 重新生成指令（仅主人）：!regenerate / !regen —— 删除上轮静静回复+触发消息，按触发消息重新请求
        // （测试提示词改动对静静表现的影响）；放在命令系统之前拦截
        if (msg.IsOwner && RegeneratePattern.IsMatch(msg.PlainText))
        {
            await HandleRegenerateAsync(msg, ct, yieldLocks, regainLocks);
            return;
        }

        // 主人命令优先（前缀命令，不经过 LLM）
        if (await _commands.TryHandleAsync(msg, ct)) return;

        // 分发
        // 连通性测试：仅当整条消息就是 ping（可带空白/标点/波浪号）时回显 pong，
        // 避免正文任意位置含 "ping" 子串（英文单词、拼音等）时误触发
        if (_options.PingEcho && PingPattern.IsMatch(msg.PlainText))
        {
            await HandleEchoAsync(msg, ct);   // 连通性测试优先
        }
        else
        {
            await HandleChatAsync(msg, ct, yieldLocks, regainLocks);   // 正常对话
        }
    }

    /// <summary>
    /// 重新生成：删除该会话上轮静静回复（最后一条 assistant）及其触发消息（上一条 user），
    /// 用触发消息重新走完整对话流程。用于测试提示词改动对静静表现的影响。
    /// </summary>
    private async Task HandleRegenerateAsync(IncomingMessage msg, CancellationToken ct,
                                             Func<Task> yieldLocks, Func<Task> regainLocks)
    {
        var recent = _users.LoadRecentMessagesWithId(msg.SessionKey, 5);   // 新→旧
        // 找：最近一条 assistant（静静上轮回复）+ 它前面的 user（触发消息）
        int asstIdx = -1, userIdx = -1;
        for (int i = 0; i < recent.Count; i++)
        {
            if (recent[i].Role == "assistant") { asstIdx = i; break; }
        }
        if (asstIdx >= 0 && asstIdx + 1 < recent.Count && recent[asstIdx + 1].Role == "user")
            userIdx = asstIdx + 1;

        if (asstIdx < 0 || userIdx < 0)
        {
            await ReplyAsync(msg, "没有可重新生成的内容（需要上一轮静静回复 + 触发消息）。", ct, atUser: true);
            return;
        }

        var trigger = recent[userIdx];
        _users.DeleteMessageById(recent[asstIdx].Id);   // 删静静上轮回复
        _users.DeleteMessageById(trigger.Id);           // 删触发消息（将重新落库）

        _logger.LogInformation("重新生成：删除上轮回复[{B}]与触发消息[{A}]，重新请求（session={Session}）",
            recent[asstIdx].Id, trigger.Id, msg.SessionKey);
        await ReplyAsync(msg, "正在重新生成～", ct, atUser: true);

        // 构造虚拟触发消息（用原触发消息的内容与说话人，走完整对话流程）
        var virtualMsg = msg with
        {
            PlainText = trigger.Content,
            UserId = trigger.UserId ?? msg.UserId,
            UserName = _users.GetUserNickname(trigger.UserId ?? msg.UserId) ?? msg.UserName,
            MessageId = msg.MessageId,
            Segments = new JsonArray(),
            QuoteId = 0,
            ImageUrls = null
        };
        await HandleChatAsync(virtualMsg, ct, yieldLocks, regainLocks);
    }

    /// <summary>
    /// 构造格式纠正提示：把上一条不合格的输出原文内联回显，明确告诉 LLM 要保留的内容，
    /// 让它"只改格式、不重新思考"——否则打回时它看不到自己刚说了什么，只能重想一遍，
    /// 结果偏向重新生成 cot 而偏离刚才的话题。
    /// </summary>
    private static string BuildCorrectionMessage(string content)
    {
        var excerpt = string.IsNullOrWhiteSpace(content)
            ? "(空)"
            : content.Length > 800 ? content[..800] + "…" : content;
        return "你刚才的输出格式不符合要求。以下是你刚才输出的正文（内容本身没问题，只是格式不对）：\n\n" +
               excerpt + "\n\n" +
               "请不要重新思考、不要改变想说的内容——只需把这段话重新整理成正确格式：" +
               "思考结束后先输出标记 ```END_REASONING```，标记之后只输出一个 JSON 对象 " +
               "{\"reply\":\"你要说的话\",\"more\":true或false}，不要输出任何多余文字、markdown 代码块或解释。请重新输出。";
    }

    /// <summary>
    /// 判断内容是否为"格式垃圾"：LLM 尝试按格式输出但失败留下的残骸（半截 JSON、reply/more 键残留、
    /// END_REASONING 标记残留、markdown 代码块残留）。普通人类语言文本（人话）不算垃圾，应直接采用。
    /// </summary>
    private static bool IsFormatJunk(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return true;
        var c = content.Trim();
        return c.Contains("{\"") || c.Contains("\"reply\"") || c.Contains("\"more\"")
            || c.Contains("END_REASONING") || c.Contains("```");
    }

    /// <summary>对话流程：唤起记忆 → 组装上下文 → 调 LLM → 截取 → 发送 → 后台总结记忆</summary>
    private async Task HandleChatAsync(IncomingMessage msg, CancellationToken ct,
                                       Func<Task>? yieldLocks = null, Func<Task>? regainLocks = null)
    {
        if (string.IsNullOrWhiteSpace(msg.PlainText)) return;

        // 0. 显式"记住"指令（不经过 LLM；私聊挂人/群聊挂群；"记住XX/记一下XX/帮我记住XX" 开头）
        if (_memory.TryRememberExplicit(msg.IsPrivate ? msg.UserId : null, msg.IsPrivate ? null : msg.GroupId, msg.PlainText))
        {
            await ReplyAsync(msg, "记住了～我记在心里啦。", ct);
            return;
        }

        // 0.5 反馈闭环：用户说"不用记/记错了/别记"→ 撤销该会话最近写入的一条记忆
        if (IsMemoryCorrection(msg.PlainText) && _memory.TryUndoMemory(msg.SessionKey))
        {
            await ReplyAsync(msg, "好～这条我不记了，已经忘掉啦。", ct);
            return;
        }

        // 1. 追加用户消息（含发送者 QQ，群聊记录可区分说话人）
        _context.AppendUser(msg.SessionKey, msg.PlainText, msg.UserId);

        // 2. 组装提示词消息：全局前置（自定义 role）+ system（身份×场景+记忆+格式指令）+ 全局后置（自定义 role）
        var prompt = _options.Prompt;
        // 按 身份×场景 解析：场景 Profile（主人私聊/客人私聊/群聊主人/群聊他人）覆盖优先，未配置字段回退身份默认
        var (role, sceneExtra) = prompt.ResolveScene(msg.IsOwner, msg.IsPrivate);
        var userName = string.IsNullOrWhiteSpace(msg.UserName) ? "朋友" : msg.UserName;
        var userDesc = msg.IsOwner ? "你的主人" : "一位客人";

        // 占位符替换：身份提示词/场景/全局提示词里可用 {UserName} {UserQQ} {OwnerId} 取真实值
        string Fill(string? s) => (s ?? "")
            .Replace("{UserName}", userName)
            .Replace("{UserQQ}", msg.UserId.ToString())
            .Replace("{OwnerId}", _options.OwnerId.ToString());

        var rolePromptText = Fill(role.BuildSystemPrompt());
        var sceneExtraText = Fill(sceneExtra);
        var globalPre = Fill(prompt.GlobalPrePrompt);
        var globalPost = Fill(prompt.GlobalPostPrompt);

        var parts = new List<string>();
        parts.Add(rolePromptText);
        if (!string.IsNullOrWhiteSpace(sceneExtraText)) parts.Add(sceneExtraText);
        // 注入当前对话对象信息（让静静知道对方是谁）；称呼方式按身份区分：主人叫"主人"，客人叫"{昵称}大人"
        var addressLine = msg.IsOwner
            ? "对方是你的主人，应称呼为「主人」。"
            : $"对方是客人，称呼时应使用其真实昵称，如「{userName}大人」。";
        parts.Add($"当前与你对话的人：昵称「{userName}」（QQ {msg.UserId}），是{userDesc}。{addressLine}");
        // 记忆注入（两步定位：私聊=对方QQ/群聊=群号+说话人+提及的QQ）
        long? memGroupId = msg.IsPrivate ? null : msg.GroupId;
        var mentionedQqs = msg.IsPrivate ? null : ExtractMentionedQqs(msg.Segments);
        parts.Add(_memory.BuildMemoryInjection(msg.IsPrivate ? msg.UserId : null, memGroupId, mentionedQqs, msg.PlainText));
        // 引用消息上下文：对方引用了某条消息时，把被引用的内容（含消息 id）告诉静静，便于呼应或原样转发；
        // 被引用消息里的图片（引用图片消息场景）收集起来交给识图模式
        List<string>? quoteImageUrls = null;
        if (msg.QuoteId > 0)
        {
            var quote = await _client.GetMessageByIdAsync(msg.QuoteId, ct);
            if (quote is not null)
            {
                if (!string.IsNullOrWhiteSpace(quote.Value.Text))
                {
                    parts.Add($"对方引用了一条消息(id={msg.QuoteId})：「{quote.Value.Text}」（来自 {quote.Value.Nickname ?? quote.Value.UserId.ToString()}）。" +
                              "这是被引用的上下文，回复时可以呼应它；如果对方要求转发这条消息，请调用 send_private_message 并传 quote_id=该 id 原样转发。");
                }
                if (_options.Vision.Enabled && quote.Value.ImageUrls is { Count: > 0 })
                {
                    quoteImageUrls = quote.Value.ImageUrls;
                }
            }
        }
        // 识图模式（双模型架构）：消息带图（当前消息 + 被引用的图片消息）时，
        // 用专用识图模型看图 → 文本描述注入 system，主模型不需要支持视觉、不需要调工具
        List<string>? visionDescriptions = null;
        if (_options.Vision.Enabled)
        {
            var allUrls = new List<string>();
            if (msg.ImageUrls is not null) allUrls.AddRange(msg.ImageUrls);
            if (quoteImageUrls is not null) allUrls.AddRange(quoteImageUrls);
            if (allUrls.Count > 0)
            {
                // 带上本次消息文字：让识图模型知道用户关注什么（如"这件衣服什么颜色"）
                visionDescriptions = await _vision.DescribeImagesAsync(allUrls, msg.PlainText, ct);
                if (visionDescriptions is not null && visionDescriptions.Count > 0)
                {
                    _logger.LogInformation("识图模式：识别 {N} 张图片完成（session={Session}）", visionDescriptions.Count, msg.SessionKey);
                }
            }
        }
        // 识图描述注入：把识图模型对图片的描述告诉主模型（在主模型上下文里，静静能"看到"图片内容）
        if (visionDescriptions is not null && visionDescriptions.Count > 0)
        {
            var descText = string.Join("\n", visionDescriptions.Select((d, i) => $"图{i + 1}：{d}"));
            parts.Add($"【图片内容】用户发来了图片，以下是识图模型对图片的识别描述（你据此理解图片）：\n{descText}");
        }
        // 群聊上下文外置（仅 AutoInjectGroupHistory=false）：提示 LLM 该会话已有多少条历史记录（超过 20 显示 20+），
        // 需要时用 get_chat_history 拉取；注入模式下历史已随请求注入，不需要此提示
        if (!msg.IsPrivate && !_options.Prompt.AutoInjectGroupHistory)
        {
            var historyCount = _users.CountSessionMessages(msg.SessionKey);
            var display = historyCount > 20 ? "20+" : historyCount.ToString();
            parts.Add($"【上下文提示】当前会话的历史聊天记录未注入本次请求（共 {display} 条，最多显示 20，超过显示 20+）。" +
                      "如果回复需要依赖与对方的过往对话，请先调用 get_chat_history 工具获取后再回复。");
        }
        parts.Add(BuildFormatInstruction(prompt.ReplyExtraction.Delimiter, _options.Llm.DisableReasoning));
        var system = string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (_options.Debug) _logger.LogInformation("[DEBUG] 组装后的 system（完整）:\n{System}", system);

        var head = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(globalPre))
            head.Add(new ChatMessage(prompt.GlobalPrePromptRole, globalPre));
        head.Add(new ChatMessage("system", system));

        // 全局后置提示词单独作为 tail：追加到 messages 最底部（历史之后、生成位置之前），约束力最强
        var tail = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(globalPost))
            tail.Add(new ChatMessage(prompt.GlobalPostPromptRole, globalPost));

        // 3. Agent 循环：静静自发地多次请求 LLM；每轮中 LLM 可自主调用工具（工具结果回填后继续）
        var toolCtx = new ToolContext(msg);
        // 自动注入模式（开关开）：群历史随请求注入，不再需要按需拉取 → 移除 get_chat_history 工具；外置模式保留
        var tools = _tools.BuildToolDefinitions();
        if (_options.Prompt.AutoInjectGroupHistory)
        {
            // 注意：必须 DeepClone——JsonNode 不能有两个父节点，直接复用原数组元素构造新 JsonArray 会抛
            // "The node already has a parent"
            var kept = new JsonArray();
            foreach (var t in tools.OfType<JsonObject>())
            {
                if (t["function"]?["name"]?.GetValue<string>() != "get_chat_history")
                    kept.Add(t.DeepClone());
            }
            tools = kept;
        }
        var repliesSent = 0;
        var roundsUsed = 0;   // 已计入回复上限的发言轮数（shell 工具轮不计数）
        string? lastReplyText = null;   // 上一轮回复内容（防连发重复）
        // 群聊自动注入：被 @ 时先拉取群聊天记录（≤MaxContextMessages 条）入库，再随请求注入（旧→新）
        List<ChatMessage>? groupHistoryMsgs = null;
        if (!msg.IsPrivate && _options.Prompt.AutoInjectGroupHistory)
        {
            groupHistoryMsgs = await BuildGroupHistoryAsync(msg, ct);
            if (groupHistoryMsgs is not null)
                _logger.LogInformation("群聊自动注入聊天记录：{N} 条（session={Session}）", groupHistoryMsgs.Count, msg.SessionKey);
        }
        // 上下文策略：私聊注入完整历史（一对一连续性好）；群聊开关开=注入拉取的历史 / 开关关=外置，
        // 只带 head + 当前消息 + tail，模型需要过往对话时自己调用 get_chat_history 工具获取
        // 格式指令单独作为底部消息（贴近生成位置、约束力最强），不混在 system 里
        var formatMsg = new ChatMessage("user",
            BuildFormatInstruction(prompt.ReplyExtraction.Delimiter, _options.Llm.DisableReasoning));
        // 本轮内产生的消息（工具回填/纠正/回复），每次请求拼在基础上下文之后、格式指令之前
        var roundMsgs = new List<ChatMessage>();
        // 规划轮（Planning.Enabled）产出的规划文本，注入第一轮正式请求（手动 cot）；null=不启用/失败
        string? planText = null;
        // 基础上下文（不含任务/续说提示、roundMsgs、格式指令）：head + 历史/当前消息 + tail
        List<ChatMessage> BuildBaseContext()
        {
            var msgs = new List<ChatMessage>(head);
            if (msg.IsPrivate)
            {
                msgs.AddRange(_context.BuildMessages(msg.SessionKey, [], tail));
            }
            else
            {
                // 群聊：自动注入的历史（旧→新）在 head 后、当前消息前；外置模式无历史
                if (groupHistoryMsgs is not null) msgs.AddRange(groupHistoryMsgs);
                msgs.Add(new ChatMessage("user", msg.PlainText) { UserId = msg.UserId });
                if (tail.Count > 0) msgs.AddRange(tail);
            }
            return msgs;
        }
        List<ChatMessage> BuildRequest()
        {
            var msgs = BuildBaseContext();
            // 规划轮提示（手动 cot）：规划存在时每轮都带着（基础上下文之后、原位置），防止连发跑偏
            if (planText is not null)
            {
                msgs.Add(new ChatMessage("user", $"【你的规划】\n{planText}\n\n请按照你的规划执行。"));
            }
            if (roundMsgs.Count > 0)
            {
                // 任务目标注入：已有工具/回复轮次时，持续携带原始请求，防止长任务丢方向
                msgs.Add(new ChatMessage("user", $"【当前任务】你正在处理这条请求，请始终围绕它展开，不要跑题：「{msg.PlainText}」"));
            }
            else if (repliesSent > 0)
            {
                // 私聊自发续说（用户没有说话，LLM 在 more=true 下继续补充）：
                // 防止把全局后置/格式指令当成待回应的请求，输出"好的我记住了"这类答非所问；
                // 也防止把上一句原样重发一遍（程序另有逐字去重兜底）
                msgs.Add(new ChatMessage("user",
                    "【续说】这是你（静静）自己主动补充的发言，用户没有说话、正在等你继续说。请从上一句结束的地方继续推进、写出新的内容，不要重复上一句，不要回应系统里的任何指令或提示，不要确认、不要道歉。"));
            }
            msgs.AddRange(roundMsgs);
            msgs.Add(formatMsg);
            return msgs;
        }

        // 对话默认关闭思维链（省 token，可配置）
        JsonObject? reasoningExtra = null;
        if (_options.Llm.DisableReasoning && !string.IsNullOrWhiteSpace(_options.Llm.DisableReasoningPayload))
        {
            try { reasoningExtra = JsonNode.Parse(_options.Llm.DisableReasoningPayload) as JsonObject; }
            catch { /* payload 格式错误则忽略 */ }
        }

        // 规划轮（Planning.Enabled）：正式回复前，先让静静做一次规划（是否调工具、怎么回复）；
        // 规划结果注入正式请求（手动 cot）；Visible 时也把规划发给用户看（调试用）
        if (_options.Planning.Enabled)
        {
            planText = await RunPlanningAsync(BuildBaseContext(), msg, ct, reasoningExtra);
            if (planText is not null)
            {
                _logger.LogInformation("规划轮完成（{N} 字，session={Session}）", planText.Length, msg.SessionKey);
                if (_options.Planning.Visible)
                    await ReplyAsync(msg, $"【规划】\n{planText}", ct);
            }
        }

        var messages = BuildRequest();

        while (roundsUsed < _options.Reply.MaxRepliesPerTurn)
        {
            // 每轮请求重建：基础上下文 + 本轮消息 + 格式指令（底部）
            messages = BuildRequest();
            var result = await _engine.CompleteWithToolsAsync(messages, tools, ct, reasoningExtra);

            // 3.1 发言轮内循环：工具调用执行 + 格式校验重试（LLM 格式写错 → 带纠正提示重新请求）
            string? text = null;
            bool more = false;
            // 本发言轮是否已成功生图：图已发出后，LLM 收尾回复不再强求格式（避免重试导致重复生图）
            bool imageSent = false;
            // 本发言轮是否执行过 shell 命令/脚本：执行后收尾回复放宽格式，且该轮不计入回复上限
            bool shellUsed = false;
            for (int fmt = 0; ; fmt++)
            {
                // 3.1.1 工具调用：LLM 决定调工具 → 执行 → 结果回填 → 再请求
                int toolRounds = 0;
                while (result.HasToolCalls && toolRounds < _options.Llm.MaxToolRounds)
                {
                    _logger.LogInformation("静静调用工具：{Tools}（第 {R} 轮）",
                        string.Join(", ", result.ToolCalls.Select(t => t.Name)), toolRounds + 1);

                    // 生图是长任务（提交 ComfyUI 后要等几十秒）：执行期间让出会话锁+全局门，
                    // 同一会话的其他普通消息不被阻塞；完成后重新获取锁继续收尾
                    bool longTask = result.ToolCalls.Any(t => t.Name == "generate_image");
                    if (longTask && yieldLocks is not null && regainLocks is not null)
                    {
                        _logger.LogInformation("生图长任务开始：让出会话锁，其他消息可继续处理");
                        await yieldLocks();
                    }
                    try
                    {
                        // 中间正文（边做边说）：LLM 可在发起工具调用的同时输出一句叙述（如"我查一下记录～"），
                        // 保留进上下文并实时发给用户，营造"边说边干"的 agent 体验（独立消息，不引用、不 @）
                        if (!string.IsNullOrWhiteSpace(result.Content))
                        {
                            roundMsgs.Add(new ChatMessage("assistant", result.Content) { ReasoningContent = result.ReasoningContent });
                            try { await ReplyAsync(msg, result.Content, ct, atUser: false, replyTo: false); }
                            catch (Exception ex) { _logger.LogWarning(ex, "发送中间正文失败"); }
                        }
                        // assistant 消息带 tool_calls 原样回传（含 reasoning_content，DeepSeek 要求完整回传否则 400）
                        roundMsgs.Add(new ChatMessage("assistant", null)
                        {
                            ToolCalls = result.ToolCalls.Select(tc => (JsonObject)new JsonObject
                            {
                                ["id"] = tc.Id,
                                ["type"] = "function",
                                ["function"] = new JsonObject
                                {
                                    ["name"] = tc.Name,
                                    ["arguments"] = tc.Arguments
                                }
                            }).ToList(),
                            ReasoningContent = result.ReasoningContent
                        });

                        // 执行每个工具，结果作为 tool 消息回填
                        foreach (var call in result.ToolCalls)
                        {
                            var output = await _tools.ExecuteAsync(call.Name, call.Arguments, toolCtx, ct)
                                         ?? $"工具 {call.Name} 不存在";
                            roundMsgs.Add(new ChatMessage("tool", output) { ToolCallId = call.Id });

                            // 标记生图成功（generate_image 成功返回以"已生成并发送图片"开头）
                            if (call.Name == "generate_image" && output.StartsWith("已生成并发送图片", StringComparison.Ordinal))
                            {
                                imageSent = true;
                            }
                            // 标记执行过 shell 命令/脚本（run_shell 调用即算，无论命令成败）
                            if (call.Name == "run_shell")
                            {
                                shellUsed = true;
                            }
                        }
                    }
                    finally
                    {
                        if (longTask && yieldLocks is not null && regainLocks is not null)
                        {
                            try { await regainLocks(); }
                            catch (OperationCanceledException) { /* 程序退出中，锁状态无关紧要 */ }
                            _logger.LogInformation("生图完成：重新获取会话锁，继续收尾");
                        }
                    }

                    messages = BuildRequest();
                    result = await _engine.CompleteWithToolsAsync(messages, tools, ct, reasoningExtra);
                    toolRounds++;
                }

                // 3.1.2 格式校验：截取 cot 后必须解析出合法的 {reply, more}
                var content = ReplyExtractor.Extract(new ChatResult(result.Content, result.ReasoningContent),
                    _options.Prompt.ReplyExtraction);
                if (TryParseReplyRound(content, out var t, out var m) && !string.IsNullOrWhiteSpace(t))
                {
                    text = t;
                    more = m;
                    break;   // 格式合格，本发言轮通过
                }

                // 工具执行轮（生图已成功 或 执行过 shell）：结果已产生，收尾文字不再强求格式——宽松提取正文即可，不做重试
                if (imageSent || shellUsed)
                {
                    text = ExtractLooseReply(content);
                    more = false;
                    break;
                }

                // 普通文本（非格式垃圾）：LLM 只是没按 JSON 格式写，但内容就是它想说的话——直接采用，不打回重试
                // （打回只会诱使它重新生成 cot，导致偏离刚才的话题）
                if (!IsFormatJunk(content))
                {
                    text = ExtractLooseReply(content);
                    more = false;
                    break;
                }

                // 格式垃圾（半截 JSON / reply/more 键残留 / 标记残留 / markdown 残骸）：本次回复作废，带纠正提示重新请求
                if (fmt >= _options.Reply.MaxFormatRetries)
                {
                    _logger.LogWarning("LLM 回复格式连续 {N} 次不合格，放弃本发言轮（session={Session}）",
                        _options.Reply.MaxFormatRetries + 1, msg.SessionKey);
                    text = null;
                    break;
                }
                _logger.LogWarning("LLM 回复格式不合格（第 {N} 次重试），已带纠正提示重新请求（session={Session}）",
                    fmt + 1, msg.SessionKey);
                // 纠正提示去重：连续失败时移除旧的同内容纠正消息，保证 roundMsgs 里只有一条。
                // 纠正消息现在内联了原文，按前缀匹配（旧版静态纠正提示同样以该前缀开头，兼容清除）
                for (int i = roundMsgs.Count - 1; i >= 0; i--)
                {
                    if (roundMsgs[i].Role == "user"
                        && roundMsgs[i].Content?.StartsWith("你刚才的输出格式不符合要求。", StringComparison.Ordinal) == true)
                    {
                        roundMsgs.RemoveAt(i);
                        break;
                    }
                }
                roundMsgs.Add(new ChatMessage("user", BuildCorrectionMessage(content)));
                messages = BuildRequest();
                result = await _engine.CompleteWithToolsAsync(messages, tools, ct, reasoningExtra);
            }

            // 3.2 本发言轮结果处理
            if (string.IsNullOrWhiteSpace(text))
            {
                // 生图已成功且无收尾文字：图就是回复，静默结束
                if (imageSent) break;
                if (repliesSent == 0)
                {
                    _logger.LogWarning("LLM 未返回有效回复（session={Session}），已回复兜底提示", msg.SessionKey);
                    await ReplyAsync(msg, "啊，我刚才走神了没想好怎么回……能再说一遍吗？", ct, atUser: true);
                }
                break;
            }

            // 防重复连发：LLM 多轮输出与上一轮逐字相同 → 丢弃本轮并终止连发（避免刷屏一模一样的消息）
            if (repliesSent > 0 && text == lastReplyText)
            {
                _logger.LogWarning("LLM 连发内容与上一轮完全相同，已丢弃并终止连发（session={Session}）", msg.SessionKey);
                break;
            }
            lastReplyText = text;

            // 追加本轮回复到上下文（落库供 get_chat_history/!history 读取）
            _context.AppendAssistant(msg.SessionKey, text);
            // 群聊上下文外置：自己刚说的话不进库注入，需加入 roundMsgs 供下一轮续说；
            // 私聊历史会从库加载（含刚落库的回复），roundMsgs 只保留工具回填消息即可
            if (!msg.IsPrivate) roundMsgs.Add(new ChatMessage("assistant", text));

            // 立即发送本条（第一条用"引用回复"引用触发消息；群聊只第一条 @ 用户）
            await ReplyAsync(msg, text, ct, atUser: repliesSent == 0, replyTo: repliesSent == 0);
            repliesSent++;

            // 回复上限计数：shell 工具执行轮不占名额（汇报执行结果不算"多轮回复"）
            if (!shellUsed) roundsUsed++;

            // LLM 说完了就停；否则自发地继续下一轮请求
            if (!more) break;
            if (_options.Reply.IntervalMs > 0)
            {
                await Task.Delay(_options.Reply.IntervalMs, ct);
            }
        }

        // 4. 后台总结长期记忆（不阻塞回复；私聊挂人/群聊挂群；信息密度门控：寒暄/短消息不触发）
        if (repliesSent > 0 && _memory.ShouldSummarize(msg.PlainText))
        {
            long? uid = msg.IsPrivate ? msg.UserId : null;
            long? gid = msg.IsPrivate ? null : msg.GroupId;
            var combined = string.Join("\n", messages.Where(m => m.Role == "assistant").Select(m => m.Content));
            _ = Task.Run(async () => await _memory.SummarizeAsync(
                uid, gid, msg.SessionKey, [("user", msg.PlainText), ("assistant", combined)], ct), ct);
        }
    }

    /// <summary>提取消息中 @ 的 QQ 号（群聊记忆定位"提及他人"用）</summary>
    private static long[] ExtractMentionedQqs(JsonArray? segments)
    {
        if (segments is null) return [];
        var result = new List<long>();
        foreach (var seg in segments.OfType<JsonObject>())
        {
            if (seg["type"]?.GetValue<string>() != "at") continue;
            var data = seg["data"] as JsonObject;
            if (data is null) continue;
            if (data["qq"] is not JsonValue qv) continue;
            if (qv.TryGetValue<long>(out var ql)) result.Add(ql);
            else if (qv.TryGetValue<string>(out var qs) && long.TryParse(qs, out var q2)) result.Add(q2);
        }
        return result.Distinct().ToArray();
    }

    /// <summary>记忆纠正信号识别：用户明确表示"不用记/记错了/别记/这条删掉"等（防误伤日常"记住"指令）</summary>
    private static bool IsMemoryCorrection(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(text,
            @"(不用记|别记|记错了|不要记|这条?别记|删掉.*记忆|取消.*记住|白记|记了也没用)");
    }

    /// <summary>群聊上下文外置：head + 当前用户消息 + tail（不注入历史，模型按需调 get_chat_history）</summary>
    private static List<ChatMessage> BuildExternalizedMessages(
        IReadOnlyList<ChatMessage> head, IReadOnlyList<ChatMessage> tail, IncomingMessage msg)
    {
        var messages = new List<ChatMessage>(head);
        messages.Add(new ChatMessage("user", msg.PlainText) { UserId = msg.UserId });
        if (tail.Count > 0) messages.AddRange(tail);
        return messages;
    }

    /// <summary>
    /// 群聊自动注入（AutoInjectGroupHistory=true）：被 @ 时拉取群聊天记录（≤MaxContextMessages 条，新→旧），
    /// 逐条按 message_id 去重入库，再反转成旧→新作为对话历史消息返回（head 之后、当前消息之前）。
    /// 拉取失败返回 null（不注入）。
    /// </summary>
    private async Task<List<ChatMessage>?> BuildGroupHistoryAsync(IncomingMessage msg, CancellationToken ct)
    {
        var messages = await _client.GetGroupMsgHistoryAsync(msg.GroupId, _options.Prompt.MaxContextMessages, ct);
        if (messages is null || messages.Count == 0) return null;

        // NapCat get_group_msg_history 返回顺序不稳定（时旧→新、时新→旧），必须显式按 time 排序（旧→新）
        var ordered = messages.OfType<JsonObject>()
            .OrderBy(m => m["time"]?.GetValue<long>() ?? 0)
            .ToList();

        var list = new List<string>();
        foreach (var m in ordered)
        {
            var uid = m["user_id"]?.GetValue<long>() ?? 0;
            var msgId = m["message_id"]?.GetValue<long>() ?? 0;
            if (uid <= 0 || msgId <= 0) continue;
            var sender = m["sender"] as JsonObject;
            var name = sender?["nickname"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) name = uid.ToString();
            if (uid == msg.SelfId) name = "静静";

            var text = FormatSegments(m["message"] as JsonArray);
            if (string.IsNullOrWhiteSpace(text)) continue;

            // 覆盖对比入库：按 message_id 去重（已存在则跳过，不存在的补入）
            var role = uid == msg.SelfId ? "assistant" : "user";
            _users.InsertMessageIfAbsent(msg.SessionKey, $"group:{msg.GroupId}:{msgId}", role, text, uid == msg.SelfId ? null : uid);

            list.Add($"{name}：{text}");
        }
        if (list.Count == 0) return null;

        // 打包成单条 user 消息（不要每条一个 ChatMessage——LLM 会模仿"昵称：内容"的对话格式去回复）：
        // 明确标注这是聊天记录背景，消息间用换行 + ------- 隔开，并提示不要模仿该格式
        var sb = new System.Text.StringBuilder();
        sb.Append("【群聊天记录】以下是本群最近的聊天记录（旧→新，第一条最早、最后一条最近）。这是过往对话背景，不是对方刚对你说的话；了解背景即可，不要复述它，也不要模仿「昵称：内容」的格式回复。\n");
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0) sb.Append("\n-------\n");
            sb.Append(list[i]);
        }
        return [new ChatMessage("user", sb.ToString())];
    }

    /// <summary>把消息段数组转成纯文本（text/at/image/reply/face 等）</summary>
    private static string FormatSegments(JsonArray? segments)
    {
        if (segments is null || segments.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var seg in segments.OfType<JsonObject>())
        {
            var type = seg["type"]?.GetValue<string>();
            var data = seg["data"] as JsonObject;
            if (type == "text" && data?["text"] is JsonValue tv)
            {
                sb.Append(tv.GetValue<string>());
            }
            else if (type == "at" && data?["qq"] is JsonValue qv)
            {
                sb.Append('@').Append(qv.GetValue<string>());
            }
            else if (type == "image")
            {
                sb.Append("[图片]");
            }
            else if (type == "face")
            {
                sb.Append("[表情]");
            }
            else if (type == "reply")
            {
                sb.Append("[回复]");
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// 规划轮（Planning.Enabled）：正式回复前的一次纯文本 LLM 调用（不带 tools），
    /// 让静静规划"是否调工具、怎么回复"，结果注入正式回复请求（手动 cot）。
    /// 失败/超时返回 null（跳过规划，正常回复）。
    /// </summary>
    private async Task<string?> RunPlanningAsync(List<ChatMessage> baseContext, IncomingMessage msg,
                                                 CancellationToken ct, JsonObject? reasoningExtra)
    {
        try
        {
            var planMsgs = new List<ChatMessage>(baseContext);
            planMsgs.Add(new ChatMessage("user", BuildPlanningPrompt(BuildToolsSummary(), msg.PlainText)));
            var result = await _engine.CompleteAsync(planMsgs, ct, reasoningExtra);
            var plan = (result.Content ?? "").Trim();
            if (string.IsNullOrWhiteSpace(plan)) return null;
            if (plan.Length > _options.Planning.MaxChars)
                plan = plan[.._options.Planning.MaxChars];
            return plan;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "规划轮失败（跳过规划，正常回复）");
            return null;
        }
    }

    /// <summary>规划提示词：把可用工具列表给静静，让它规划是否需要调工具、怎么回复</summary>
    private static string BuildPlanningPrompt(string toolsSummary, string userText)
    {
        return "在正式回复前，请先做一次回复规划（这是你的内部规划，用于理清思路，用户不会直接看到）。\n" +
               $"【用户消息】{userText}\n" +
               $"【你可用的工具】\n{toolsSummary}" +
               "请规划：\n" +
               "1. 是否需要调用工具？如果需要，先调用哪些、为什么；不需要则简单说明。\n" +
               "2. 回复的要点、结构和语气（结合当前场景与你的身份）。\n" +
               "输出 3~5 行简洁规划即可。不要执行工具，不要输出正式回复内容。";
    }

    /// <summary>从工具定义中提取"名称 - 描述"摘要（规划轮提示词用）</summary>
    private string BuildToolsSummary()
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var tools = _tools.BuildToolDefinitions();
            foreach (var t in tools.OfType<JsonObject>())
            {
                var fn = t["function"] as JsonObject;
                var name = fn?["name"]?.GetValue<string>();
                var desc = fn?["description"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var shortDesc = string.IsNullOrWhiteSpace(desc) ? "" : (desc.Length > 80 ? desc[..80] + "…" : desc);
                sb.Append("- ").Append(name).Append("：").Append(shortDesc).Append('\n');
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "构建工具摘要失败");
        }
        return sb.ToString();
    }


    /// <summary>
    /// 格式指令。disableReasoning=true（配置关闭思维链）时不要求 cot 和 END_REASONING 标记——
    /// 模型已无思考过程，直接输出 JSON 正文；否则保留"先思考 → 标记 → JSON"的格式。
    /// </summary>
    private static string BuildFormatInstruction(string? delimiter, bool disableReasoning)
    {
        var mark = string.IsNullOrWhiteSpace(delimiter) ? "```END_REASONING```" : delimiter;

        // 关闭思维链：不要提 cot/标记，只要求直接输出 JSON
        var format = disableReasoning
            ? "【回复格式】直接输出一个 JSON 对象：{\"reply\":\"你要说的话\",\"more\":true或false}。" +
              "more 表示你是否还想继续补充下一句（有后续内容想接着说时为 true，说完为 false）。" +
              "不要输出任何思考过程、标记、markdown 代码块或多余文字。"
            : "【回复格式】每次回复：先输出你的思考过程（cot，仅供内部推理，用户看不到）；" +
              $"思考结束后输出标记 {mark}；标记之后只输出一个 JSON 对象：" +
              "{\"reply\":\"你要说的话\",\"more\":true或false}。" +
              "more 表示你是否还想继续补充下一句（有后续内容想接着说时为 true，说完为 false）。" +
              "标记之前不要输出任何正文，标记之后不要输出任何额外文字。";

        return format +
               "【工具调用例外】当用户请求画图、查时间、查记忆、记住信息、浏览网页、执行命令等需要调用工具时，" +
               "必须优先发起工具调用（tool_calls），不要输出上面的 JSON 和标记；" +
               "等工具执行完毕后，再按上述格式输出最终回复。" +
               "【边做边说】需要调用工具时，可以先输出一两句话向用户说明你正在做什么（如「我先查一下记录～」「这就画给你看～」），再发起工具调用；工具执行完后再总结结果。不要闷头调工具不说话。" +
               "【自主执行】判断出需要调用工具时直接调用，不要先询问用户是否同意、不要犹豫拖延——工具就是为你完成用户请求的手段，大胆使用。" +
               "【记忆工具例外】当用户明确要求你记住某事（说「记住…」「记下来」「记一下…」「帮我记住…」等）时，必须立即调用 remember 工具写入记忆，并在回复中明确反馈「记住了」；日常聊天不要主动记录，也不要为了表态调用本工具。";
    }

    /// <summary>
    /// 严格解析静静本轮回复：必须是 JSON 对象 {"reply": "...", "more": bool}。
    /// 健壮处理：容忍 JSON 前后的杂质（如 @昵称 前缀、多余反引号 ```、markdown 包裹、解释文字），
    /// 先从内容中提取最外层 JSON 对象再解析；解析失败返回 false（视为格式无效，交由上层重试）。
    /// </summary>
    private static bool TryParseReplyRound(string content, out string text, out bool more)
    {
        text = "";
        more = false;
        if (string.IsNullOrWhiteSpace(content)) return false;

        // 1) 尝试直接解析（干净输出）
        if (TryParseReplyJson(content.Trim(), out text, out more)) return true;

        // 2) 容忍杂质：从内容中抠出最外层 {...} JSON 对象再解析
        var extracted = ExtractJsonObject(content);
        return extracted is not null && TryParseReplyJson(extracted, out text, out more);
    }

    /// <summary>解析纯 JSON 对象文本，提取 reply/more；失败返回 false</summary>
    private static bool TryParseReplyJson(string json, out string text, out bool more)
    {
        text = "";
        more = false;
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(json) is System.Text.Json.Nodes.JsonObject obj
                && obj["reply"]?.GetValue<string>() is { } reply)
            {
                text = reply.Trim();
                more = obj["more"]?.GetValue<bool>() ?? false;
                return !string.IsNullOrWhiteSpace(text);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // 非 JSON → 格式无效
        }
        return false;
    }

    /// <summary>
    /// 从任意文本中提取第一个完整的最外层 JSON 对象 {...}。
    /// 正确处理字符串内的 { } 与转义，保证不会在字符串中间截断。
    /// 找不到完整对象返回 null。
    /// </summary>
    private static string? ExtractJsonObject(string s)
    {
        int start = -1;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '{') { start = i; break; }
        }
        if (start < 0) return null;

        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
            }
            else
            {
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return s[start..(i + 1)];
                }
            }
        }
        return null;
    }

    /// <summary>
    /// 宽松提取回复正文（生图成功后的收尾文字专用）：
    /// 优先用健壮解析抠出 reply；抠不出就去掉 markdown 包裹取原文。
    /// </summary>
    private static string ExtractLooseReply(string content)
    {
        if (TryParseReplyRound(content, out var text, out _)) return text;

        var raw = content.Trim();
        if (raw.StartsWith("```"))
        {
            var start = raw.IndexOf('\n');
            var end = raw.LastIndexOf("```", StringComparison.Ordinal);
            if (start > 0 && end > start) raw = raw[(start + 1)..end].Trim();
        }
        return raw;
    }

    /// <summary>
    /// 按场景发送单条回复。
    /// replyTo=true 时用 QQ"引用回复"功能引用触发的那条消息；群聊第一条 @ 用户，后续不加 @ 防刷屏。
    /// </summary>
    private async Task ReplyAsync(IncomingMessage msg, string text, CancellationToken ct,
                                  bool atUser = true, bool replyTo = true)
    {
        var segments = new List<JsonNode>();
        if (replyTo)
        {
            segments.Add(Segments.Reply((int)msg.MessageId));
        }
        if (msg.IsPrivate)
        {
            segments.Add(Segments.Text(text));
            await _client.SendPrivateMessageAsync(msg.UserId, segments, ct);
        }
        else
        {
            if (atUser) segments.Add(Segments.At(msg.UserId));
            segments.Add(Segments.Text(" " + text));
            await _client.SendGroupMessageAsync(msg.GroupId, segments, ct);
        }
    }

    /// <summary>
    /// 触发规则：
    ///  - 私聊：PrivateEnabled 且 不在黑名单（白名单为空则放行）
    ///  - 群聊：必须 @ 机器人 或 回复机器人 才触发
    /// </summary>
    private bool TryBuildIncoming(OneBotEvent evt, out IncomingMessage? msg)
    {
        msg = null;
        var trigger = _options.Trigger;

        // 黑名单
        if (trigger.BlockedUsers.Contains(evt.UserId)) return false;

        // 白名单（非空才过滤；主人永远放行）
        if (trigger.AllowedUsers.Length > 0
            && evt.UserId != _options.OwnerId
            && !trigger.AllowedUsers.Contains(evt.UserId))
            return false;

        bool isPrivate = evt.MessageType == "private";
        if (isPrivate)
        {
            if (!trigger.PrivateEnabled) return false;
            msg = new IncomingMessage(
                evt.MessageId, evt.SelfId, evt.UserId, evt.UserName ?? "?",
                0, true, GetPlainText(evt.Message), evt.Message ?? new JsonArray(),
                $"private:{evt.UserId}", evt.UserId == _options.OwnerId,
                ExtractQuoteId(evt.Message), ExtractImageUrls(evt.Message));
            return true;
        }

        // 群聊
        if (evt.GroupId == 0) return false;
        if (trigger.GroupAtOnly && !IsAtBot(evt.Message, evt.SelfId))
        {
            if (_options.Debug)
            {
                _logger.LogInformation("[DEBUG] 群聊消息未触发（无 @ 机器人）：uid={Uid} 段={Segs}",
                    evt.UserId, evt.Message?.ToJsonString());
            }
            return false;
        }

        msg = new IncomingMessage(
            evt.MessageId, evt.SelfId, evt.UserId, evt.UserName ?? "?",
            evt.GroupId, false, GetPlainText(evt.Message), evt.Message ?? new JsonArray(),
            $"group:{evt.GroupId}", evt.UserId == _options.OwnerId,
            ExtractQuoteId(evt.Message), ExtractImageUrls(evt.Message));
        return true;
    }

    /// <summary>提取消息中引用（reply）段指向的消息 id；无引用返回 0</summary>
    private static long ExtractQuoteId(JsonArray? segments)
    {
        if (segments is null) return 0;
        foreach (var seg in segments.OfType<JsonObject>())
        {
            if (seg["type"]?.GetValue<string>() != "reply") continue;
            if (seg["data"]?["id"] is JsonValue idv)
            {
                if (idv.TryGetValue<long>(out var l)) return l;
                if (idv.TryGetValue<string>(out var s) && long.TryParse(s, out var l2)) return l2;
            }
        }
        return 0;
    }

    /// <summary>提取消息中的图片直链（image 段的 url，仅 http 开头）；无图返回 null</summary>
    private static List<string>? ExtractImageUrls(JsonArray? segments)
    {
        if (segments is null) return null;
        var urls = new List<string>();
        foreach (var seg in segments.OfType<JsonObject>())
        {
            if (seg["type"]?.GetValue<string>() != "image") continue;
            var url = seg["data"]?["url"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                urls.Add(url);
        }
        return urls.Count > 0 ? urls : null;
    }

    /// <summary>提取消息纯文本（去掉 @ 段）</summary>
    private static string GetPlainText(JsonArray? segments)
    {
        if (segments is null) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var seg in segments.OfType<JsonObject>())
        {
            if (seg["type"]?.GetValue<string>() == "text")
                sb.Append(seg["data"]?["text"]?.GetValue<string>());
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// 触发条件（群聊）：仅当消息中 @ 了机器人本人才触发。
    /// 兼容 at 段 qq 为字符串或数字两种格式；群友互 @ / @全体 / 纯回复引用 均不触发。
    /// </summary>
    private static bool IsAtBot(JsonArray? segments, long selfId)
    {
        if (segments is null) return false;
        foreach (var seg in segments.OfType<JsonObject>())
        {
            if (seg["type"]?.GetValue<string>() != "at") continue;
            var data = seg["data"] as JsonObject;
            if (data is null) continue;
            if (data["qq"] is not JsonValue qv) continue;

            if (qv.TryGetValue<string>(out var qs) && qs == selfId.ToString()) return true;
            if (qv.TryGetValue<long>(out var ql) && ql == selfId) return true;
        }
        return false;
    }

    /// <summary>P1 回显：ping → pong（验证全链路）</summary>
    private async Task HandleEchoAsync(IncomingMessage msg, CancellationToken ct)
    {
        var reply = $"pong! (来自 {(msg.IsOwner ? "主人" : "客人")} {msg.UserName})";
        if (msg.IsPrivate)
            await _client.SendPrivateMessageAsync(msg.UserId, [Segments.Text(reply)], ct);
        else
            await _client.SendGroupMessageAsync(msg.GroupId,
                [Segments.At(msg.UserId), Segments.Text(" " + reply)], ct);
    }
}
