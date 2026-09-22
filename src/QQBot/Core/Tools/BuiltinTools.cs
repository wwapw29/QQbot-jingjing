using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using QQBot.Core.Chat;
using QQBot.Core.ComfyUI;
using QQBot.Core.Memory;
using QQBot.Core.OneBot;
using QQBot.Core.Options;

namespace QQBot.Core.Tools;

/// <summary>内置工具（LLM 自主调用）</summary>
public static class BuiltinTools
{
    public static IEnumerable<ITool> CreateAll(Database db, MemoryService memory, OneBotClient client,
                                               GenerateImageTool generateImage,
                                               ShellOptions shellOptions,
                                               ILogger<ShellTool> shellLogger,
                                               int maxContextMessages,
                                               QQBot.Core.Vision.VisionService vision,
                                               QQBot.Core.Options.ToolsOptions toolsOptions,
                                               ILogger<ScreenCaptureTool> screenLogger)
    {
        var tools = new List<ITool>
        {
            new GetTimeTool(),
            new ReadyToReplyTool(),   // 烧token模式专用（OwnerOnly）：只在收集阶段出现，正式回复阶段会被移除
            new RememberTool(memory),
            new SearchMemoryTool(db),
            new UpdateMemoryTool(db),   // 整理记忆：修正已有记忆的内容/重要度（OwnerOnly，不能删除）
            new GetChatHistoryTool(db, client, maxContextMessages),
            new GetGroupMembersTool(client),   // 群成员名录（群名片/昵称/QQ）——查"群里某人是哪个"用
            new SendTextTool(client),
            new GetFriendListTool(client),
            new SendPrivateMessageTool(client),
            new BrowseWebTool(),
            new ReadFileTool(shellOptions),
            new ScreenCaptureTool(vision, toolsOptions, screenLogger),   // 仅主人可用（OwnerOnly）
            generateImage
        };
        if (shellOptions.Enabled) tools.Add(new ShellTool(shellOptions, shellLogger));
        return tools;
    }
}

/// <summary>
/// ready_to_reply —— 烧token模式专用：声明"信息收集完毕，可以正式回复了"。
/// ⚠️ 她平时（普通聊天、自主活动）**没有**这个工具：只在烧token模式的收集阶段出现，
/// 进入正式回复阶段会被从工具清单里移除；且 OwnerOnly=true，客人清单里永远不出现。
/// 程序靠"是否调用了它"来划分收集阶段与正文输出——这是唯一的收敛信号。
/// </summary>
public sealed class ReadyToReplyTool : ITool
{
    public string Name => "ready_to_reply";

    public string Description =>
        "声明你的信息收集已经完成、可以开始正式回复了。当且仅当你确认手上的信息足够回复对方时调用它。" +
        "调用之后，你的**下一次输出**才会被当作正式回复发给对方；在此之前你说的任何话对方都看不到，" +
        "只是你的内部记录，所以不必在这里写正式回复内容。如果信息还不够，请继续调用其它工具去查，不要过早调用本工具。" +
        "⚠️ 想结束收集、想发言，都只能用本工具——收集阶段调用 send_text / send_private_message 会被拦截、内容当场丢弃，" +
        "所以不要用它们来\"回复\"或表达\"我说完了\"。";

    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
        ["additionalProperties"] = false
    };

    // 仅主人可用：客人不该看见这个工具（他们也不会进入烧token模式）
    public bool OwnerOnly => true;

    public Task<string> ExecuteAsync(string argsJson, ToolContext ctx, CancellationToken ct)
        => Task.FromResult("已收到：信息收集阶段结束，可以开始正式回复了。请在接下来的一条消息里给出正式回复。");
}

/// <summary>get_time —— 获取当前时间</summary>
public sealed class GetTimeTool : ITool
{
    public string Name => "get_time";
    public string Description => "获取当前日期和时间。当用户问现在几点、今天几号、星期几时直接调用，无需询问。";
    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
        ["additionalProperties"] = false
    };

    public Task<string> ExecuteAsync(string argsJson, ToolContext ctx, CancellationToken ct)
        => Task.FromResult($"当前时间：{DateTime.Now:yyyy-MM-dd dddd HH:mm}");
}

/// <summary>remember —— 写入长期记忆</summary>
public sealed class RememberTool : ITool
{
    private readonly MemoryService _memory;
    public RememberTool(MemoryService memory) => _memory = memory;

    public string Name => "remember";
    public string Description => "将用户明确要求记住的信息写入长期记忆。当用户明确要求你记住某事（说“记住…”“记下来”“记一下…”“帮我记住…”等）时，必须立即调用本工具写入记忆，并在最终回复中明确告知“记住了”；日常聊天中的普通信息不需要主动记录（有后台自动总结），更不要为了表态而调用本工具。写入前先判断归属（见 global 参数）：公开事实记通用，私人内容记私密。";

    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["content"] = new JsonObject { ["type"] = "string", ["description"] = "要记住的记忆内容" },
            ["global"] = new JsonObject { ["type"] = "boolean", ["description"] = "记成通用还是私密（默认 false）。判断标准只有一个——**这条当着别人、或在别的群里被说出来，会不会不合适？** 不会 → true（通用）：静静自己的设定、对方的身份/称呼/账号、群里对某人的固定叫法（如「29老师」指主人）这类公开事实，别人问起时你也该答得上；会 → false（私密）：个人偏好、习惯、亲密话题、情绪细节、只在某个人或某个群内发生的事。拿不准时选 false。" }
        },
        ["required"] = new JsonArray("content"),
        ["additionalProperties"] = false
    };

    public Task<string> ExecuteAsync(string argsJson, ToolContext ctx, CancellationToken ct)
    {
        var args = JsonNode.Parse(argsJson) as JsonObject;
        var content = args?["content"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(content)) return Task.FromResult("内容为空，无法记忆");
        var global = args?["global"]?.GetValue<bool>() ?? false;

        long? qqId = ctx.Message.UserId;   // 群聊也带说话人（记忆粒度=群+用户）
        long? groupId = ctx.Message.IsPrivate ? null : ctx.Message.GroupId;
        var id = _memory.AddMemory(qqId, groupId, content, global);
        var owner = global ? "通用" : (ctx.Message.IsPrivate ? "用户" : "群聊");
        return Task.FromResult(id > 0
            ? $"已记入长期记忆：{content}（{owner}）"
            : "记忆写入失败");
    }
}

/// <summary>search_memory —— 检索长期记忆</summary>
public sealed class SearchMemoryTool : ITool
{
    private readonly Database _db;
    public SearchMemoryTool(Database db) => _db = db;

    public string Name => "search_memory";
    public string Description =>
        "查询 / 盘点你的长期记忆。两种用法：" +
        "① 想回忆「记过什么关于某人、某事」→ 传关键词检索；" +
        "② 想盘点「我到底记住了什么、有没有记漏 / 记错 / 过时的」→ query 留空，" +
        "会列出你当前范围内的记忆清单（带编号 #id、重要度、分类）以及记忆总条数。" +
        "拿到 #id 后可以用 update_memory 修正；发现该记而没记的，用 remember 补上。";

    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["query"] = new JsonObject { ["type"] = "string", ["description"] = "检索关键词/话题；留空 = 盘点模式（列出你范围内的记忆清单）" },
            ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "最多返回几条（默认 12；盘点时可给 30~50，上限 100）" }
        },
        ["additionalProperties"] = false
    };

    public Task<string> ExecuteAsync(string argsJson, ToolContext ctx, CancellationToken ct)
    {
        var args = JsonNode.Parse(argsJson) as JsonObject;
        var query = args?["query"]?.GetValue<string>() ?? "";
        var limit = Math.Clamp(args?["limit"]?.GetValue<int>() ?? 12, 1, 100);
        var inventory = string.IsNullOrWhiteSpace(query) || query.Trim() == "*";

        // ⚠️ 群聊也必须带说话人 QQ：记忆粒度是「群 + 用户」，写入（RememberTool）与自动注入
        // （MemoryService.BuildMemoryInjection → GetContextMemories）都一直这么传；
        // 这里若在群聊时置空，会导致 user 记忆在群里**全部查不到**
        // （实测：群里只看到 11 条 = global 9 + 群层面 2，私聊 30 条）。
        long? qqId = ctx.Message.UserId;
        long? gid = ctx.Message.IsPrivate ? null : ctx.Message.GroupId;
        var (items, total) = _db.SearchScopedMemories(qqId, gid, inventory ? null : query, limit);

        if (items.Count == 0)
        {
            return Task.FromResult(inventory
                ? "你在这个范围内的记忆库是空的——什么都还没记住。"
                : $"没有找到与「{query}」相关的记忆（你范围内的记忆共 {total} 条）。");
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(inventory
            ? $"你范围内的记忆共 {total} 条，以下是重要度最高的 {items.Count} 条（#id 可用 update_memory 修正）："
            : $"与「{query}」相关的记忆 {items.Count} 条（你范围内的记忆共 {total} 条）：");
        foreach (var m in items)
            sb.AppendLine($"#{m.Id} ★{m.Importance} [{(string.IsNullOrWhiteSpace(m.Category) ? m.Scope : m.Category)}] {m.Content}");
        return Task.FromResult(sb.ToString().TrimEnd());
    }
}

/// <summary>
/// update_memory —— 修正一条**已有**记忆的内容 / 重要度（"整理记忆"用）。
/// 烧token模式的收尾自评轮靠它把"发现记错了 / 记漏了关键点 / 重要度标歪了"落地。
/// ⚠️ 只允许改她自己范围内的记忆（通用 / 该用户 / 该群），且**不提供删除**——
/// 删除不可逆，留给主人的 ! 命令。OwnerOnly=true。
/// </summary>
public sealed class UpdateMemoryTool : ITool
{
    private readonly Database _db;
    public UpdateMemoryTool(Database db) => _db = db;

    public string Name => "update_memory";

    public string Description =>
        "修正一条已有记忆的内容或重要度（用于整理记忆：发现记错了、记漏了关键点、重要度标错了时）。" +
        "先用 search_memory 盘点拿到记忆编号 #id，再调用本工具。它不能新增（用 remember）、也不能删除。" +
        "只有在确实发现记忆有误 / 缺失 / 过时时才调用，不要为了显得认真而修改。";

    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["memory_id"] = new JsonObject { ["type"] = "integer", ["description"] = "要修正的记忆编号（search_memory 返回的 #id）" },
            ["content"] = new JsonObject { ["type"] = "string", ["description"] = "修正后的完整内容（不改就留空）" },
            ["importance"] = new JsonObject { ["type"] = "integer", ["description"] = "修正后的重要度 1~5（不改就留空）" }
        },
        ["required"] = new JsonArray("memory_id"),
        ["additionalProperties"] = false
    };

    public bool OwnerOnly => true;

    public Task<string> ExecuteAsync(string argsJson, ToolContext ctx, CancellationToken ct)
    {
        var args = JsonNode.Parse(argsJson) as JsonObject;
        var id = args?["memory_id"]?.GetValue<long>() ?? 0;
        if (id <= 0) return Task.FromResult("需要给出 memory_id（先用 search_memory 拿到 #编号）。");

        var rec = _db.GetMemoryById(id);
        if (rec is null) return Task.FromResult($"没有找到编号 #{id} 的记忆。");

        // 范围校验：与检索范围一致（通用 / 该用户 / 该群里归属该用户的），防止改到别人或别的群的记忆
        long? qq = ctx.Message.UserId;
        long? gid = ctx.Message.IsPrivate ? null : ctx.Message.GroupId;
        var inScope = rec.Scope == "global"
                      || (rec.Scope == "user" && rec.QqId == qq)
                      || (rec.Scope == "group" && rec.GroupId == gid && (!rec.QqId.HasValue || rec.QqId == qq));
        if (!inScope) return Task.FromResult($"编号 #{id} 的记忆不在你当前的记忆范围内，不能修改。");

        var newContent = args?["content"]?.GetValue<string>();
        int? newImp = args?["importance"]?.GetValue<int>();
        if (string.IsNullOrWhiteSpace(newContent) && newImp is null)
            return Task.FromResult("没给出要改的字段（content / importance 至少给一个）。");

        var content = string.IsNullOrWhiteSpace(newContent) ? rec.Content : newContent.Trim();
        var imp = Math.Clamp(newImp ?? rec.Importance, 1, 5);
        var ok = _db.UpdateMemoryFull(id, content, rec.Trigger, imp, rec.Scope, rec.QqId, rec.GroupId);
        return Task.FromResult(ok
            ? $"已更新 #{id}：★{rec.Importance} → ★{imp}｜{content}"
            : $"更新 #{id} 失败（记忆可能已不存在）。");
    }
}

/// <summary>
/// get_group_members —— 群成员名录（群名片 + QQ 昵称 + QQ 号）。
/// 补的是这个缺口：她的历史/记忆都是**文字**，遇到"群里某人是哪个"——
/// 尤其主人直接报出一个群名片的名字时——她没有别的地方可查，只能靠聊天记录猜（实测因此认错过人）。
/// 注意：群成员的**私称/外号**（如「29老师」）不在名录里，那种只能靠聊天记录或记忆。
/// </summary>
public sealed class GetGroupMembersTool : ITool
{
    private readonly OneBotClient _client;
    public GetGroupMembersTool(OneBotClient client) => _client = client;

    public string Name => "get_group_members";

    public string Description =>
        "查询当前群的成员名录：每个人的 **群名片**、**QQ 昵称**和 **QQ 号**都在这里（群里一般用群名片称呼人）。" +
        "当群里出现你不认识的人名、或者要确认「某个名字对应哪个 QQ、这个人在不在群里」时，**用它核对，不要靠聊天记录猜**。" +
        "可以按关键词筛选（如只查名字里带「狐」的）。" +
        "注意：私下的外号/绰号（如「29老师」）通常不在名录里——那属于群里约定俗成的叫法，只能靠聊天记录或记忆；" +
        "但「这个群里都有谁、谁叫什么、群名片是什么」只有名录能答。";

    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["query"] = new JsonObject { ["type"] = "string", ["description"] = "按关键词筛选：群名片 / QQ 昵称 / QQ 号 里包含它就算命中；留空 = 全部成员" },
            ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "最多返回几个（默认 50，上限 200）" }
        },
        ["additionalProperties"] = false
    };

    public async Task<string> ExecuteAsync(string argsJson, ToolContext ctx, CancellationToken ct)
    {
        JsonObject? args = null;
        try { args = JsonNode.Parse(argsJson) as JsonObject; } catch { /* 参数坏了就当空 */ }
        var query = (args?["query"]?.GetValue<string>() ?? "").Trim();
        var limit = Math.Clamp(args?["limit"]?.GetValue<int>() ?? 50, 1, 200);

        if (ctx.Message.IsPrivate)
            return "当前是私聊、没有群上下文 —— 本工具在这里用不了，**不要再调用它**。" +
                   "要查某个群的名录，请在群里问我；或者先用 get_chat_history 看群里的聊天记录。";

        var members = await _client.GetGroupMemberProfilesAsync(ctx.Message.GroupId, ct);
        if (members.Count == 0) return "没能取到这个群的成员名录（接口失败或机器人权限不足）。";

        var hit = members
            .Where(m => query.Length == 0
                        || m.Card.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || m.Nick.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || m.Qq.ToString().Contains(query, StringComparison.Ordinal))
            .Take(limit)
            .ToList();

        if (hit.Count == 0)
            return $"这个群共 {members.Count} 人，但没有任何人的群名片 / QQ 昵称 / QQ 号包含「{query}」。" +
                   "（如果你找的是外号、绰号，名录里查不到，得靠聊天记录或记忆。）";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(query.Length == 0
            ? $"群 {ctx.Message.GroupId} 共 {members.Count} 人（格式：群名片 ｜ QQ昵称 ｜ QQ号）："
            : $"匹配「{query}」的成员 {hit.Count} 人（格式：群名片 ｜ QQ昵称 ｜ QQ号）：");
        foreach (var m in hit)
        {
            var card = string.IsNullOrWhiteSpace(m.Card) ? "（未设群名片）" : m.Card;
            var nick = string.IsNullOrWhiteSpace(m.Nick) ? "（无昵称）" : m.Nick;
            sb.AppendLine($"· {card} ｜ {nick} ｜ {m.Qq}");
        }
        sb.Append("（群名片是群里称呼用的名字；同一人的 QQ 昵称常常完全不同，别把两者当成两个人。）");
        return sb.ToString();
    }
}

/// <summary>send_text —— 发送一条独立回复（多条回复的工具方式）</summary>
public sealed class SendTextTool : ITool
{
    private readonly OneBotClient _client;
    public SendTextTool(OneBotClient client) => _client = client;
    public string Name => "send_text";
    public string Description => "向用户额外发送一条独立消息。当需要分开发送多条内容（先发主回复，再补充/追问/提醒）时自主调用直接发送，无需询问用户。";

    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["text"] = new JsonObject { ["type"] = "string", ["description"] = "要发送的消息文本" }
        },
        ["required"] = new JsonArray("text"),
        ["additionalProperties"] = false
    };

    public async Task<string> ExecuteAsync(string argsJson, ToolContext ctx, CancellationToken ct)
    {
        var args = JsonNode.Parse(argsJson) as JsonObject;
        var text = args?["text"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text)) return "内容为空";

        if (ctx.Message.IsPrivate)
            await _client.SendPrivateMessageAsync(ctx.Message.UserId, [Segments.Text(text)], ct);
        else
            await _client.SendGroupMessageAsync(ctx.Message.GroupId,
                [Segments.At(ctx.Message.UserId), Segments.Text(" " + text)], ct);
        return "已发送";
    }
}

/// <summary>
/// get_chat_history —— 获取当前会话的最近聊天记录（上下文按需拉取）。
/// 群聊：调 NapCat get_group_msg_history 拉**群里完整记录**（含其他人之间的话），
///       拉到的消息先按 message_id 去重入库（覆盖对比），再返回；
///       拉取数量上限受 MaxContextMessages 限制。
/// 私聊：从本地库读取（两人对话已完整）。
/// </summary>
public sealed class GetChatHistoryTool : ITool
{
    private readonly Database _db;
    private readonly OneBotClient _client;
    private readonly int _maxCount;   // 拉取数量上限（取 appsettings 的 MaxContextMessages）
    public GetChatHistoryTool(Database db, OneBotClient client, int maxCount)
    {
        _db = db;
        _client = client;
        _maxCount = Math.Max(1, maxCount);
    }

    public string Name => "get_chat_history";
    public string Description => "获取当前会话的最近聊天记录（上下文）。当需要回忆与对方的过往对话、当前问题依赖之前聊过的内容（如“刚才说到哪了”“再说一遍刚才那个”“接着之前的话题”）、或需要了解话题背景时调用；如果当前消息可以独立理解就直接回复，不需要调用。";

    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),   // 无参数：条数固定用配置上限（MaxContextMessages），不由 LLM 决定
        ["additionalProperties"] = false
    };

    public async Task<string> ExecuteAsync(string argsJson, ToolContext ctx, CancellationToken ct)
    {
        // 条数以配置为准（MaxContextMessages），忽略 LLM 传入的条数参数——避免与面板设置不符
        var count = _maxCount;

        // 群聊：优先从 QQ 拉完整群记录（含所有人消息），拉到的先入库（去重覆盖），失败回退本地库
        if (!ctx.Message.IsPrivate)
        {
            var fromQq = await LoadFromQqAsync(ctx.Message.GroupId, ctx.Message.SessionKey, ctx.Message.SelfId, count, ct);
            if (fromQq is not null) return fromQq;
        }

        var msgs = _db.LoadRecentMessagesDesc(ctx.Message.SessionKey, count);
        if (msgs.Count == 0) return "（该会话还没有聊天记录）";

        // 新→旧：最新一条在最前，一眼可见最近对话
        var sb = new System.Text.StringBuilder($"以下是最近 {msgs.Count} 条聊天记录（新→旧，第一条为最近）：\n");
        foreach (var m in msgs)
        {
            var who = m.Role == "assistant" ? "静静" : (m.UserId.HasValue ? _db.GetUserNickname(m.UserId.Value) ?? m.UserId.Value.ToString() : "对方");
            sb.Append(who).Append("：").Append(m.Content ?? "").Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>从 NapCat 拉群历史，先按 message_id 去重入库（覆盖对比），再格式化返回（新→旧）</summary>
    private async Task<string?> LoadFromQqAsync(long groupId, string sessionKey, long selfId, int count, CancellationToken ct)
    {
        var messages = await _client.GetGroupMsgHistoryAsync(groupId, count, ct);
        if (messages is null || messages.Count == 0) return null;

        // NapCat 返回顺序不稳定，显式按 time 降序（新→旧，最新在最前）
        var ordered = messages.OfType<JsonObject>()
            .OrderByDescending(m => m["time"]?.GetValue<long>() ?? 0)
            .ToList();

        var sb = new System.Text.StringBuilder($"以下是群里最近 {ordered.Count} 条聊天记录（新→旧，第一条为最近，来自 QQ 实时记录）：\n");
        foreach (var msg in ordered)
        {
            var sender = msg["sender"] as JsonObject;
            var name = sender?["nickname"]?.GetValue<string>();
            var uid = msg["user_id"]?.GetValue<long>() ?? 0;
            var msgId = msg["message_id"]?.GetValue<long>() ?? 0;
            if (string.IsNullOrWhiteSpace(name)) name = uid.ToString();
            if (uid == selfId) name = "静静";  // 机器人自己

            var text = FormatSegments(msg["message"] as JsonArray);
            if (string.IsNullOrWhiteSpace(text)) continue;

            // 覆盖对比：拉到的群消息按 message_id 去重写入本地库（已存在则跳过，不存在的补入）
            var role = uid == selfId ? "assistant" : "user";
            var msgKey = $"group:{groupId}:{msgId}";
            _db.InsertMessageIfAbsent(sessionKey, msgKey, role, text, uid == selfId ? null : uid);

            sb.Append(name).Append("：").Append(text).Append('\n');
        }
        return sb.ToString();
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
}

/// <summary>
/// get_friend_list —— 获取静静的好友列表（可私聊的对象）。
/// 当静静想主动私聊某人、需要确认能跟谁私聊时调用。
/// </summary>
public sealed class GetFriendListTool : ITool
{
    private readonly OneBotClient _client;
    public GetFriendListTool(OneBotClient client) => _client = client;

    public string Name => "get_friend_list";
    public string Description => "获取静静的好友列表（可私聊的对象）。当想主动私聊某人、需要确认能跟谁私聊时调用，返回每个好友的昵称和 QQ 号。";

    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
        ["additionalProperties"] = false
    };

    public async Task<string> ExecuteAsync(string argsJson, ToolContext ctx, CancellationToken ct)
    {
        var friends = await _client.GetFriendListAsync(ct);
        if (friends.Count == 0) return "（静静还没有好友，或好友列表获取失败）";
        return "好友列表（共 " + friends.Count + " 人，均为可私聊对象）：\n"
               + string.Join("\n", friends.Select(f => $"- {f.Nickname ?? f.UserId.ToString()}（{f.UserId}）"));
    }
}

/// <summary>
/// send_private_message —— 给指定 QQ 号发送私聊消息（带好友检查）。
/// 发送前自动查询好友列表：对方不是静静好友则拒绝并说明，避免乱发。
/// </summary>
public sealed class SendPrivateMessageTool : ITool
{
    private readonly OneBotClient _client;
    public SendPrivateMessageTool(OneBotClient client) => _client = client;

    public string Name => "send_private_message";
    public string Description => "给指定的 QQ 号发送一条私聊消息。当需要主动私聊某个特定的人（向某位好友问好、通知、询问、汇报、转发消息）时调用。发送前会自动检查对方是否为静静的好友，不是好友会返回错误提示，不会发送。若需要转发某条消息（含图片/表情等所有内容），传 quote_id（该消息的 id），程序会用 QQ 的转发功能原样转发，无需手打内容。";

    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["qq"] = new JsonObject { ["type"] = "integer", ["description"] = "接收者的 QQ 号" },
            ["text"] = new JsonObject { ["type"] = "string", ["description"] = "要发送的消息内容（quote_id 存在时忽略）" },
            ["quote_id"] = new JsonObject { ["type"] = "integer", ["description"] = "可选：要原样转发的被引用消息 id（如对方刚引用的消息）" }
        },
        ["required"] = new JsonArray("qq"),
        ["additionalProperties"] = false
    };

    public async Task<string> ExecuteAsync(string argsJson, ToolContext ctx, CancellationToken ct)
    {
        var args = JsonNode.Parse(argsJson) as JsonObject;
        var qq = args?["qq"]?.GetValue<long>() ?? 0;
        var text = args?["text"]?.GetValue<string>();
        var quoteId = args?["quote_id"]?.GetValue<long>() ?? 0;
        if (qq <= 0) return "参数错误：需要 qq（接收者QQ号）和 text（消息内容）";

        // 好友检查：不是好友则拒绝并返回错误提示
        var friends = await _client.GetFriendListAsync(ct);
        if (friends.All(f => f.UserId != qq))
        {
            return $"发送失败：{qq} 不是静静的好友，不能发起私聊。可先调用 get_friend_list 查看能私聊的好友，或请主人先添加对方为好友。";
        }

        // 原样转发：quote_id 存在时用 QQ 转发功能转发整条消息（图片/表情等所有内容保真）
        if (quoteId > 0)
        {
            var fwd = await _client.ForwardFriendSingleMessageAsync(qq, quoteId, ct);
            return fwd ? $"已把消息 {quoteId} 原样转发给 {qq}（含图片/表情等所有内容）。" : $"转发失败：无法转发消息 {quoteId}（可能消息已过期、不存在或对方设置限制）。";
        }
        if (string.IsNullOrWhiteSpace(text)) return "参数错误：text 为空且未提供 quote_id";

        var ok = await _client.SendPrivateMessageAsync(qq, [Segments.Text(text)], ct);
        return ok ? $"已向 {qq} 发送私聊消息。" : $"发送失败：向 {qq} 发私聊消息时出错（可能对方设置了拒绝接收或网络异常）。";
    }
}
