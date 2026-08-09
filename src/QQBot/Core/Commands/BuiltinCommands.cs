using QQBot.Core.Chat;
using QQBot.Core.Memory;
using QQBot.Core.OneBot;
using QQBot.Core.Tools;

namespace QQBot.Core.Commands;

/// <summary>命令元数据（帮助清单用）</summary>
public sealed record CommandMeta(string Name, string Description, string Usage);

/// <summary>内置命令实现（仅主人可用）</summary>
public static class BuiltinCommands
{
    /// <summary>命令目录（!help 展示顺序即此）</summary>
    public static readonly IReadOnlyList<CommandMeta> Catalog =
    [
        new("help", "列出所有可用命令", "!help"),
        new("status", "显示机器人状态统计", "!status"),
        new("memories", "查看长期记忆（本人/通用/全部）", "!memories [global|all|QQ号]"),
        new("history", "查看某人的最近聊天记录", "!history [QQ号] [条数]"),
        new("clear", "清空聊天记录/上下文", "!clear [QQ号] 或 !clear group [群号]"),
        new("wipe", "清空某人长期记忆", "!wipe [QQ号] 或 !wipe all"),
        new("remember", "添加长期记忆", "!remember [global] 内容"),
        new("mdel", "删除记忆（按 id，支持批量）", "!mdel <记忆id> 或 !mdel [id,id,id...]"),
        new("mmove", "移动记忆归属（user↔global）", "!mmove <记忆id> global|user [QQ号]"),
        new("mimp", "修改记忆重要度 1~5", "!mimp <记忆id> <1-5>"),
        new("draw", "跳过 LLM，直接用提示词调 ComfyUI 生图（所有人可用）", "!draw <提示词>"),
        new("summarize", "手动总结最近对话并沉淀长期记忆", "!summarize [QQ号] 或 !summarize group [群号]")
    ];

    public static IEnumerable<IBotCommand> CreateAll(ChatContext context, Database db,
                                                     GenerateImageTool generateImage,
                                                     MemoryService memory) =>
    [
        new HelpCommand(),
        new StatusCommand(db),
        new MemoriesCommand(db),
        new HistoryCommand(db),
        new ClearCommand(context),
        new WipeCommand(db),
        new RememberCommand(db),
        new MemoryDeleteCommand(db),
        new MemoryMoveCommand(db),
        new MemoryImportanceCommand(db),
        new DrawCommand(generateImage),
        new SummarizeCommand(memory)
    ];

    /// <summary>文本截断（命令回复显示用）</summary>
    public static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

/// <summary>!mdel <id> 或 !mdel [id,id,id...] —— 删除记忆（支持批量）</summary>
public sealed class MemoryDeleteCommand : IBotCommand
{
    private readonly Database _db;
    public MemoryDeleteCommand(Database db) => _db = db;

    public string Name => "mdel";
    public string Description => "删除记忆（按 id，支持批量）";
    public string Usage => "!mdel <记忆id> 或 !mdel [id,id,id...]";

    public Task<string> ExecuteAsync(IncomingMessage msg, string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
            return Task.FromResult("用法：!mdel <记忆id> 或 !mdel [id,id,id...]（id 见 !memories 输出）");

        // 解析 ids：支持 !mdel 12、!mdel [12,34,56]、!mdel 12,34,56、!mdel 12 34 56
        var raw = string.Join(",", args);
        raw = raw.Replace("[", "").Replace("]", "").Trim();
        var ids = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.TryParse(s, out var v) ? v : (long?)null)
            .Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (ids.Count == 0)
            return Task.FromResult("用法：!mdel <记忆id> 或 !mdel [id,id,id...]（id 见 !memories 输出）");

        var deleted = new System.Collections.Generic.List<string>();
        var missing = new System.Collections.Generic.List<long>();
        foreach (var id in ids)
        {
            var mem = _db.GetMemoryById(id);
            if (mem is null) { missing.Add(id); continue; }
            _db.DeleteMemoryById(id);
            deleted.Add($"[{id}] {BuiltinCommands.Truncate(mem.Content, 30)}");
        }

        if (deleted.Count == 0)
            return Task.FromResult($"没有找到这些 id 的记忆：{string.Join("、", missing)}");
        var sb = new System.Text.StringBuilder($"已删除 {deleted.Count} 条记忆：\n").AppendJoin('\n', deleted);
        if (missing.Count > 0) sb.Append($"\n未找到：{string.Join("、", missing)}");
        return Task.FromResult(sb.ToString());
    }
}

/// <summary>!mmove <id> global|user [QQ号] —— 移动记忆归属</summary>
public sealed class MemoryMoveCommand : IBotCommand
{
    private readonly Database _db;
    public MemoryMoveCommand(Database db) => _db = db;

    public string Name => "mmove";
    public string Description => "移动记忆归属（user↔global）";
    public string Usage => "!mmove <记忆id> global|user [QQ号]";

    public Task<string> ExecuteAsync(IncomingMessage msg, string[] args, CancellationToken ct)
    {
        if (args.Length < 2 || !long.TryParse(args[0], out var id))
            return Task.FromResult("用法：!mmove <记忆id> global|user [QQ号]");

        var mem = _db.GetMemoryById(id);
        if (mem is null) return Task.FromResult($"没有找到 id={id} 的记忆");

        var target = args[1].ToLowerInvariant();
        switch (target)
        {
            case "global":
                _db.UpdateMemoryScope(id, "global", null);
                return Task.FromResult($"已把记忆 [{id}] 移到全局：{BuiltinCommands.Truncate(mem.Content, 40)}");
            case "user":
                var qq = args.Length > 2 && long.TryParse(args[2], out var uid) ? uid : msg.UserId;
                _db.UpdateMemoryScope(id, "user", qq);
                return Task.FromResult($"已把记忆 [{id}] 移到用户 {qq}：{BuiltinCommands.Truncate(mem.Content, 40)}");
            default:
                return Task.FromResult("目标只能是 global 或 user");
        }
    }
}

/// <summary>!mimp <id> <1-5> —— 修改记忆重要度</summary>
public sealed class MemoryImportanceCommand : IBotCommand
{
    private readonly Database _db;
    public MemoryImportanceCommand(Database db) => _db = db;

    public string Name => "mimp";
    public string Description => "修改记忆重要度 1~5";
    public string Usage => "!mimp <记忆id> <1-5>";

    public Task<string> ExecuteAsync(IncomingMessage msg, string[] args, CancellationToken ct)
    {
        if (args.Length < 2 || !long.TryParse(args[0], out var id) || !int.TryParse(args[1], out var imp))
            return Task.FromResult("用法：!mimp <记忆id> <1-5>");

        var mem = _db.GetMemoryById(id);
        if (mem is null) return Task.FromResult($"没有找到 id={id} 的记忆");
        _db.UpdateMemoryImportance(id, Math.Clamp(imp, 1, 5));
        return Task.FromResult($"已把记忆 [{id}] 重要度改为 {Math.Clamp(imp, 1, 5)}：{BuiltinCommands.Truncate(mem.Content, 40)}");
    }
}

/// <summary>!help —— 列出所有命令</summary>
public sealed class HelpCommand : IBotCommand
{
    public string Name => "help";
    public string Description => "列出所有可用命令";
    public string Usage => "!help";

    public Task<string> ExecuteAsync(IncomingMessage msg, string[] args, CancellationToken ct)
    {
        var lines = new List<string> { "【静静的主人命令】" };
        foreach (var c in BuiltinCommands.Catalog)
        {
            lines.Add($"{c.Usage} —— {c.Description}");
        }
        return Task.FromResult(string.Join("\n", lines));
    }
}

/// <summary>!status —— 机器人状态</summary>
public sealed class StatusCommand : IBotCommand
{
    private readonly Database _db;
    public StatusCommand(Database db) => _db = db;

    public string Name => "status";
    public string Description => "显示机器人状态统计";
    public string Usage => "!status";

    public Task<string> ExecuteAsync(IncomingMessage msg, string[] args, CancellationToken ct)
        => Task.FromResult(
            $"【静静状态】\n会话记录：{_db.CountSessions()} 个\n消息条数：{_db.CountMessages()}\n长期记忆：{_db.CountMemories()} 条");
}

/// <summary>!memories [global|all|QQ号] —— 查看长期记忆（默认当前用户；global=通用；all=全部）</summary>
public sealed class MemoriesCommand : IBotCommand
{
    private readonly Database _db;
    public MemoriesCommand(Database db) => _db = db;

    public string Name => "memories";
    public string Description => "查看长期记忆（默认本人；global=通用；all=全部）";
    public string Usage => "!memories [global|all|QQ号]";

    public Task<string> ExecuteAsync(IncomingMessage msg, string[] args, CancellationToken ct)
    {
        var arg = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        // 分组显示辅助（带记忆 id，供 !mdel/!mmove/!mimp 精确定位）
        string RenderGroup(string title, List<MemoryRecord> list)
        {
            if (list.Count == 0) return "";
            var lines = new List<string> { title };
            foreach (var m in list)
            {
                var cat = string.IsNullOrEmpty(m.Category) ? "" : $"({m.Category})";
                lines.Add($"[{m.Id}] · {m.Content}{cat} ★{m.Importance}");
            }
            return string.Join("\n", lines);
        }

        switch (arg)
        {
            case "global":
            {
                var list = _db.LoadMemoriesByScope("global", null, 20);
                return Task.FromResult(list.Count == 0
                    ? "目前没有任何通用记忆～"
                    : RenderGroup("【通用记忆】", list));
            }
            case "all":
            {
                var globals = _db.LoadMemoriesByScope("global", null, 20);
                var users = _db.LoadMemoriesByScope("user", null, 20);
                var sections = new List<string>();
                if (globals.Count > 0) sections.Add(RenderGroup("【通用记忆】", globals));
                if (users.Count > 0) sections.Add(RenderGroup("【用户记忆】", users));
                return Task.FromResult(sections.Count > 0 ? string.Join("\n\n", sections) : "目前没有任何长期记忆～");
            }
            case "":
            {
                var list = _db.LoadMemoriesByScope("user", msg.UserId, 20);
                return Task.FromResult(list.Count == 0
                    ? $"{msg.UserId} 还没有长期记忆～"
                    : RenderGroup($"【{msg.UserId} 的记忆】", list));
            }
            default:
            {
                if (long.TryParse(args[0], out var uid))
                {
                    var list = _db.LoadMemoriesByScope("user", uid, 20);
                    return Task.FromResult(list.Count == 0
                        ? $"{uid} 还没有长期记忆～"
                        : RenderGroup($"【{uid} 的记忆】", list));
                }
                return Task.FromResult("用法：!memories [global|all|QQ号]");
            }
        }
    }
}

/// <summary>!history [QQ号|group 群号] [条数] —— 查看某人/某群的最近聊天记录（默认当前会话）</summary>
public sealed class HistoryCommand : IBotCommand
{
    private readonly Database _db;
    public HistoryCommand(Database db) => _db = db;

    public string Name => "history";
    public string Description => "查看某人/某群的最近聊天记录";
    public string Usage => "!history [QQ号] 或 !history group [群号] [条数]";

    public Task<string> ExecuteAsync(IncomingMessage msg, string[] args, CancellationToken ct)
    {
        // 目标会话解析：!history → 当前会话；!history 12345 → private:{12345}；!history group 88888 → group:{88888}
        string sessionKey;
        int count = 10;
        int argIdx = 0;

        if (args.Length >= 2 && args[0].Equals("group", StringComparison.OrdinalIgnoreCase)
                             && long.TryParse(args[1], out var gid))
        {
            sessionKey = $"group:{gid}";
            argIdx = 2;
        }
        else if (args.Length >= 1 && long.TryParse(args[0], out var uid))
        {
            sessionKey = $"private:{uid}";
            argIdx = 1;
        }
        else
        {
            sessionKey = msg.SessionKey;
        }

        if (args.Length > argIdx && int.TryParse(args[argIdx], out var n))
        {
            count = Math.Clamp(n, 1, 50);
        }

        var list = _db.LoadRecentMessages(sessionKey, count);
        if (list.Count == 0)
        {
            return Task.FromResult($"{sessionKey} 还没有聊天记录～");
        }

        // 倒序：最新消息排最上面，避免长回复把最新内容挤到看不见
        list.Reverse();

        var lines = new List<string> { $"【{sessionKey} 最近 {list.Count} 条（最新在上）】" };
        foreach (var m in list)
        {
            string who = m.Role == "assistant"
                ? "静静"
                : (m.UserId.HasValue
                    ? (_db.GetUserNickname(m.UserId.Value) ?? m.UserId.ToString())
                    : "用户");
            var content = m.Content?.Replace("\n", " ") ?? "";
            if (content.Length > 60) content = content[..60] + "…";
            lines.Add($"{who}：{content}");
        }
        return Task.FromResult(string.Join("\n", lines));
    }
}

/// <summary>!clear [qq号|group 群号] —— 清空聊天上下文（默认当前会话）</summary>
public sealed class ClearCommand : IBotCommand
{
    private readonly ChatContext _context;
    public ClearCommand(ChatContext context) => _context = context;

    public string Name => "clear";
    public string Description => "清空聊天记录/上下文";
    public string Usage => "!clear [QQ号] 或 !clear group [群号]";

    public Task<string> ExecuteAsync(IncomingMessage msg, string[] args, CancellationToken ct)
    {
        string target = args.Length switch
        {
            0 => msg.SessionKey,
            1 when long.TryParse(args[0], out var uid) => $"private:{uid}",
            2 when args[0].Equals("group", StringComparison.OrdinalIgnoreCase)
                   && long.TryParse(args[1], out var gid) => $"group:{gid}",
            _ => msg.SessionKey
        };
        _context.Clear(target);
        return Task.FromResult($"已清空 {target} 的聊天记录～");
    }
}

/// <summary>!wipe [QQ号|all] —— 清空某人记忆（默认当前用户；all=全部含通用记忆）</summary>
public sealed class WipeCommand : IBotCommand
{
    private readonly Database _db;
    public WipeCommand(Database db) => _db = db;

    public string Name => "wipe";
    public string Description => "清空某人长期记忆";
    public string Usage => "!wipe [QQ号] 或 !wipe all";

    public Task<string> ExecuteAsync(IncomingMessage msg, string[] args, CancellationToken ct)
    {
        if (args.Length > 0 && args[0].Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var n = _db.DeleteMemories(null, globalToo: true);
            return Task.FromResult($"已清空全部长期记忆（{n} 条）");
        }
        var qq = args.Length > 0 && long.TryParse(args[0], out var uid)
            ? uid
            : msg.UserId;
        var count = _db.DeleteMemories(qq, globalToo: false);
        return Task.FromResult($"已清空 {qq} 的长期记忆（{count} 条）");
    }
}

/// <summary>!remember [global] 内容 —— 添加长期记忆（默认记给当前用户；global=通用）</summary>
public sealed class RememberCommand : IBotCommand
{
    private readonly Database _db;
    public RememberCommand(Database db) => _db = db;

    public string Name => "remember";
    public string Description => "添加长期记忆";
    public string Usage => "!remember [global] 内容";

    public Task<string> ExecuteAsync(IncomingMessage msg, string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            return Task.FromResult("用法：!remember [global] 要记的内容");
        }

        bool global = args[0].Equals("global", StringComparison.OrdinalIgnoreCase);
        var content = string.Join(" ", global ? args[1..] : args);
        if (content.Length == 0) return Task.FromResult("内容不能为空");

        // 归属：私聊记给用户；群聊记给群（群聊中的约定/话题更贴近群维度）
        var scope = global ? "global" : (msg.IsPrivate ? "user" : "group");
        _db.UpsertMemory(
            scope: scope,
            qqId: global || !msg.IsPrivate ? null : msg.UserId,
            groupId: global || msg.IsPrivate ? null : msg.GroupId,
            content: content,
            trigger: ExtractKeywords(content),
            importance: 3,
            category: "主人添加");
        var owner = global ? "通用" : (msg.IsPrivate ? $"用户 {msg.UserId}" : $"群 {msg.GroupId}");
        return Task.FromResult($"已记住：{content}（{owner}记忆）");
    }

    private static string ExtractKeywords(string text)
    {
        var words = new List<string>();
        var cur = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (ch >= '\u4e00' && ch <= '\u9fff') cur.Append(ch);
            else
            {
                if (cur.Length is >= 2 and <= 6) words.Add(cur.ToString());
                cur.Clear();
            }
        }
        if (cur.Length is >= 2 and <= 6) words.Add(cur.ToString());
        return string.Join(",", words.Take(4));
    }
}

/// <summary>
/// !draw <提示词> —— 跳过 LLM 阶段，直接把给定提示词写入 workflow 调用 ComfyUI 生图。
/// 不经过 LLM 扩写，提示词原样使用；图片生成后直接发送。
/// </summary>
public sealed class DrawCommand : IBotCommand
{
    private readonly GenerateImageTool _gen;

    public DrawCommand(GenerateImageTool gen) => _gen = gen;

    public string Name => "draw";
    public string Description => "跳过 LLM，直接用提示词调 ComfyUI 生图";
    public string Usage => "!draw <提示词>";
    public bool GuestAllowed => true;   // 生图命令对所有人开放

    public async Task<string> ExecuteAsync(IncomingMessage msg, string[] args, CancellationToken ct)
    {
        var prompt = string.Join(" ", args).Trim();
        if (prompt.Length == 0)
        {
            return "用法：!draw <提示词>（该提示词将直接写入 ComfyUI，不经过 LLM 扩写）";
        }

        // 1. 反馈"正在画"（不干等）
        await _gen.SendTextAsync(new ToolContext(msg), $"好的～正在用你给的提示词画「{BuiltinCommands.Truncate(prompt, 20)}」…", ct);

        // 2. 直接生图并发送（跳过扩写）
        var (ok, message) = await _gen.GenerateAndSendAsync(prompt, new ToolContext(msg), ct);
        if (ok)
        {
            // 图片已发送，返回空字符串 → CommandRouter 不再重复回复
            return "";
        }
        return $"生图失败：{message}";
    }
}

/// <summary>
/// !summarize —— 手动总结最近对话并沉淀长期记忆（人工兜底层）。
/// 默认总结当前会话；可指定用户（私聊）或群（群聊）。
/// </summary>
public sealed class SummarizeCommand : IBotCommand
{
    private readonly MemoryService _memory;
    public SummarizeCommand(MemoryService memory) => _memory = memory;

    public string Name => "summarize";
    public string Description => "手动总结最近对话并沉淀长期记忆";
    public string Usage => "!summarize [QQ号] 或 !summarize group [群号]";

    public async Task<string> ExecuteAsync(IncomingMessage msg, string[] args, CancellationToken ct)
    {
        long? qqId;
        long? groupId;
        string sessionKey;

        if (args.Length >= 2 && args[0].Equals("group", StringComparison.OrdinalIgnoreCase)
                              && long.TryParse(args[1], out var gid))
        {
            qqId = null; groupId = gid; sessionKey = $"group:{gid}";
        }
        else if (args.Length >= 1 && long.TryParse(args[0], out var uid))
        {
            qqId = uid; groupId = null; sessionKey = $"private:{uid}";
        }
        else
        {
            qqId = msg.IsPrivate ? msg.UserId : null;
            groupId = msg.IsPrivate ? null : msg.GroupId;
            sessionKey = msg.SessionKey;
        }

        var n = await _memory.SummarizeRecentAsync(qqId, groupId, sessionKey, ct);
        return n > 0
            ? $"已从最近对话沉淀 {n} 条长期记忆（{sessionKey}）"
            : $"（{sessionKey}）最近对话没有值得沉淀的新记忆，或消息太少（需至少 2 条）";
    }
}
