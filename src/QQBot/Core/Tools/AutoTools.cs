using System.Text.Json.Nodes;
using QQBot.Core.Memory;
using QQBot.Core.OneBot;
using QQBot.Core.Options;

namespace QQBot.Core.Tools;

/// <summary>
/// 自主活动专用工具：send_private_to_owner —— 私聊主人。
/// 只在自主活动（AutoActivity）时注册给 LLM，普通对话不可用。
/// </summary>
public sealed class SendPrivateToOwnerTool : ITool
{
    private readonly OneBotClient _client;
    private readonly long _ownerId;

    public SendPrivateToOwnerTool(OneBotClient client, BotOptions options)
    {
        _client = client;
        _ownerId = options.OwnerId;
    }

    public string Name => "send_private_to_owner";
    public string Description => "给主人发送一条私聊消息。想主动找主人说话、问好、汇报、提醒、撒娇时调用。参数 text 为消息内容。";

    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["text"] = new JsonObject { ["type"] = "string", ["description"] = "要发送给主人的消息内容" }
        },
        ["required"] = new JsonArray("text"),
        ["additionalProperties"] = false
    };

    public async Task<string> ExecuteAsync(string argsJson, ToolContext ctx, CancellationToken ct)
    {
        var args = JsonNode.Parse(argsJson) as JsonObject;
        var text = args?["text"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text)) return "内容为空，未发送";
        if (_ownerId <= 0) return "未配置主人 QQ（OwnerId），无法私聊";

        var ok = await _client.SendPrivateMessageAsync(_ownerId, [Segments.Text(text)], ct);
        return ok ? $"已私聊主人：{text}" : "私聊主人发送失败";
    }
}

/// <summary>
/// 自主活动专用工具：send_group_message —— 在指定群发言插嘴。
/// 只在自主活动时注册给 LLM，普通对话不可用（防止刷屏）。
/// </summary>
public sealed class SendGroupMessageTool : ITool
{
    private readonly OneBotClient _client;

    public SendGroupMessageTool(OneBotClient client) => _client = client;

    public string Name => "send_group_message";
    public string Description => "在某个群里发言（插嘴）。当你看到群里有值得回应的话题、或想和群友互动时调用。参数 group_id 为群号，text 为发言内容。注意公共场合要得体、简洁，不要刷屏。";

    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["group_id"] = new JsonObject { ["type"] = "integer", ["description"] = "目标群号" },
            ["text"] = new JsonObject { ["type"] = "string", ["description"] = "要发送的发言内容" }
        },
        ["required"] = new JsonArray("group_id", "text"),
        ["additionalProperties"] = false
    };

    public async Task<string> ExecuteAsync(string argsJson, ToolContext ctx, CancellationToken ct)
    {
        var args = JsonNode.Parse(argsJson) as JsonObject;
        var gid = args?["group_id"]?.GetValue<long>() ?? 0;
        var text = args?["text"]?.GetValue<string>();
        if (gid <= 0) return "群号无效";
        if (string.IsNullOrWhiteSpace(text)) return "内容为空，未发送";

        var ok = await _client.SendGroupMessageAsync(gid, [Segments.Text(text)], ct);
        return ok ? $"已在群 {gid} 发言：{text}" : $"群 {gid} 发言失败";
    }
}

/// <summary>
/// 自主活动专用工具：organize_memory —— 整理长期记忆（受控）。
/// 规则（硬性校验）：
///  - 只能删除/移动 3 星及以下（重要度 &lt; 4）的记忆
///  - 4 星、5 星记忆受保护，不可操作
///  - 禁止添加新记忆、禁止修改记忆内容
/// 只注册给自主活动，普通对话不可用。
/// </summary>
public sealed class OrganizeMemoryTool : ITool
{
    private readonly Database _db;

    public OrganizeMemoryTool(Database db) => _db = db;

    public string Name => "organize_memory";
    public string Description =>
        "整理长期记忆：删除或移动 3 星及以下（重要度低于 4 星）的记忆。规则：只能操作 1~3 星的记忆；4 星和 5 星记忆受保护不可动；禁止添加或修改记忆。" +
        "当你想清理过期/无用的记忆，或把某条记忆在用户级与通用级之间移动时调用。参数 action=delete 删除；action=move 移动（需指定 target_scope=user 或 global）。" +
        "记忆 id 可用 search_memory 查询到。";

    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["action"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("delete", "move"),
                ["description"] = "操作类型：delete=删除 / move=移动归属"
            },
            ["memory_id"] = new JsonObject { ["type"] = "integer", ["description"] = "要操作的记忆 id（用 search_memory 查询）" },
            ["target_scope"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("user", "global"),
                ["description"] = "move 时目标范围：user=用户记忆 / global=通用记忆（仅 move 需要）"
            }
        },
        ["required"] = new JsonArray("action", "memory_id"),
        ["additionalProperties"] = false
    };

    public Task<string> ExecuteAsync(string argsJson, ToolContext ctx, CancellationToken ct)
    {
        var args = JsonNode.Parse(argsJson) as JsonObject;
        var action = args?["action"]?.GetValue<string>()?.ToLowerInvariant() ?? "";
        var id = args?["memory_id"]?.GetValue<long>() ?? 0;
        var target = args?["target_scope"]?.GetValue<string>()?.ToLowerInvariant() ?? "";

        if (id <= 0) return Task.FromResult("记忆 id 无效");

        var mem = _db.GetMemoryById(id);
        if (mem is null) return Task.FromResult($"没有找到 id={id} 的记忆");

        // 硬性校验：4 星及以上受保护
        if (mem.Importance >= 4)
        {
            return Task.FromResult($"记忆 [{id}] 是 {mem.Importance} 星，受保护不可操作（只能整理 3 星及以下）。");
        }

        switch (action)
        {
            case "delete":
                _db.DeleteMemoryById(id);
                return Task.FromResult($"已删除记忆 [{id}]：{Truncate(mem.Content, 40)}");
            case "move":
                if (target is not ("user" or "global"))
                    return Task.FromResult("move 需要指定 target_scope=user 或 global");
                long? qq = target == "user" && ctx.Message.IsPrivate ? ctx.Message.UserId : null;
                _db.UpdateMemoryScope(id, target, qq);
                return Task.FromResult($"已把记忆 [{id}] 移到 {target}：{Truncate(mem.Content, 40)}");
            default:
                return Task.FromResult("action 只能是 delete 或 move");
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
