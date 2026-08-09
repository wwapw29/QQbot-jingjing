using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using QQBot.Core.Chat;
using QQBot.Core.Options;

namespace QQBot.Core.Memory;

/// <summary>
/// 神经链记忆服务（2026-08 改进版）：
///  - 写入三层：当场（remember 工具/“记住”开头，保守）/ 沉淀（后台累积式 function call 总结，指令放消息最后）/ 手动（!summarize 命令）
///  - 写入门控：信息密度（长度/信号词）过滤寒暄；累积式（攒 N 条才总结）；正则硬事实通道补 trigger
///  - 归属三类：user(qq_id) 私聊 / group(group_id) 群聊 / global 通用
///  - 提取两步：① 结构化定位（QQ/群号精确检索，SQL 走索引）② 语境筛选（触发词 + 2-gram 相关度 + 硬事实 + 5 星常驻）
///  - 生命周期：用进废退——被唤起升温（use_count++），重要度随时间惰性衰减
///  - 去重合并：新记忆与同归属旧记忆 2-gram 相似度 ≥ 阈值时更新旧的而非新增
/// </summary>
public sealed class MemoryService
{
    private readonly Database _db;
    private readonly ChatEngine _engine;
    private readonly MemoryOptions _options;
    private readonly ILogger<MemoryService> _logger;
    /// <summary>累积式总结缓冲：sessionKey → 待总结对话（攒满 SummarizeBatchSize 才触发）</summary>
    private readonly ConcurrentDictionary<string, List<(string Role, string Text)>> _pendingConvos = new();

    public MemoryService(Database db, ChatEngine engine, MemoryOptions options, ILogger<MemoryService> logger)
    {
        _db = db;
        _engine = engine;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 对话前调用：两步定位提取记忆并生成注入文本。
    /// qqId=私聊对方/群聊说话人；groupId=群号（私聊为 null）；mentionedQqIds=消息中 @/提及的其他人 QQ。
    /// </summary>
    public string BuildMemoryInjection(long? qqId, long? groupId, long[]? mentionedQqIds, string userMessage)
    {
        try
        {
            // 第一步：结构化定位（QQ/群号精确检索候选）
            var candidates = _db.GetContextMemories(qqId, groupId, mentionedQqIds, 500);
            if (candidates.Count == 0) return "";

            var now = DateTime.Now;
            // 第二步：语境筛选——5 星常驻 或 触发词/内容相关/硬事实命中
            var userHardFacts = ExtractHardFacts(userMessage);
            var picked = new List<MemoryRecord>();
            foreach (var rec in candidates)
            {
                var eff = GetEffectiveImportance(rec, now);
                var always = eff >= _options.AlwaysInjectImportance;      // 默认 5 星才常驻
                var hit = TriggerHits(rec.Trigger, userMessage)
                          || GramsSimilarity(rec.Content, userMessage) > 0.2
                          || HardFactsHit(rec, userHardFacts);
                if (always || hit) picked.Add(rec);
            }
            picked = picked
                .OrderByDescending(rec => (TriggerHits(rec.Trigger, userMessage)
                                           || GramsSimilarity(rec.Content, userMessage) > 0.2
                                           || HardFactsHit(rec, userHardFacts)) ? 1 : 0)
                .ThenByDescending(rec => GetEffectiveImportance(rec, now))
                .Take(_options.MaxMemoriesPerTurn)
                .ToList();
            if (picked.Count == 0) return "";

            // 被唤起：升温（用进废退）
            foreach (var rec in picked) _db.TouchMemory(rec.Id);

            var sb = new StringBuilder("\n\n【你记得的长期记忆】这些是从对话中沉淀下来的，回复时自然提及即可，不要一次性倒出来，更不要生硬背诵——只在你觉得相关时提起。\n");
            foreach (var rec in picked)
            {
                var eff = GetEffectiveImportance(rec, now);
                var stars = new string('★', Math.Clamp((int)Math.Round(eff), 1, 5));
                var cat = string.IsNullOrEmpty(rec.Category) ? "" : $"（{rec.Category}）";
                sb.Append("- 你记得：").Append(rec.Content).Append(cat).Append(' ').Append(stars).Append('\n');
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "构建记忆注入失败");
            return "";
        }
    }

    /// <summary>
    /// 写入门控：信息密度判断——用户消息够长或命中事实信号词才值得总结（寒暄/短消息跳过）。
    /// EventDispatcher 在触发后台总结前调用。
    /// </summary>
    public bool ShouldSummarize(string userText)
    {
        if (string.IsNullOrWhiteSpace(userText)) return false;
        if (_options.MinSummaryLength > 0 && userText.Length >= _options.MinSummaryLength) return true;
        return _options.FactSignalWords.Any(w =>
            !string.IsNullOrWhiteSpace(w) && userText.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 对话后/命令触发调用：累积式总结——会话缓冲攒够 SummarizeBatchSize 条才真正调 LLM。
    /// 返回本次写入条数（未到阈值返回 0）。
    /// </summary>
    public async Task<int> SummarizeAsync(long? qqId, long? groupId, string sessionKey,
                                          IReadOnlyList<(string Role, string Text)> convo,
                                          CancellationToken ct = default)
    {
        if (!_options.EnableAutoMemory) return 0;
        if (convo.Count == 0) return 0;

        // 累积到会话缓冲，攒满阈值才总结（减少 LLM 调用、总结带完整上下文）
        var buffer = _pendingConvos.GetOrAdd(sessionKey, _ => new());
        lock (buffer) buffer.AddRange(convo);
        int count;
        lock (buffer) count = buffer.Count;
        if (count < _options.SummarizeBatchSize) return 0;

        List<(string Role, string Text)> batch;
        lock (buffer)
        {
            batch = [.. buffer];
            buffer.Clear();
        }
        return await SummarizeBatchAsync(qqId, groupId, sessionKey, batch, ct);
    }

    /// <summary>真正执行一次记忆总结（batch 为待总结的对话内容），function call 提取写入</summary>
    private async Task<int> SummarizeBatchAsync(long? qqId, long? groupId, string sessionKey,
                                                IReadOnlyList<(string Role, string Text)> batch,
                                                CancellationToken ct)
    {
        if (batch.Count == 0) return 0;

        // 拒绝型对话不总结：LLM 拒绝请求是模型行为，不是用户真实信息/偏好，写入会污染记忆库
        var lastAssistant = batch.LastOrDefault(x => x.Role == "assistant").Text;
        if (IsRefusalReply(lastAssistant))
        {
            _logger.LogInformation("对话为拒绝型回复，跳过记忆总结（session={Session}）", sessionKey);
            return 0;
        }

        try
        {
            var convoText = string.Join("\n", batch.Select(x =>
                x.Role == "user" ? $"用户说：{x.Text}" : $"机器人回：{x.Text}"));

            // 消息队列：指令放在最后一条（近因效应，让 LLM 刚看完对话就执行提取）
            var messages = new List<ChatMessage>
            {
                new("system",
                    "你是记忆整理器，负责把对话中值得长期记住的信息沉淀到记忆库。\n" +
                    "【只记】可复用的长期事实：用户偏好、身份信息、生活习惯、承诺约定、重要关系、规则。\n" +
                    "【禁止】一次性事件（今天吃了什么/去了哪/临时心情）、寒暄、无关闲聊。\n" +
                    "【重要度】默认 1~3；用户反复提及、强烈表态或明确要求记录时才给 4~5。\n" +
                    "【trigger】给 2~6 字的唤起关键词，逗号分隔，今后聊天中提到这些词时会想起这条记忆。"),
                new("user", $"以下是刚才的对话：\n{convoText}"),
                new("user", "现在请根据上面的对话调用 save_memories 工具，把值得长期记住的信息写入。没有值得记的就把 memories 传空数组。")
            };

            var result = await _engine.CompleteWithToolsAsync(messages, BuildSaveMemoriesTools(), ct, forceTool: "save_memories");
            var call = result.ToolCalls.FirstOrDefault(t => t.Name == "save_memories");
            if (call is null)
            {
                _logger.LogInformation("记忆总结未产生工具调用（session={Session}）", sessionKey);
                return 0;
            }

            var parsed = ParseSaveMemoriesArgs(call.Arguments);
            if (parsed.Count == 0)
            {
                _logger.LogInformation("记忆总结无可写条目（session={Session}）", sessionKey);
                return 0;
            }

            var written = 0;
            foreach (var m in parsed.Take(_options.MaxMemoriesPerSummary))
            {
                if (WriteMemoryWithMerge(qqId, groupId, m, sessionKey)) written++;
            }
            _logger.LogInformation("已写入 {N} 条长期记忆（session={Session}）", written, sessionKey);
            return written;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "记忆总结失败（不影响对话）");
            return 0;
        }
    }

    /// <summary>手动总结最近对话（!summarize 命令用）：取会话最近 N 条消息强制总结（绕过累积）</summary>
    public async Task<int> SummarizeRecentAsync(long? qqId, long? groupId, string sessionKey, CancellationToken ct = default)
    {
        // 清掉未攒满的累积缓冲，避免与本次总结重复
        _pendingConvos.TryRemove(sessionKey, out _);
        var msgs = _db.LoadRecentMessages(sessionKey, _options.SummarizeMaxMessages);
        if (msgs.Count < 2) return 0;
        var convo = msgs.Select(m => (m.Role, m.Content ?? "")).ToList();
        return await SummarizeBatchAsync(qqId, groupId, sessionKey, convo, ct);
    }

    /// <summary>
    /// 显式记住：消息以"记住/记下/记一下/帮我记住/记着"开头 → 直接写入（私聊挂人/群聊挂群）。
    /// 返回 true 表示已写入（调用方应回复"记住了"反馈）。
    /// </summary>
    public bool TryRememberExplicit(long? qqId, long? groupId, string userText)
    {
        var text = userText.Trim();
        // 长指令词优先匹配（"帮我记住" 必须在 "记住" 之前）
        string? prefix = null;
        foreach (var kw in new[] { "帮我记住", "记一下", "记住", "记下", "记着" })
        {
            if (text.StartsWith(kw, StringComparison.Ordinal)) { prefix = kw; break; }
        }
        if (prefix is null || text.Length <= prefix.Length) return false;

        var content = text[prefix.Length..].Trim().TrimStart('：', ':', '，', ',').Trim();
        if (content.Length == 0) return false;

        return AddMemory(qqId, groupId, content, global: false, category: "用户要求") > 0;
    }

    /// <summary>
    /// 反馈闭环：用户说"不用记/记错了/别记"时撤销该会话最近写入的一条记忆。返回是否撤销成功。
    /// </summary>
    public bool TryUndoMemory(string sessionKey)
    {
        try
        {
            var mem = _db.GetLatestMemoryBySession(sessionKey);
            if (mem is null) return false;
            var ok = _db.DeleteMemoryById(mem.Id);
            if (ok) _logger.LogInformation("记忆纠错：已删除 [{Id}] {Content}", mem.Id, mem.Content[..Math.Min(mem.Content.Length, 40)]);
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "记忆纠错失败");
            return false;
        }
    }

    /// <summary>
    /// 自动记忆整理（自主活动定时调用）：取低价值记忆（存储重要度 ≤2，5 星天然保护）让 LLM 决定删除/合并，程序执行。
    /// 返回处理条数（删除+合并）。
    /// </summary>
    public async Task<int> OrganizeAllAsync(CancellationToken ct = default)
    {
        try
        {
            var candidates = _db.LoadMemoriesByScope(null, null, 100)
                .Where(m => m.Importance <= 2)
                .OrderByDescending(m => m.UpdatedAt)
                .Take(30)
                .ToList();
            if (candidates.Count == 0) return 0;

            var list = string.Join("\n", candidates.Select(m =>
                $"[{m.Id}] ({(m.Scope == "user" ? $"用户{m.QqId}" : m.Scope == "group" ? $"群{m.GroupId}" : "通用")}) {m.Content}(★{m.Importance})"));
            var messages = new List<ChatMessage>
            {
                new("system",
                    "你是记忆库管理员。下面是待整理的低价值记忆清单（★≤2）。请决定：\n" +
                    "1. 删除过时/无价值/空泛的记忆（delete_ids）\n" +
                    "2. 合并内容重复或高度相似的记忆（merge_pairs，保留编号小的那条）\n" +
                    "5 星记忆绝不在清单里，无需担心。拿不准的保留（不要删除）。"),
                new("user", $"待整理的低价值记忆清单：\n{list}"),
                new("user", "请调用 review_memories 工具给出处理决定。")
            };

            var result = await _engine.CompleteWithToolsAsync(messages, BuildReviewMemoriesTools(), ct, forceTool: "review_memories");
            var call = result.ToolCalls.FirstOrDefault(t => t.Name == "review_memories");
            if (call is null) return 0;

            var args = JsonNode.Parse(call.Arguments) as JsonObject;
            var processed = 0;
            // 删除
            if (args?["delete_ids"] is JsonArray delIds)
            {
                foreach (var idNode in delIds.OfType<JsonValue>())
                {
                    if (idNode.TryGetValue<long>(out var id) && candidates.Any(c => c.Id == id) && _db.DeleteMemoryById(id))
                    {
                        processed++;
                    }
                }
            }
            // 合并：保留编号小的那条，内容取更长的，触发词并集，重要度取 max
            if (args?["merge_pairs"] is JsonArray pairs)
            {
                foreach (var pair in pairs.OfType<JsonArray>())
                {
                    if (pair.Count < 2) continue;
                    if (pair[0] is not JsonValue v0 || pair[1] is not JsonValue v1) continue;
                    if (!v0.TryGetValue<long>(out var a) || !v1.TryGetValue<long>(out var b)) continue;
                    if (a == b) continue;
                    var ma = candidates.FirstOrDefault(c => c.Id == a);
                    var mb = candidates.FirstOrDefault(c => c.Id == b);
                    if (ma is null || mb is null) continue;
                    var keep = a < b ? ma : mb;
                    var drop = a < b ? mb : ma;
                    var mergedContent = keep.Content.Length >= drop.Content.Length ? keep.Content : drop.Content;
                    var mergedTrigger = string.Join(",",
                        (keep.Trigger + "," + drop.Trigger).Split([',', '，', ';', '；', '、'], StringSplitOptions.RemoveEmptyEntries).Distinct());
                    _db.UpdateMemoryContent(keep.Id, mergedContent, mergedTrigger, Math.Max(keep.Importance, drop.Importance));
                    _db.DeleteMemoryById(drop.Id);
                    processed++;
                }
            }
            if (processed > 0) _logger.LogInformation("自动记忆整理：处理 {N} 条低价值记忆", processed);
            return processed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "自动记忆整理失败");
            return 0;
        }
    }

    /// <summary>review_memories 工具定义（自动整理专用）</summary>
    private static JsonArray BuildReviewMemoriesTools() => new()
    {
        new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "review_memories",
                ["description"] = "给出低价值记忆的处理决定（删除/合并）。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["delete_ids"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" }, ["description"] = "要删除的记忆 id 列表" },
                        ["merge_pairs"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" } }, ["description"] = "要合并的记忆 id 对 [[id1,id2],...]（保留 id 小的）" }
                    },
                    ["required"] = new JsonArray("delete_ids", "merge_pairs")
                }
            }
        }
    };

    /// <summary>通用写入入口（供工具/命令调用）：写记忆 + 抽取触发词（含正则硬事实补充）</summary>
    public long AddMemory(long? qqId, long? groupId, string content, bool global, string? category = null)
    {
        var trigger = MergeTrigger("", ExtractKeywords(content).Take(4), ExtractHardFacts(content));
        var scope = global ? "global" : (groupId.HasValue ? "group" : "user");
        return _db.UpsertMemory(
            scope: scope,
            qqId: global || groupId.HasValue ? null : qqId,
            groupId: global || !groupId.HasValue ? null : groupId,
            content: content,
            trigger: trigger,
            importance: 3,
            category: category ?? (global ? "通用" : (groupId.HasValue ? "群聊" : "用户")));
    }

    /// <summary>把触发词 + 硬事实合并成逗号分隔的 trigger（去重）</summary>
    private static string MergeTrigger(string baseTrigger, IEnumerable<string> keywords, List<string> hardFacts)
    {
        var parts = new List<string>();
        foreach (var kw in baseTrigger.Split([',', '，', ';', '；', '、'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (kw.Trim().Length > 0) parts.Add(kw.Trim());
        }
        parts.AddRange(keywords.Select(k => k.Trim()).Where(k => k.Length > 0));
        parts.AddRange(hardFacts);
        return string.Join(",", parts.Distinct().Take(10));
    }

    // ---------------- 内部工具 ----------------

    /// <summary>save_memories 工具定义（记忆总结专用，不注册进对话 Agent 工具表）</summary>
    private static JsonArray BuildSaveMemoriesTools() => new()
    {
        new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "save_memories",
                ["description"] = "把对话中值得长期记住的信息批量写入记忆库（偏好/身份/习惯/承诺/关系等可复用长期事实）。",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["memories"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["description"] = "要写入的记忆列表；没有值得记的传空数组",
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["content"] = new JsonObject { ["type"] = "string", ["description"] = "记忆内容，简洁、可直接引用" },
                                    ["trigger"] = new JsonObject { ["type"] = "string", ["description"] = "2~6 字唤起关键词，逗号分隔" },
                                    ["scope"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("user", "global"), ["description"] = "user=只与该用户相关；global=对所有人都适用（如静静自己的行为准则）" },
                                    ["importance"] = new JsonObject { ["type"] = "integer", ["description"] = "1~5；默认 1~3，反复提及或用户明确要求才 4~5" },
                                    ["category"] = new JsonObject { ["type"] = "string", ["description"] = "偏好/事件/承诺/习惯/身份/规则" },
                                    ["user_qq"] = new JsonObject { ["type"] = "integer", ["description"] = "群聊中信息明确属于某个群友时填其 QQ 号（该记忆仅在【该群+该群友】范围内生效）；不确定就不填" }
                                },
                                ["required"] = new JsonArray("content")
                            }
                        }
                    },
                    ["required"] = new JsonArray("memories")
                }
            }
        }
    };

    /// <summary>解析 save_memories 工具参数（结构化，无需格式容错）</summary>
    private List<NewMemory> ParseSaveMemoriesArgs(string arguments)
    {
        var result = new List<NewMemory>();
        try
        {
            var node = JsonNode.Parse(arguments);
            var arr = node?["memories"] as JsonArray ?? [];
            foreach (var item in arr.OfType<JsonObject>())
            {
                var related = item["related_to"] as JsonArray;
                var content = item["content"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(content)) continue;
                result.Add(new NewMemory(
                    Content: content.Trim(),
                    Trigger: item["trigger"]?.GetValue<string>() ?? "",
                    Scope: (item["scope"]?.GetValue<string>() ?? "user").ToLowerInvariant() == "global" ? "global" : "user",
                    Importance: item["importance"]?.GetValue<int>() ?? 2,
                    Category: item["category"]?.GetValue<string>(),
                    RelatedTo: related?.Select(x => x?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToArray() ?? [],
                    UserQq: item["user_qq"] is JsonValue uq && uq.TryGetValue<long>(out var qq) ? qq : null));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "解析记忆工具参数失败: {Args}", arguments[..Math.Min(arguments.Length, 300)]);
        }
        return result;
    }

    /// <summary>写入一条记忆（含去重合并 + 归属 + 建链 + 群聊个人双写），成功返回 true</summary>
    private bool WriteMemoryWithMerge(long? qqId, long? groupId, NewMemory m, string sessionKey)
    {
        var importance = Math.Clamp(m.Importance, 1, 5);
        var targetScope = m.Scope == "global" ? "global" : (groupId.HasValue ? "group" : "user");
        // 群聊中信息明确属于某个群友（UserQq）时：主记忆存「该群+该群友」组合域，
        // 仅在该群该人上下文命中，不污染群层面记忆也不污染该用户的其他场景
        var targetQq = targetScope == "group" && m.UserQq.HasValue && m.UserQq.Value > 0
            ? m.UserQq.Value
            : (targetScope == "user" ? qqId : null);
        var targetGroup = targetScope == "group" ? groupId : null;

        // 正则硬事实补充 trigger（QQ/日期/时间/金额/词表+宾语）
        var enrichedTrigger = MergeTrigger(m.Trigger, [], ExtractHardFacts(m.Content));

        long id;
        // 去重合并：同归属域找相似旧记忆，相似则更新旧的而非新增
        var similar = _db.FindSimilarMemory(targetScope, targetQq, targetGroup, m.Content, _options.DuplicateThreshold);
        if (similar is not null)
        {
            var mergedContent = m.Content.Length > similar.Content.Length ? m.Content : similar.Content;
            var mergedTrigger = string.Join(",",
                (enrichedTrigger + "," + similar.Trigger).Split([',', '，', ';', '；', '、'], StringSplitOptions.RemoveEmptyEntries).Distinct());
            var mergedImp = Math.Max(similar.Importance, importance);
            _db.UpdateMemoryContent(similar.Id, mergedContent, mergedTrigger, mergedImp);
            id = similar.Id;
            _logger.LogInformation("记忆去重合并：更新 [{Id}] {Content}", id, mergedContent[..Math.Min(mergedContent.Length, 40)]);
        }
        else
        {
            id = _db.UpsertMemory(targetScope, targetQq, targetGroup, m.Content, enrichedTrigger, importance, m.Category, sessionKey);
            if (id <= 0) return false;
        }

        // 神经链：与相关旧记忆建立关联边
        foreach (var related in m.RelatedTo)
        {
            if (string.IsNullOrWhiteSpace(related)) continue;
            var oldId = _db.FindMemoryIdByContent(qqId, related);
            if (oldId > 0 && oldId != id) _db.LinkMemories(id, oldId);
        }
        return true;
    }

    /// <summary>有效重要度 = 存储重要度 - 每天衰减量 × 距上次衰减天数（惰性计算，不落库）</summary>
    private double GetEffectiveImportance(MemoryRecord rec, DateTime now)
    {
        var baseTime = rec.DecayAt ?? rec.UpdatedAt;
        var days = Math.Max(0, (now - baseTime).TotalDays);
        return rec.Importance - _options.DecayPerDay * days + _options.BoostOnUse * rec.UseCount;
    }

    /// <summary>触发词命中：trigger 中任一词出现在用户消息里（子串匹配）</summary>
    private static bool TriggerHits(string trigger, string userText)
    {
        if (string.IsNullOrWhiteSpace(trigger) || string.IsNullOrWhiteSpace(userText)) return false;
        var kws = trigger.Split([',', '，', ';', '；', '、', ' '], StringSplitOptions.RemoveEmptyEntries);
        return kws.Any(kw => userText.Contains(kw, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>正则硬事实提取：从文本中抽结构化硬事实（QQ/日期/时间/金额/词表+宾语），写入补 trigger、检索直接命中</summary>
    private List<string> ExtractHardFacts(string text)
    {
        var facts = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return facts;
        foreach (var pattern in _options.HardFactPatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            try
            {
                foreach (Match m in Regex.Matches(text, pattern))
                {
                    var v = m.Value.Trim().TrimEnd('，', '。', '、', ',', '.');
                    if (v.Length is >= 2 and <= 30) facts.Add(v);
                }
            }
            catch { /* 用户配置了非法正则则忽略该条 */ }
        }
        return facts.Distinct().ToList();
    }

    /// <summary>硬事实命中：用户消息与记忆内容提取出的硬事实有完全一致的项（QQ号/日期精确匹配）</summary>
    private bool HardFactsHit(MemoryRecord rec, List<string> userHardFacts)
    {
        if (userHardFacts.Count == 0) return false;
        var recFacts = ExtractHardFacts(rec.Content);
        if (recFacts.Count == 0) return false;
        return userHardFacts.Any(uf => recFacts.Contains(uf));
    }

    /// <summary>内容级相似度（2-gram Jaccard），用词不同也能召回</summary>
    private static double GramsSimilarity(string a, string b)
    {
        var ga = Database.Grams(a);
        var gb = Database.Grams(b);
        if (ga.Count == 0 || gb.Count == 0) return 0;
        var inter = ga.Intersect(gb).Count();
        return (double)inter / (ga.Count + gb.Count - inter);
    }

    /// <summary>判断本轮 AI 回复是否为"拒绝型"（命中配置的拒绝关键词即视为拒绝）</summary>
    private bool IsRefusalReply(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var kw in _options.RefusalKeywords)
        {
            if (string.IsNullOrWhiteSpace(kw)) continue;
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static IEnumerable<string> ExtractKeywords(string text)
    {
        // 简单启发式：取 2~6 字的连续汉字片段（避免虚词），最多返回几个
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in text)
        {
            if (ch >= '\u4e00' && ch <= '\u9fff') current.Append(ch);
            else
            {
                if (current.Length is >= 2 and <= 6) words.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length is >= 2 and <= 6) words.Add(current.ToString());
        return words;
    }
}
