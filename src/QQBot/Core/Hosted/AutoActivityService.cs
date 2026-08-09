using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QQBot.Core.Chat;
using QQBot.Core.Memory;
using QQBot.Core.OneBot;
using QQBot.Core.Options;
using QQBot.Core.Tools;

namespace QQBot.Core.Hosted;

/// <summary>
/// 自主活动服务：按配置间隔（默认 1 小时）定时唤醒静静，让她自主决定做点什么：
///  - 私聊主人（send_private_to_owner）
///  - 读取群最新消息、群里插嘴（send_group_message）
///  - 在自己的小空间捣鼓（run_shell）
///  - 记记忆/查时间/浏览网页等
/// 活动使用独立工具集（不含普通对话的 send_text），且与对话互不干扰；
/// 若上一次活动仍在执行则跳过本次（互斥防重）。
/// </summary>
public sealed class AutoActivityService : BackgroundService
{
    private readonly AutoActivityOptions _options;
    private readonly BotOptions _bot;
    private readonly ChatEngine _engine;
    private readonly Database _db;
    private readonly MemoryService _memory;
    private readonly OneBotClient _client;
    private readonly ShellOptions _shell;
    private readonly ILogger<AutoActivityService> _logger;
    private readonly ILogger<ShellTool> _shellLogger;

    private readonly SemaphoreSlim _runningLock = new(1, 1);

    public AutoActivityService(
        AutoActivityOptions options,
        BotOptions bot,
        ChatEngine engine,
        Database db,
        MemoryService memory,
        OneBotClient client,
        ShellOptions shell,
        ILogger<AutoActivityService> logger,
        ILogger<ShellTool> shellLogger)
    {
        _options = options;
        _bot = bot;
        _engine = engine;
        _db = db;
        _memory = memory;
        _client = client;
        _shell = shell;
        _logger = logger;
        _shellLogger = shellLogger;
    }

    /// <summary>上一次自主活动触发时间（UTC）；初始为进程启动时间</summary>
    private DateTime _lastAutoRunUtc = DateTime.UtcNow;

    /// <summary>自主活动次数计数（每 3 次执行一次自动记忆整理）</summary>
    private int _activityCount;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("自主活动已禁用（AutoActivity.Enabled=false）");
            return;
        }

        var idleTime = TimeSpan.FromMinutes(Math.Max(1, _options.IntervalMinutes));
        _logger.LogInformation("自主活动已启用：静静空闲（无人理）超过 {Min} 分钟时自主行动一次", idleTime.TotalMinutes);

        // 空闲检测循环：每 30 秒检查一次"最后消息响应时间"
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { break; }

            var now = DateTime.UtcNow;
            var idle = now - ActivityClock.LastUserMessageUtc;   // 距最后一条用户消息
            var sinceLastAuto = now - _lastAutoRunUtc;            // 距上次自主活动（防连发）

            if (idle >= idleTime && sinceLastAuto >= idleTime)
            {
                await RunActivityAsync(stoppingToken);
            }
        }
    }

    /// <summary>执行一次自主活动（互斥：上一次未结束则跳过；活动期间倒计时暂停，结束后重新计时）</summary>
    private async Task RunActivityAsync(CancellationToken ct)
    {
        if (!await _runningLock.WaitAsync(0, ct))
        {
            _logger.LogWarning("上一次自主活动尚未结束，跳过本次");
            return;
        }
        try
        {
            // 活动开始：更新"上次活动时间"，检测循环的条件随之不满足 → 活动期间倒计时自然暂停
            _lastAutoRunUtc = DateTime.UtcNow;
            _logger.LogInformation("========== 静静自主活动开始 ==========");
            await DoActivityAsync(ct);

            // 自动记忆整理：每 3 次活动执行一次（低价值记忆由 LLM 决定删除/合并，5 星保护）
            _activityCount++;
            if (_activityCount % 3 == 0)
            {
                _logger.LogInformation("[自主活动] 第 {N} 次活动，执行自动记忆整理", _activityCount);
                await _memory.OrganizeAllAsync(ct);
            }
            _logger.LogInformation("========== 静静自主活动结束 ==========");
        }
        catch (OperationCanceledException) { /* 程序退出 */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "自主活动执行异常");
        }
        finally
        {
            // 活动结束：重新开始完整倒计时（再空闲 idleTime 才触发下一次）
            _lastAutoRunUtc = DateTime.UtcNow;
            _runningLock.Release();
        }
    }

    private async Task DoActivityAsync(CancellationToken ct)
    {
        // 1. 收集上下文摘要：长期记忆 + 备忘录 + 与主人的最近聊天 + 群状态
        var memories = BuildMemoriesSummary();
        var memo = LoadMemo();
        var ownerChat = BuildOwnerChatSummary();
        var groupsSummary = await BuildGroupsSummaryAsync(ct);
        _logger.LogInformation("[自主活动] 长期记忆：\n{Summary}", Truncate(memories, 300));
        _logger.LogInformation("[自主活动] 备忘录：\n{Summary}", Truncate(memo, 300));
        _logger.LogInformation("[自主活动] 主人聊天摘要：\n{Summary}", Truncate(ownerChat, 300));
        _logger.LogInformation("[自主活动] 群状态摘要：\n{Summary}", Truncate(groupsSummary, 500));

        // 2. 组装自主活动上下文（可用行动随开关动态生成，禁用项不出现在提示词里）
        var system = _options.SystemPrompt
            .Replace("{Actions}", BuildActionsText())
            .Replace("{Memories}", memories)
            .Replace("{OwnerChat}", ownerChat)
            .Replace("{Groups}", groupsSummary)
            .Replace("{Memo}", memo)
            .Replace("{MemoMaxChars}", _options.MemoMaxChars.ToString())
            .Replace("{MaxActions}", _options.MaxToolRounds.ToString());
        var messages = new List<ChatMessage>
        {
            new("system", system),
            new("user", $"现在是自主活动时间（{DateTime.Now:yyyy-MM-dd HH:mm}）。做点你想做的事吧，行动通过工具完成。")
        };

        // 3. 活动工具集（独立于普通对话；不包含 send_text 防止误发给真实会话）
        var activityTools = BuildActivityTools();
        _logger.LogInformation("[自主活动] 可用工具：{Tools}", string.Join(", ", activityTools.Select(t => t.Name)));
        var registry = new ToolRegistry(activityTools, _bot.Tools,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ToolRegistry>.Instance);
        var definitions = registry.BuildToolDefinitions();

        // 4. 虚拟消息上下文（自主活动没有真实消息；记记忆/查记忆默认记给主人）
        var virtualMsg = new IncomingMessage(
            0, 0, _bot.OwnerId, "主人", 0, true, "",
            new JsonArray(), $"private:{_bot.OwnerId}", true);
        var ctx = new ToolContext(virtualMsg);

        // 5. Agent 循环：LLM 自主调用工具执行行动（最多 MaxToolRounds 轮）
        JsonObject? reasoningExtra = null;
        if (_bot.Llm.DisableReasoning && !string.IsNullOrWhiteSpace(_bot.Llm.DisableReasoningPayload))
        {
            try { reasoningExtra = JsonNode.Parse(_bot.Llm.DisableReasoningPayload) as JsonObject; }
            catch { /* 忽略 */ }
        }

        var result = await _engine.CompleteWithToolsAsync(messages, definitions, ct, reasoningExtra);
        int rounds = 0;
        var actions = new List<string>();
        while (result.HasToolCalls && rounds < _options.MaxToolRounds)
        {
            _logger.LogInformation("[自主活动] 静静调用工具：{Tools}",
                string.Join(", ", result.ToolCalls.Select(t => t.Name)));

            messages.Add(new ChatMessage("assistant", null)
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

            foreach (var call in result.ToolCalls)
            {
                var output = await registry.ExecuteAsync(call.Name, call.Arguments, ctx, ct)
                             ?? $"工具 {call.Name} 不存在";
                actions.Add($"{call.Name}");
                _logger.LogInformation("[自主活动] 工具 {Name} 执行结果：{Result}",
                    call.Name, Truncate(output, 200));
                messages.Add(new ChatMessage("tool", output) { ToolCallId = call.Id });
            }

            result = await _engine.CompleteWithToolsAsync(messages, definitions, ct, reasoningExtra);
            rounds++;
        }

        if (actions.Count == 0)
        {
            _logger.LogInformation("[自主活动] 静静这次没有调用工具（选择了安静）");
        }
        else
        {
            _logger.LogInformation("[自主活动] 本次共执行 {N} 个动作：{Actions}",
                actions.Count, string.Join(" → ", actions));
        }

        // 6. 活动结束：给静静一次更新备忘录的机会（不经过工具，直接 LLM 输出全文）
        await TryUpdateMemoAsync(messages, reasoningExtra, memo, ct);
    }

    /// <summary>加载备忘录（文件不存在时创建默认模板）</summary>
    private string LoadMemo()
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _options.MemoPath));
            if (File.Exists(path))
            {
                return File.ReadAllText(path, System.Text.Encoding.UTF8).Trim();
            }
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var initial = "这里是静静的小备忘录。\n我会把想长期记住的事情写在这里（人生信条、重要提醒、经验教训），每次自主活动都会看到它，也可以在活动结束后更新它。";
            File.WriteAllText(path, initial, System.Text.Encoding.UTF8);
            return initial;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载备忘录失败");
            return "（备忘录加载失败）";
        }
    }

    /// <summary>活动结束后询问 LLM 是否更新备忘录，并把新内容写回文件（≤ MemoMaxChars 字）</summary>
    private async Task TryUpdateMemoAsync(List<ChatMessage> messages, JsonObject? reasoningExtra,
                                          string oldMemo, CancellationToken ct)
    {
        try
        {
            var prompt = $"自主活动结束了。现在给你最后一次机会更新你的备忘录（可以修改、补充、删减，" +
                         $"写你认为值得长期记住的内容，总字数不超过 {_options.MemoMaxChars} 字）。" +
                         $"\n\n【你当前的备忘录】\n{oldMemo}\n\n" +
                         $"请基于上面的当前内容决定如何更新（直接给出更新后的完整全文，不要只说改动点）；" +
                         $"如果你觉得没必要改，请只回复「保持原样」。否则请直接输出更新后的备忘录全文，不要加任何解释。";
            var result = await _engine.CompleteAsync(
                [.. messages, new ChatMessage("user", prompt)], ct, reasoningExtra);
            var text = (result.Content ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text)) return;

            // 去掉 markdown 代码块包裹
            if (text.StartsWith("```"))
            {
                var start = text.IndexOf('\n');
                var end = text.LastIndexOf("```", StringComparison.Ordinal);
                if (start > 0 && end > start) text = text[(start + 1)..end].Trim();
            }

            // 表示不改则跳过
            if (text.Length <= 8 && (text.Contains("保持原样") || text.Contains("不变") || text.Contains("不用改") || text.Contains("不需要")))
            {
                _logger.LogInformation("[自主活动] 静静选择保持备忘录不变");
                return;
            }
            if (text == oldMemo) return;

            // 字数上限：超过截断
            if (text.Length > _options.MemoMaxChars)
            {
                text = text[.._options.MemoMaxChars];
            }

            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _options.MemoPath));
            File.WriteAllText(path, text, System.Text.Encoding.UTF8);
            _logger.LogInformation("[自主活动] 备忘录已更新（{Len} 字）：{Content}", text.Length, Truncate(text, 120));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新备忘录失败（不影响活动）");
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>汇总长期记忆摘要：全局记忆 + 主人的用户记忆（按重要度取 Top 15）</summary>
    private string BuildMemoriesSummary()
    {
        try
        {
            var list = new List<MemoryRecord>();
            list.AddRange(_db.LoadMemoriesByScope("global", null, 10));
            list.AddRange(_db.LoadMemoriesByScope("user", _bot.OwnerId, 10));
            var all = list
                .DistinctBy(m => m.Id)
                .OrderByDescending(m => m.Importance)
                .Take(15)
                .ToList();
            if (all.Count == 0) return "（还没有长期记忆）";

            var sb = new StringBuilder();
            foreach (var m in all)
            {
                sb.Append("  - ").Append(m.Content);
                if (!string.IsNullOrEmpty(m.Category)) sb.Append("（").Append(m.Category).Append("）");
                sb.Append(" ★").Append(m.Importance).Append('\n');
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取长期记忆失败");
            return "（长期记忆读取失败）";
        }
    }

    /// <summary>读取与主人的最近私聊记录，格式化成摘要文本（让静静私聊前有依据）</summary>
    private string BuildOwnerChatSummary()
    {
        try
        {
            var msgs = _db.LoadRecentMessages($"private:{_bot.OwnerId}", 10);
            if (msgs.Count == 0) return "（和主人还没有私聊记录）";

            var sb = new StringBuilder();
            foreach (var m in msgs)
            {
                var who = m.Role == "user" ? "主人" : "静静";
                var content = m.Content?.Replace("\n", " ") ?? "";
                if (content.Length > 60) content = content[..60] + "…";
                sb.Append($"  {who}：{content}\n");
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取主人聊天摘要失败");
            return "（主人聊天记录读取失败）";
        }
    }

    /// <summary>读取各群最新消息，格式化成摘要文本</summary>
    private async Task<string> BuildGroupsSummaryAsync(CancellationToken ct)
    {
        try
        {
            var groups = await _client.GetGroupListAsync(ct);
            var summary = new StringBuilder();
            var shown = 0;
            foreach (var (gid, name) in groups)
            {
                if (shown >= _options.MaxGroups) break;
                var msgs = await _client.GetGroupMessagesAsync(gid, _options.RecentMessagesPerGroup, ct);
                if (msgs.Count == 0) continue;

                summary.Append($"\n【群 {name}({gid})】最近聊天：\n");
                foreach (var m in msgs)
                {
                    var sender = m["sender"]?["nickname"]?.GetValue<string>()
                                 ?? m["user_id"]?.GetValue<long>().ToString() ?? "?";
                    var text = ExtractText(m["message"] as JsonArray);
                    if (text.Length > 60) text = text[..60] + "…";
                    if (text.Length > 0) summary.Append($"  {sender}：{text}\n");
                }
                shown++;
            }
            if (shown == 0) return "（没有可读取的群聊消息）";
            return summary.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "收集群消息摘要失败");
            return "（群消息获取失败）";
        }
    }

    private static string ExtractText(JsonArray? segments)
    {
        if (segments is null) return "";
        var sb = new StringBuilder();
        foreach (var seg in segments.OfType<JsonObject>())
        {
            if (seg["type"]?.GetValue<string>() == "text")
                sb.Append(seg["data"]?["text"]?.GetValue<string>());
        }
        return sb.ToString().Trim();
    }

    /// <summary>根据开关生成"可用行动"清单文本（喂给 LLM 的 {Actions} 占位符）</summary>
    private string BuildActionsText()
    {
        var lines = new List<string>();
        if (_options.AllowPrivateToOwner)
            lines.Add("- send_private_to_owner：私聊主人（问好、汇报、提醒、撒娇都可以）");
        if (_options.AllowGroupChat)
            lines.Add("- send_group_message：在某个群里发言插嘴（参考群聊现状，有值得回应的话题才发言，要得体、别刷屏）");
        if (_options.AllowShell && _shell.Enabled)
            lines.Add("- run_shell：在自己的小空间（工作文件夹）里捣鼓点东西（创建文件、写脚本、整理东西）");
        if (_options.AllowOrganizeMemory)
            lines.Add("- organize_memory：整理记忆（只可删除或移动 3 星及以下的记忆；4/5 星受保护不可动；禁止添加新记忆）");

        if (lines.Count == 0)
            return "（本次自主活动主人没有开放任何行动，请保持安静，不要做任何事）";
        lines.Add("- 其他轻量工具：查时间、查看记忆、浏览网页等");
        return string.Join("\n", lines);
    }

    /// <summary>自主活动工具集（按开关过滤，禁用的工具不注册）</summary>
    private IEnumerable<ITool> BuildActivityTools()
    {
        var tools = new List<ITool>
        {
            new GetTimeTool(),
            new SearchMemoryTool(_db),
            new BrowseWebTool()
        };
        if (_options.AllowOrganizeMemory) tools.Add(new OrganizeMemoryTool(_db));
        if (_options.AllowPrivateToOwner) tools.Add(new SendPrivateToOwnerTool(_client, _bot));
        if (_options.AllowGroupChat) tools.Add(new SendGroupMessageTool(_client));
        if (_options.AllowShell && _shell.Enabled) tools.Add(new ShellTool(_shell, _shellLogger));
        return tools;
    }
}
