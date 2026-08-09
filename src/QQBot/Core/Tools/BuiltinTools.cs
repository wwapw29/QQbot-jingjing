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
                                               int maxContextMessages)
    {
        var tools = new List<ITool>
        {
            new GetTimeTool(),
            new RememberTool(memory),
            new SearchMemoryTool(db),
            new GetChatHistoryTool(db, client, maxContextMessages),
            new SendTextTool(client),
            new GetFriendListTool(client),
            new SendPrivateMessageTool(client),
            new BrowseWebTool(),
            generateImage
        };
        if (shellOptions.Enabled) tools.Add(new ShellTool(shellOptions, shellLogger));
        return tools;
    }
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
    public string Description => "将用户明确要求记住的信息写入长期记忆。当用户明确要求你记住某事（说“记住…”“记下来”“记一下…”“帮我记住…”等）时，必须立即调用本工具写入记忆，并在最终回复中明确告知“记住了”；日常聊天中的普通信息不需要主动记录（有后台自动总结），更不要为了表态而调用本工具。";

    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["content"] = new JsonObject { ["type"] = "string", ["description"] = "要记住的记忆内容" },
            ["global"] = new JsonObject { ["type"] = "boolean", ["description"] = "是否记成通用记忆（默认 false=只对该用户有效）" }
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

        long? qqId = ctx.Message.IsPrivate ? ctx.Message.UserId : null;
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
    public string Description => "检索与该用户相关的长期记忆（偏好、事件、承诺等）。当需要回想之前聊过的事、用户问“你还记得…”时自主调用直接检索，无需询问用户。";

    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["query"] = new JsonObject { ["type"] = "string", ["description"] = "检索关键词/话题" }
        },
        ["required"] = new JsonArray("query"),
        ["additionalProperties"] = false
    };

    public Task<string> ExecuteAsync(string argsJson, ToolContext ctx, CancellationToken ct)
    {
        var args = JsonNode.Parse(argsJson) as JsonObject;
        var query = args?["query"]?.GetValue<string>() ?? "";

        long? qqId = ctx.Message.IsPrivate ? ctx.Message.UserId : null;
        var all = _db.LoadMemories(qqId, 20, includeGlobal: true);
        var hits = all
            .Where(m => string.IsNullOrEmpty(query) || m.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(10)
            .ToList();
        if (hits.Count == 0) return Task.FromResult("没有找到相关记忆");
        return Task.FromResult(string.Join("\n", hits.Select(m => $"- {m.Content}(★{m.Importance})")));
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
        ["properties"] = new JsonObject
        {
            ["count"] = new JsonObject { ["type"] = "integer", ["description"] = "要获取的最近消息条数（1~上限，默认用上限值）" }
        },
        ["additionalProperties"] = false
    };

    public async Task<string> ExecuteAsync(string argsJson, ToolContext ctx, CancellationToken ct)
    {
        var args = JsonNode.Parse(argsJson) as JsonObject;
        var count = Math.Clamp(args?["count"]?.GetValue<int>() ?? _maxCount, 1, _maxCount);

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
