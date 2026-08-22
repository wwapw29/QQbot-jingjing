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
    private readonly Microsoft.Extensions.Configuration.IConfiguration _config;
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

    /// <summary>消息合并窗口（按会话）：QQ「转发+留言」等连续消息合并成整体回复；0 秒=关闭</summary>
    private readonly ConcurrentDictionary<string, MergeWindow> _mergeWindows = new();

    /// <summary>
    /// 会话忙时的排队消息（按会话）：LLM 回复生成期间到达的触发消息并入队列，
    /// 当前回复完成后合并成一条统一处理——回复延迟结束前多次触发只回一条。
    /// </summary>
    private readonly ConcurrentDictionary<string, List<IncomingMessage>> _pendingAfterBusy = new();

    /// <summary>单个会话的合并缓冲（Buffer 需在 lock 内访问；Timer 到期触发 flush）</summary>
    private sealed class MergeWindow
    {
        public List<IncomingMessage> Buffer { get; } = new();
        public CancellationTokenSource? Timer { get; set; }
    }

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
        Microsoft.Extensions.Configuration.IConfiguration config,
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
        _config = config;
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

    /// <summary>调试模式：运行时读配置（面板热更新立即生效，无需重启）</summary>
    private bool DebugMode => string.Equals(_config["Bot:Debug"], "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>处理一条原始事件（并发安全：全局门 → 会话锁 两级调度）</summary>
    public async Task HandleAsync(OneBotEvent evt, CancellationToken ct = default)
    {
        // 1. 只处理消息事件
        if (evt.PostType != "message") return;

        // 2. 触发过滤（无副作用，在锁外执行以降低锁竞争）
        if (!TryBuildIncoming(evt, out var msg))
        {
            // 2.5 群聊未 @ 的消息：若该会话正处于合并窗口（转发+留言场景的第二条留言无 @），并入缓冲
            if (TryEnqueuePassive(evt, ct)) return;
            _logger.LogDebug("消息未通过触发过滤: uid={Uid} {Type}", evt.UserId, evt.MessageType);
            return;
        }

        // 2.6 消息合并窗口（Trigger.MergeSeconds>0 时）：触发后等待窗口，把连续消息合并成整体回复；
        //     窗口内再次 @ 机器人 → 立即触发已收集的批次，本消息开启新窗口（拆分为两次回复）
        if (_options.Trigger.MergeSeconds > 0 && await TryMergeAsync(msg, ct)) return;

        // 3. 两级调度：全局并发门（限制总并发）→ 会话串行锁（同会话按序）
        await ProcessImmediatelyAsync(msg, ct);
    }

    /// <summary>直接走两级调度处理一条消息（合并窗口到期/拆分时的统一出口）</summary>
    private async Task ProcessImmediatelyAsync(IncomingMessage msg, CancellationToken ct)
    {
        var sessionLock = _sessionLocks.GetOrAdd(msg.SessionKey, _ => new SemaphoreSlim(1, 1));
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
            FlushPendingIfIdle(msg.SessionKey, ct);
        }
    }

    /// <summary>会话空闲后：把回复期间排队（忙时合并）的触发消息作为一条整体回复处理</summary>
    private void FlushPendingIfIdle(string sessionKey, CancellationToken ct)
    {
        if (!_pendingAfterBusy.TryRemove(sessionKey, out var pending) || pending.Count == 0) return;
        _logger.LogInformation("会话空闲：合并处理回复期间排队的 {N} 条消息（{Session}）", pending.Count, sessionKey);
        _ = Task.Run(async () =>
        {
            try { await FlushMergeAsync(pending, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "排队批次处理失败（{Session}）", sessionKey); }
        }, ct);
    }

    /// <summary>
    /// 合并窗口判定（Trigger.MergeSeconds>0 时由 HandleAsync 调用）。
    /// 返回 true = 消息已进入合并缓冲（等待窗口到期整体回复），或已触发拆分（不再直接处理）。
    /// 窗口内再次 @ 机器人 = 新一轮召唤 → 已收集的批次立即触发，本消息开启新窗口。
    /// </summary>
    private async Task<bool> TryMergeAsync(IncomingMessage msg, CancellationToken ct)
    {
        // 主人命令（! 前缀）不合并，立即处理
        if (msg.PlainText.TrimStart().StartsWith(_options.Command.Prefix)) return false;

        // 会话忙（当前正在生成回复）：后续触发消息（含 @、触发词）并入排队队列，不再单独触发——
        // 回复完成后合并成一条处理，保证"回复延迟结束前多次触发=一条回复"
        if (_sessionLocks.TryGetValue(msg.SessionKey, out var busyLock) && busyLock.CurrentCount == 0)
        {
            var pending = _pendingAfterBusy.GetOrAdd(msg.SessionKey, _ => new List<IncomingMessage>());
            lock (pending) pending.Add(msg);
            _logger.LogDebug("会话忙，触发消息排队（{Session}）：{Text}", msg.SessionKey, msg.PlainText[..Math.Min(msg.PlainText.Length, 30)]);
            return true;
        }

        var window = _mergeWindows.GetOrAdd(msg.SessionKey, _ => new MergeWindow());
        bool split = false;
        lock (window)
        {
            if (window.Buffer.Count > 0 && !msg.IsPrivate && IsAtBot(msg.Segments, msg.SelfId))
            {
                split = true;   // 群聊窗口内再次 @：拆分
            }
            else
            {
                window.Buffer.Add(msg);
                if (window.Buffer.Count == 1) StartMergeTimer(window, msg.SessionKey, ct);
                return true;
            }
        }

        // 拆分：立即触发已收集的批次，本消息开启新窗口
        var batch = TakeMergeBatch(window);
        StartMergeTimer(window, msg.SessionKey, ct);
        lock (window) window.Buffer.Add(msg);
        _logger.LogInformation("合并窗口拆分（再次 @）：{Session} 先触发已收集 {N} 条消息", msg.SessionKey, batch.Count);
        _ = FlushMergeAsync(batch, ct);
        return true;
    }

    /// <summary>群聊未触发消息（无 @）并入活跃窗口缓冲；返回 true = 已并入（转发+留言的第二条留言）</summary>
    private bool TryEnqueuePassive(OneBotEvent evt, CancellationToken ct)
    {
        if (evt.MessageType != "group" || evt.GroupId == 0) return false;
        // 自动上文注入开启时：群聊触发后由 get_group_msg_history 拉取上文（窗口延迟 n 秒期间到达的
        // 留言会被拉取到），若再并入缓冲会造成留言内容重复注入——此时直接丢弃，靠注入覆盖
        if (_options.Prompt.AutoInjectGroupHistory) return false;
        var sessionKey = $"group:{evt.GroupId}";
        if (!_mergeWindows.TryGetValue(sessionKey, out var window)) return false;
        lock (window)
        {
            if (window.Buffer.Count == 0 || window.Timer is null) return false;
            var msg = new IncomingMessage(evt.MessageId, evt.SelfId, evt.UserId, evt.UserName ?? "?",
                evt.GroupId, false, GetPlainText(evt.Message), evt.Message ?? new JsonArray(),
                sessionKey, evt.UserId == _options.OwnerId,
                ExtractQuoteId(evt.Message), ExtractImageUrls(evt.Message));
            window.Buffer.Add(msg);
        }
        _logger.LogDebug("合并窗口并入未触发消息（{Session}）", sessionKey);
        return true;
    }

    /// <summary>启动（或重置）会话的合并窗口定时器；到期后取出缓冲整体触发</summary>
    private void StartMergeTimer(MergeWindow window, string sessionKey, CancellationToken ct)
    {
        var seconds = Math.Max(1, _options.Trigger.MergeSeconds);
        CancellationTokenSource timer;
        lock (window)
        {
            window.Timer?.Cancel();
            window.Timer = timer = new CancellationTokenSource();
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(seconds * 1000, timer.Token);
                var batch = TakeMergeBatch(window);
                if (batch.Count > 0) await FlushMergeAsync(batch, ct);
                else _logger.LogDebug("合并窗口到期但无消息（{Session}）", sessionKey);
            }
            catch (OperationCanceledException)
            {
                // 定时器被取消（新窗口/退出）：正常退出
            }
            catch (Exception ex)
            {
                // 防止未观察异常被静默吞掉（曾导致"工具调用后没下文"且无任何日志）
                _logger.LogError(ex, "合并窗口处理异常（{Session}）", sessionKey);
            }
        });
    }

    /// <summary>取出缓冲批次并取消定时器（线程安全）</summary>
    private static List<IncomingMessage> TakeMergeBatch(MergeWindow window)
    {
        lock (window)
        {
            var batch = window.Buffer.ToList();
            window.Buffer.Clear();
            window.Timer?.Cancel();
            return batch;
        }
    }

    /// <summary>把合并批次作为一条整体消息触发回复</summary>
    private async Task FlushMergeAsync(List<IncomingMessage> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        var merged = MergeBatch(batch);
        if (batch.Count > 1)
        {
            _logger.LogInformation("合并窗口到期：{Session} 合并 {N} 条消息为一个整体回复", merged.SessionKey, batch.Count);
        }
        await ProcessImmediatelyAsync(merged, ct);
    }

    /// <summary>把一批消息合并成一条（段按顺序拼接，文本按顺序连接，引用/发送者取第一条）</summary>
    private static IncomingMessage MergeBatch(List<IncomingMessage> batch)
    {
        var first = batch[0];
        if (batch.Count == 1) return first;
        var segs = new JsonArray();
        var texts = new List<string>();
        var images = new List<string>();
        foreach (var m in batch)
        {
            if (m.Segments is not null)
            {
                foreach (var s in m.Segments.OfType<JsonObject>())
                {
                    segs.Add(s.DeepClone());
                }
            }
            if (!string.IsNullOrWhiteSpace(m.PlainText)) texts.Add(m.PlainText);
            if (m.ImageUrls is not null) images.AddRange(m.ImageUrls);
        }
        return first with
        {
            PlainText = string.Join("\n", texts),
            Segments = segs,
            ImageUrls = images.Count > 0 ? images : null,
        };
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
               "思考结束后先输出标记 ```END_REASONING```，标记之后只输出一个 JSON 数组，" +
               "数组的每一项代表一条要发送的消息：[{\"reply\":\"第一句\"},{\"reply\":\"第二句\"}]，" +
               "需要调用工具时在对应项加 tool_calls 字段。不要输出任何多余文字、markdown 代码块或解释。请重新输出。";
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
        // 0. 聊天记录分享（合并转发）解析：当前消息或被引用消息里含 forward 段时，
        //    递归展开为「此消息为聊天记录分享，内容：...」的一般消息格式文本；
        //    纯转发消息（无文字）也能借此进入对话流程
        string? forwardText = null;
        if (HasForwardSegment(msg.Segments) || msg.QuoteId > 0)
        {
            forwardText = await BuildForwardTextAsync(msg, ct);
        }
        if (string.IsNullOrWhiteSpace(msg.PlainText) && forwardText is null) return;

        // 0. 消息级持久去重（修复：NapCat 重连重放的消息超过 30s 去重窗口后会被重复处理/重复回复/重复写记忆/重复落库）：
        // 落库即占用 msg_key（INSERT OR IGNORE）——已存在（重放）则跳过整个处理流程。必须放在一切副作用（记住/记忆/回复）之前
        var contentForHistory = forwardText is null
            ? msg.PlainText
            : (string.IsNullOrWhiteSpace(msg.PlainText) ? forwardText : msg.PlainText + "\n\n" + forwardText);
        var msgKey = (msg.IsPrivate ? $"private:{msg.UserId}" : $"group:{msg.GroupId}") + $":{msg.MessageId}";
        if (!_context.InsertUserIfAbsent(msg.SessionKey, msgKey, contentForHistory, msg.UserId))
        {
            _logger.LogInformation("消息已处理过（msgKey={MsgKey}），跳过重放（session={Session}）", msgKey, msg.SessionKey);
            return;
        }

        // 0. 显式"记住"指令（不经过 LLM；私聊挂人/群聊挂"群+说话人"；"记住XX/记一下XX/帮我记住XX" 开头）
        if (_memory.TryRememberExplicit(msg.UserId, msg.IsPrivate ? null : msg.GroupId, msg.PlainText))
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

        // 1. 用户消息已由去重插入落库（上一步 msgKey 占用即写入）；后续上下文加载直接读库即可
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
        // 记忆注入（两步定位：私聊=对方QQ/群聊=群号+说话人+提及的QQ；群聊也带说话人 uid → 支持"群+用户"记忆粒度）
        long? memGroupId = msg.IsPrivate ? null : msg.GroupId;
        var mentionedQqs = msg.IsPrivate ? null : ExtractMentionedQqs(msg.Segments);
        parts.Add(_memory.BuildMemoryInjection(msg.UserId, memGroupId, mentionedQqs, msg.PlainText));
        // 上一轮规划延续：把该会话最近一次规划注入，让静静记得上次的互动方向（如"欲擒故纵"先拒后应），
        // 下次对话可在其基础上延续张力；无规划或已过时则忽略重新规划
        var lastPlan = _context.GetLastPlanning(msg.SessionKey);
        if (!string.IsNullOrWhiteSpace(lastPlan))
        {
            parts.Add($"【你上一轮的规划】{lastPlan}（这是你上次回复前的内部规划。如需延续上次的互动方向可以参考它；若已过时或场景不同，请忽略并重新规划。）");
        }
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
        // 主模型嵌入式识图（UseMainModel=true）：图片直接嵌入对话请求，不转描述——
        // 主模型为 DeepSeek 时优先走 Files API（上传获取 file_id，24h 复用，不占请求体）；失败回退 base64 内联
        List<string>? embeddedImages = null;
        List<string>? embeddedFileIds = null;
        var visionUseMain = string.Equals(_config["Bot:Vision:UseMainModel"], "true", StringComparison.OrdinalIgnoreCase);
        if (_options.Vision.Enabled)
        {
            var allUrls = new List<string>();
            if (msg.ImageUrls is not null) allUrls.AddRange(msg.ImageUrls);
            if (quoteImageUrls is not null) allUrls.AddRange(quoteImageUrls);
            if (allUrls.Count > 0)
            {
                if (visionUseMain)
                {
                    // DeepSeek：先尝试 Files API 上传（file_id 引用），上传失败的图回退 base64 内联
                    var inlineUrls = new List<string>();
                    if (_vision.IsDeepSeekMainModel())
                    {
                        var (fids, failedUrls) = await _vision.UploadImagesToFilesAsync(allUrls, ct);
                        if (fids.Count > 0) embeddedFileIds = fids;
                        inlineUrls.AddRange(failedUrls);
                    }
                    else
                    {
                        inlineUrls.AddRange(allUrls);
                    }
                    if (inlineUrls.Count > 0)
                        embeddedImages = await _vision.DownloadImagesDataUrlAsync(inlineUrls, ct);
                }
                else
                {
                    // 带上本次消息文字：让识图模型知道用户关注什么（如"这件衣服什么颜色"）
                    visionDescriptions = await _vision.DescribeImagesAsync(allUrls, msg.PlainText, ct);
                    if (visionDescriptions is not null && visionDescriptions.Count > 0)
                    {
                        _logger.LogInformation("识图模式：识别 {N} 张图片完成（session={Session}）", visionDescriptions.Count, msg.SessionKey);
                    }
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
        // 格式指令不再加入 system/head——head 会被规划轮复用（RunPlanningAsync 用 BuildBaseContext），
        // 会导致规划轮也带"输出 JSON"要求而频繁输出回复草稿；格式指令只保留在正式请求底部
        //（formatMsg，BuildRequest 里贴近生成位置，约束力更强）
        // parts.Add(BuildFormatInstruction(prompt.ReplyExtraction.Delimiter, _options.Llm.DisableReasoning));
        var system = string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (DebugMode) _logger.LogInformation("[DEBUG] 组装后的 system（完整）:\n{System}", system);

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
        // 客人（非主人）对话：仅提供 GuestAllowed 白名单内的工具（主人始终全量）
        var tools = _tools.BuildToolDefinitions(forGuest: !msg.IsOwner);
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
        var sentTexts = new HashSet<string>();   // 本次触发已发送的所有文本（防跨轮重复刷屏：只拦逐字相同的）
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
            BuildFormatInstruction(prompt.ReplyExtraction.Delimiter, _options.Llm.DisableReasoning, _options.Reply.MaxRepliesPerTurn));
        // 本轮内产生的消息（工具回填/纠正/回复），每次请求拼在基础上下文之后、格式指令之前
        var roundMsgs = new List<ChatMessage>();
        // 规划轮（Planning.Enabled）产出的规划文本，注入第一轮正式请求（手动 cot）；null=不启用/失败
        string? planText = null;
        // 基础上下文（不含任务/续说提示、roundMsgs、格式指令）：head + 历史/当前消息 + tail
        // includeTail=false 时排除全局后置提示词（GlobalPostPrompt）——规划轮专用：
        // 后置是回复风格/格式约束，规划阶段不需要，避免干扰规划输出
        List<ChatMessage> BuildBaseContext(bool includeTail = true)
        {
            var msgs = new List<ChatMessage>(head);
            if (msg.IsPrivate)
            {
                msgs.AddRange(_context.BuildMessages(msg.SessionKey, [], includeTail ? tail : null));
            }
            else
            {
                // 群聊：自动注入的历史（旧→新）在 head 后、当前消息前；外置模式无历史
                if (groupHistoryMsgs is not null) msgs.AddRange(groupHistoryMsgs);
                msgs.Add(new ChatMessage("user", msg.PlainText) { UserId = msg.UserId });
                if (includeTail && tail.Count > 0) msgs.AddRange(tail);
            }
            // 主模型嵌入式识图：图片作为独立 user 消息（仅图块）嵌入——多模态内容只能出现在 user 消息；
            // DeepSeek Files API 的 file_id 块优先，其余 base64 image_url 块
            if ((embeddedImages is { Count: > 0 } || embeddedFileIds is { Count: > 0 }))
                msgs.Add(new ChatMessage("user", null) { ImageDataUrls = embeddedImages, FileIds = embeddedFileIds });
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
            planText = await RunPlanningAsync(BuildBaseContext(includeTail: false), msg, ct, reasoningExtra);
            if (planText is not null)
            {
                // 保存本轮规划：下次对话注入上下文，延续互动方向（欲擒故纵等张力跨对话保持）
                _context.SavePlanning(msg.SessionKey, planText);
                _logger.LogInformation("规划轮完成（{N} 字，session={Session}）", planText.Length, msg.SessionKey);
                if (_options.Planning.Visible)
                {
                    await ReplyAsync(msg, $"【规划】\n{planText}", ct);
                }
                // [2026-08-13 主人要求注释] 规划轮回复草稿识别：LLM 经常在规划轮输出回复草稿（JSON），
                // 当作过渡句发出来感官不好——现不再发送（规划静默，正式回复照常）；如需"边说边干"可恢复此分支
                // else if (TryParseReplyRound(planText, out var transition, out _) && !string.IsNullOrWhiteSpace(transition))
                // {
                //     _logger.LogInformation("规划轮输出了回复草稿，作为过渡句先发送（{Session}）", msg.SessionKey);
                //     try { await ReplyAsync(msg, transition, ct, atUser: false, replyTo: false); }
                //     catch (Exception ex) { _logger.LogWarning(ex, "发送规划过渡句失败"); }
                // }
            }
        }

        var messages = BuildRequest();

        // 3.1 多轮回复循环（数组格式）：LLM 每轮输出「回复数组」，每项 = 一条要发送的消息，
        // 项可内嵌 tool_calls（该条发起工具调用）。程序逐条发送（间隔 Reply.IntervalMs），
        // 数组内发起工具调用 → 执行 → 结果回填 → 继续下一轮，直到本轮无工具调用或达轮次上限。
        int toolRounds = 0;   // 工具后继续轮次（上限 Llm.MaxToolRounds）
        while (true)
        {
            // 每轮请求重建：基础上下文 + 本轮消息 + 格式指令（底部）
            messages = BuildRequest();
            var result = await _engine.CompleteWithToolsAsync(messages, tools, ct, reasoningExtra);

            // 本发言轮是否已成功生图：图已发出后，LLM 收尾回复不再强求格式（避免重试导致重复生图）
            bool imageSent = false;
            // 本发言轮是否执行过 shell 命令/脚本：执行后收尾回复放宽格式
            bool shellUsed = false;
            // 本轮是否执行过工具（决定是否继续下一轮）
            bool hadToolCalls = false;
            int fmt = 0;   // 格式重试计数
            bool finished = false;   // 本轮已产出（发送/兜底），跳出重试循环

            // 逐条发送一条消息（数组项通用：落库 + 群聊 roundMsgs + 引用/at + 间隔）
            async Task SendItemAsync(string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                // 防重复（跨轮）：工具执行后的下一轮，LLM 经常把上一轮的话原样重发一遍——
                // 用"本次触发已发送集合"去重（只拦逐字相同的刷屏，不影响有意强调的新内容）
                if (!sentTexts.Add(text))
                {
                    _logger.LogWarning("LLM 回复与之前已发送的内容逐字相同，已跳过（session={Session}）", msg.SessionKey);
                    return;
                }
                _context.AppendAssistant(msg.SessionKey, text);
                if (!msg.IsPrivate) roundMsgs.Add(new ChatMessage("assistant", text));
                await ReplyAsync(msg, text, ct, atUser: repliesSent == 0, replyTo: repliesSent == 0);
                repliesSent++;
                if (_options.Reply.IntervalMs > 0)
                    await Task.Delay(_options.Reply.IntervalMs, ct);
            }

            while (!finished)
            {
                // 3.1.1 解析回复数组 → 逐条发送 + 执行内嵌工具调用
                var content = ReplyExtractor.Extract(new ChatResult(result.Content, result.ReasoningContent),
                    _options.Prompt.ReplyExtraction);
                if (TryParseReplyArray(content, out var items))
                {
                    foreach (var item in items)
                    {
                        // 该条附带工具调用：先发话（若有），再执行工具
                        if (item.ToolCalls is { Count: > 0 })
                        {
                            hadToolCalls = true;
                            _logger.LogInformation("静静调用工具：{Tools}（第 {R} 轮）",
                                string.Join(", ", item.ToolCalls.Select(t => t.Name)), toolRounds + 1);

                            // 生图是长任务（提交 ComfyUI 后要等几十秒）：执行期间让出会话锁+全局门，
                            // 同一会话的其他普通消息不被阻塞；完成后重新获取锁继续收尾
                            bool longTask = item.ToolCalls.Any(t => t.Name == "generate_image");
                            if (longTask && yieldLocks is not null && regainLocks is not null)
                            {
                                _logger.LogInformation("生图长任务开始：让出会话锁，其他消息可继续处理");
                                await yieldLocks();
                            }
                            try
                            {
                                // 先发话（边说边干）
                                if (!string.IsNullOrWhiteSpace(item.Reply))
                                    await SendItemAsync(item.Reply);

                                // assistant 消息带 tool_calls 原样回传（含 reasoning_content，DeepSeek 要求完整回传否则 400）
                                roundMsgs.Add(new ChatMessage("assistant", item.Reply)
                                {
                                    ToolCalls = item.ToolCalls.Select(tc => (JsonObject)new JsonObject
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
                                foreach (var call in item.ToolCalls)
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
                        }
                        else
                        {
                            // 普通消息：逐条发送（1 秒间隔）
                            await SendItemAsync(item.Reply ?? "");
                        }
                    }
                    finished = true;
                    break;
                }

                // 3.1.2 宽松兜底（生图已成功 / 执行过 shell / 普通文本）：收尾文字不强制数组格式
                if (imageSent || shellUsed)
                {
                    await SendItemAsync(ExtractLooseReply(content));
                    finished = true;
                    break;
                }
                if (!IsFormatJunk(content))
                {
                    await SendItemAsync(ExtractLooseReply(content));
                    finished = true;
                    break;
                }

                // 3.1.3 格式垃圾（半截 JSON / 非数组结构 / 标记残留）：带纠正提示重新请求
                if (fmt >= _options.Reply.MaxFormatRetries)
                {
                    _logger.LogWarning("LLM 回复格式连续 {N} 次不合格，放弃本发言轮（session={Session}）",
                        _options.Reply.MaxFormatRetries + 1, msg.SessionKey);
                    finished = true;
                    break;
                }
                _logger.LogWarning("LLM 回复格式不合格（第 {N} 次重试），已带纠正提示重新请求（session={Session}）",
                    fmt + 1, msg.SessionKey);
                // 纠正提示去重：连续失败时移除旧的同内容纠正消息
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
                fmt++;
            }

            // 3.2 本轮结束：执行过工具且未达上限 → 继续下一轮（LLM 汇报结果）；否则结束
            if (hadToolCalls && toolRounds < _options.Llm.MaxToolRounds)
            {
                toolRounds++;
                continue;
            }
            break;
        }

        // 4. 后台总结长期记忆（不阻塞回复；私聊挂人/群聊挂群；信息密度门控：寒暄/短消息不触发）
        if (repliesSent > 0 && _memory.ShouldSummarize(msg.PlainText))
        {
            // 群聊也带说话人 uid：总结出的记忆归属"群+用户"，不再是全群混合
            long? uid = msg.UserId;
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

            // 名字带 QQ 号 + 发送时间（如"黎问明（274120497）18:40：…"）：同一人可能昵称/群昵称不同，
            // LLM 按 QQ 号关联身份，不会再把不同昵称当成不同的人；时间帮助 LLM 理解对话先后/时序；自己（静静）不标 QQ
            var ts = m["time"]?.GetValue<long>() ?? 0;
            var timeStr = "";
            if (ts > 0)
            {
                var dt = DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime();
                timeStr = dt.Date == DateTime.Today ? dt.ToString("HH:mm") : dt.ToString("MM-dd HH:mm");
            }
            list.Add(uid == msg.SelfId ? $"{name} {timeStr}：{text}" : $"{name}（{uid}）{timeStr}：{text}");
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
            planMsgs.Add(new ChatMessage("user", BuildPlanningPrompt(BuildToolsSummary(forGuest: !msg.IsOwner), msg.PlainText)));
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

    /// <summary>规划提示词：把可用工具列表给静静，让它规划是否需要调工具、怎么回复。
    /// 只包含规划要求，不包含任何正式回复的格式要求（避免 LLM 在规划轮输出回复草稿 JSON）。
    /// 模板可配置（Bot.Prompt.PlanningPrompt，{Tools}/{UserText} 占位，面板提示词页可编辑，运行时读即时生效）。</summary>
    private string BuildPlanningPrompt(string toolsSummary, string userText)
    {
        var template = _config["Bot:Prompt:PlanningPrompt"];
        if (string.IsNullOrWhiteSpace(template))
        {
            template = DefaultPlanningPrompt;
        }
        return template.Replace("{Tools}", toolsSummary).Replace("{UserText}", userText);
    }

    /// <summary>内置默认规划轮提示词模板</summary>
    private const string DefaultPlanningPrompt =
        "在正式回复前，请先做一次回复规划（这是你的内部规划，用于理清思路，用户不会直接看到）。\n" +
        "【用户消息】{UserText}\n" +
        "【你可用的工具】\n{Tools}" +
        "请规划：\n" +
        "1. 是否需要调用工具？如果需要，先调用哪些、为什么；不需要则简单说明。\n" +
        "2. 回复的要点、结构和语气（结合当前场景与你的身份）。\n" +
        "输出 3~5 行简洁的普通文字规划即可。注意：这是内部规划，不要输出正式回复内容，" +
        "不要输出 JSON、代码块或其他任何结构化格式标记，直接用普通文字写规划。";

    /// <summary>从工具定义中提取"名称 - 描述"摘要（规划轮提示词用）</summary>
    private string BuildToolsSummary(bool forGuest = false)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var tools = _tools.BuildToolDefinitions(forGuest: forGuest);
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
    private static string BuildFormatInstruction(string? delimiter, bool disableReasoning, int maxItems)
    {
        var mark = string.IsNullOrWhiteSpace(delimiter) ? "```END_REASONING```" : delimiter;

        // 多轮数组格式：每项=一条要发送的消息；最多 maxItems 项；项可内嵌 tool_calls
        var arrayRule = $"【回复格式】直接输出一个 JSON 数组，数组的每一项代表一条要发送的消息（程序会逐条以 1 秒间隔发送）：\n" +
            "[{\"reply\":\"第一句\"},{\"reply\":\"第二句\",\"tool_calls\":[{\"type\":\"function\",\"function\":{\"name\":\"get_time\",\"arguments\":\"{}\"}}]},{\"reply\":\"第三句\"}]\n" +
            $"规则：1. 每项必须有 reply 字段（该条消息内容）；2. 数组最多 {maxItems} 项，想说几句就写几项（想接着说就多写几项，说完就写一项即可）；" +
            "3. 需要调用工具时，在对应项加 tool_calls 字段（格式如上方示例）；4. 不调用工具时省略 tool_calls；" +
            "5. 只输出 JSON 数组本身。";

        // 关闭思维链：不要提 cot/标记，只要求直接输出 JSON
        var format = disableReasoning
            ? arrayRule + "不要输出任何思考过程、标记、markdown 代码块或多余文字。"
            : "【回复格式】每次回复：先输出你的思考过程（cot，仅供内部推理，用户看不到）；" +
              $"思考结束后输出标记 {mark}；标记之后只输出一个 JSON 数组（格式如下）：\n" +
              arrayRule +
              "标记之前不要输出任何正文，标记之后不要输出任何额外文字。";

        return format +
               "【防重复】工具执行完成后继续回复时，绝对不要重复自己已经发送过的任何一句话（包括上一轮和更早的话）。" +
               "如果想说的话已经说过了，就只对工具结果做一句简短收尾，或者直接结束回复（输出只含空 reply 项的数组或一项收尾即可）。" +
               "【工具调用例外】当用户请求画图、查时间、查记忆、记住信息、浏览网页、执行命令等需要调用工具时，" +
               "必须在数组对应项的 tool_calls 里发起工具调用（tool_calls），不要编造结果；" +
               "等工具执行完毕后，再按上述格式输出最终回复（数组）。" +
               "【边做边说】需要调用工具时，可以在发起工具调用的那一项里输出一句话向用户说明你正在做什么（如「我先查一下记录～」「这就画给你看～」），再发起工具调用；工具执行完后再总结结果。不要闷头调工具不说话。" +
               "【自主执行】判断出需要调用工具时直接调用，不要先询问用户是否同意、不要犹豫拖延——工具就是为你完成用户请求的手段，大胆使用。" +
               "【记忆工具例外】当用户明确要求你记住某事（说「记住…」「记下来」「记一下…」「帮我记住…」等）时，必须立即调用 remember 工具写入记忆，并在回复中明确反馈「记住了」；日常聊天不要主动记录，也不要为了表态调用本工具。";
    }

    /// <summary>
    /// 严格解析静静本轮回复：必须是 JSON 对象 {"reply": "...", "more": bool}。
    /// 健壮处理：容忍 JSON 前后的杂质（如 @昵称 前缀、多余反引号 ```、markdown 包裹、解释文字），
    /// 先从内容中提取最外层 JSON 对象再解析；解析失败返回 false（视为格式无效，交由上层重试）。
    /// </summary>
    private static bool TryParseReplyRound(string content, out string text, out bool more)
    {        text = "";
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

    /// <summary>多轮回复数组中的一项：reply=要发送的消息文本；ToolCalls=该条附带发起的工具调用（可空）</summary>
    private sealed record ReplyItem(string? Reply, List<ToolCall>? ToolCalls);

    /// <summary>
    /// 解析多轮回复数组（新格式）：[{"reply":"第一句"},{"reply":"第二句","tool_calls":[...]},...]。
    /// 容忍杂质（提取最外层 [...]）；每项须有 reply 字段（或 tool_calls）；空数组/解析失败返回 false。
    /// </summary>
    private static bool TryParseReplyArray(string content, out List<ReplyItem> items)
    {
        items = new List<ReplyItem>();
        if (string.IsNullOrWhiteSpace(content)) return false;
        var s = content.Trim();
        var start = s.IndexOf('[');
        var end = s.LastIndexOf(']');
        if (start < 0 || end <= start) return false;

        JsonArray? arr;
        try { arr = JsonNode.Parse(s[start..(end + 1)]) as JsonArray; }
        catch { return false; }
        if (arr is null || arr.Count == 0) return false;

        foreach (var node in arr)
        {
            try
            {
                if (node is not JsonObject obj) continue;
                var reply = obj["reply"]?.GetValue<string>()?.Trim();
                List<ToolCall>? calls = null;
                if (obj["tool_calls"] is JsonArray tcs && tcs.Count > 0)
                {
                    calls = new List<ToolCall>();
                    foreach (var tc in tcs.OfType<JsonObject>())
                    {
                        var fn = tc["function"] as JsonObject;
                        var name = fn?["name"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        // arguments 兼容两种形态：标准 JSON 字符串，或 LLM 不规范输出的对象（序列化成 JSON 字符串）
                        var argsNode = fn?["arguments"];
                        string argsStr;
                        if (argsNode is JsonValue av && av.TryGetValue<string>(out var asStr)) argsStr = asStr;
                        else if (argsNode is JsonObject aobj) argsStr = aobj.ToJsonString();
                        else argsStr = "{}";
                        calls.Add(new ToolCall(
                            tc["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
                            name,
                            argsStr));
                    }
                }
                if (string.IsNullOrWhiteSpace(reply) && (calls is null || calls.Count == 0)) continue;
                items.Add(new ReplyItem(string.IsNullOrWhiteSpace(reply) ? null : reply, calls));
            }
            catch
            {
                // 单项解析失败跳过：不让 LLM 的不规范输出中断整个回复流程
            }
        }
        return items.Count > 0;
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
    /// 优先解析数组（新格式：拼接各项 reply + 工具调用摘要）；再试旧对象格式；最后去 markdown 包裹取原文。
    /// </summary>
    private static string ExtractLooseReply(string content)
    {
        // 新格式优先：数组 → 拼接所有项
        if (TryParseReplyArray(content, out var items))
        {
            var parts = new List<string>();
            foreach (var it in items)
            {
                if (it.ToolCalls is { Count: > 0 })
                    parts.AddRange(it.ToolCalls.Select(t => $"[工具调用] {t.Name}({t.Arguments})"));
                if (!string.IsNullOrWhiteSpace(it.Reply)) parts.Add(it.Reply);
            }
            if (parts.Count > 0) return string.Join("\n", parts);
        }
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
        // 群黑名单：该群内被拉黑的 QQ 不响应（无论 @ 还是关键词触发——避免 bot 互相触发）
        if (_users.IsGroupBlacklisted(evt.GroupId, evt.UserId))
        {
            _logger.LogDebug("群黑名单命中，忽略（gid={Gid} uid={Uid}）", evt.GroupId, evt.UserId);
            return false;
        }
        if (trigger.GroupAtOnly && !IsAtBot(evt.Message, evt.SelfId))
        {
            // 关键词触发（开关开时）：未被 @ 的消息，正文（不含引用段——GetPlainText 只取 text 段）含任一触发词也触发
            if (!(trigger.GroupKeywordTrigger
                  && ContainsTriggerWord(GetPlainText(evt.Message), trigger.TriggerWords)))
            {
                if (DebugMode)
                {
                    _logger.LogInformation("[DEBUG] 群聊消息未触发（无 @ 机器人且不含触发词）：uid={Uid} 段={Segs}",
                        evt.UserId, evt.Message?.ToJsonString());
                }
                return false;
            }
        }

        msg = new IncomingMessage(
            evt.MessageId, evt.SelfId, evt.UserId, evt.UserName ?? "?",
            evt.GroupId, false, GetPlainText(evt.Message), evt.Message ?? new JsonArray(),
            $"group:{evt.GroupId}", evt.UserId == _options.OwnerId,
            ExtractQuoteId(evt.Message), ExtractImageUrls(evt.Message));
        return true;
    }

    /// <summary>消息段数组里是否含 forward（聊天记录分享）段</summary>
    private static bool HasForwardSegment(JsonArray? segments)
    {
        if (segments is null) return false;
        return segments.OfType<JsonObject>().Any(s => s["type"]?.GetValue<string>() == "forward");
    }

    /// <summary>提取 forward 段的 id（兼容字符串/数字两种格式）</summary>
    private static string? ExtractForwardId(JsonObject? data)
    {
        if (data?["id"] is not JsonValue idv) return null;
        if (idv.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) return s;
        if (idv.TryGetValue<long>(out var l)) return l.ToString();
        return null;
    }

    /// <summary>
    /// 解析当前消息与被引用消息中的合并转发（forward）段，展开为「此消息为聊天记录分享，内容：[...]」文本。
    /// 无 forward 段返回 null。嵌套转发（记录里套记录）递归展开，深度上限防死循环。
    /// </summary>
    private async Task<string?> BuildForwardTextAsync(IncomingMessage msg, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        // 当前消息里的 forward 段
        await AppendForwardTextAsync(sb, msg.Segments, ct, 0);
        // 被引用消息里的 forward 段（引用分享的聊天记录）
        if (msg.QuoteId > 0)
        {
            var quote = await _client.GetMessageByIdAsync(msg.QuoteId, ct);
            if (quote is not null && quote.Value.Segments is not null)
            {
                await AppendForwardTextAsync(sb, quote.Value.Segments, ct, 0);
            }
        }
        return sb.Length > 0 ? sb.ToString().TrimEnd() : null;
    }

    /// <summary>把段数组里的每个 forward 段展开追加到 sb（每条记录一行，一般消息格式）</summary>
    private async Task AppendForwardTextAsync(System.Text.StringBuilder sb, JsonArray? segments,
                                              CancellationToken ct, int depth)
    {
        if (segments is null || depth > 5) return;
        foreach (var seg in segments.OfType<JsonObject>())
        {
            if (seg["type"]?.GetValue<string>() != "forward") continue;
            var id = ExtractForwardId(seg["data"] as JsonObject);
            if (id is null) continue;

            var list = await _client.GetForwardMsgAsync(id, ct);
            if (list is null)
            {
                sb.Append("（聊天记录分享获取失败）\n");
                continue;
            }
            sb.Append("此消息为聊天记录分享，内容：\n");
            foreach (var (nick, uid, inner) in list)
            {
                var who = string.IsNullOrWhiteSpace(nick) ? uid.ToString() : nick;
                var text = await RenderSegmentsTextAsync(inner, ct, depth + 1);
                sb.Append($"[{who}]：{text}\n");
            }
        }
    }

    /// <summary>把单条消息的段数组渲染为一般文本（text 原文、图片/表情/at 占位、forward 递归）</summary>
    private async Task<string> RenderSegmentsTextAsync(JsonArray? segments, CancellationToken ct, int depth)
    {
        if (segments is null) return "";
        if (depth > 5) return "…（层级过深）";
        var sb = new System.Text.StringBuilder();
        foreach (var seg in segments.OfType<JsonObject>())
        {
            var type = seg["type"]?.GetValue<string>();
            var data = seg["data"] as JsonObject;
            switch (type)
            {
                case "text":
                    sb.Append(data?["text"]?.GetValue<string>());
                    break;
                case "image":
                    sb.Append("[图片]");
                    break;
                case "face":
                    sb.Append("[表情]");
                    break;
                case "at":
                    var atQq = data?["qq"]?.ToString();
                    sb.Append(atQq == "all" ? "@全体" : $"@{atQq}");
                    break;
                case "forward":
                    // 嵌套聊天记录分享：递归展开（记录里套记录）
                    var fwdId = ExtractForwardId(data);
                    if (string.IsNullOrWhiteSpace(fwdId))
                    {
                        sb.Append("[聊天记录]");
                    }
                    else
                    {
                        var inner = await _client.GetForwardMsgAsync(fwdId, ct);
                        if (inner is null)
                        {
                            sb.Append("[聊天记录]");
                        }
                        else
                        {
                            sb.Append("\n[嵌套聊天记录分享]\n");
                            foreach (var (nick, uid, innerSegs) in inner)
                            {
                                var who = string.IsNullOrWhiteSpace(nick) ? uid.ToString() : nick;
                                var t = await RenderSegmentsTextAsync(innerSegs, ct, depth + 1);
                                sb.Append($"  [{who}]：{t}\n");
                            }
                        }
                    }
                    break;
                default:
                    // 其他段类型（json/record/video 等）给占位，避免丢失上下文
                    if (!string.IsNullOrEmpty(type)) sb.Append($"[{type}]");
                    break;
            }
        }
        return sb.ToString().Trim();
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

    /// <summary>关键词触发匹配：正文含任一触发词（逗号/顿号/分号/竖线分隔，去空）即命中</summary>
    private static bool ContainsTriggerWord(string text, string wordsCsv)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(wordsCsv)) return false;
        foreach (var w in wordsCsv.Split([',', '，', '、', ';', '；', '|'], StringSplitOptions.RemoveEmptyEntries))
        {
            var word = w.Trim();
            if (word.Length > 0 && text.Contains(word, StringComparison.Ordinal)) return true;
        }
        return false;
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
