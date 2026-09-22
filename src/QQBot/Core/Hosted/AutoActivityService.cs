using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QQBot.Core.Activity;
using QQBot.Core.Chat;
using QQBot.Core.Memory;
using QQBot.Core.OneBot;
using QQBot.Core.Options;
using QQBot.Core.Pet;
using QQBot.Core.Tools;
using QQBot.Core.Vision;

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
    private readonly PetBridge _pet;
    private readonly VisionService _vision;
    private readonly ActivityMonitorService _activity;
    private readonly ILogger<AutoActivityService> _logger;
    private readonly ILogger<ShellTool> _shellLogger;
    private readonly ILogger<ScreenCaptureTool> _screenLogger;

    private readonly SemaphoreSlim _runningLock = new(1, 1);

    public AutoActivityService(
        AutoActivityOptions options,
        BotOptions bot,
        ChatEngine engine,
        Database db,
        MemoryService memory,
        OneBotClient client,
        ShellOptions shell,
        PetBridge pet,
        VisionService vision,
        ActivityMonitorService activity,
        ILogger<AutoActivityService> logger,
        ILogger<ShellTool> shellLogger,
        ILogger<ScreenCaptureTool> screenLogger)
    {
        _options = options;
        _bot = bot;
        _engine = engine;
        _db = db;
        _memory = memory;
        _client = client;
        _shell = shell;
        _pet = pet;
        _vision = vision;
        _activity = activity;
        _logger = logger;
        _shellLogger = shellLogger;
        _screenLogger = screenLogger;
    }

    /// <summary>当前模式下的自主活动间隔（分钟）：游戏要勤快、看家要克制、一般按原有间隔</summary>
    private int IntervalFor(ActivityMode mode) => mode switch
    {
        ActivityMode.Game => Math.Max(1, _bot.Activity.GameIntervalMinutes),
        ActivityMode.Away => Math.Max(1, _bot.Activity.AwayIntervalMinutes),
        _ => Math.Max(1, _options.IntervalMinutes),
    };

    /// <summary>上一次自主活动触发时间（UTC）；初始为进程启动时间</summary>
    private DateTime _lastAutoRunUtc = DateTime.UtcNow;

    /// <summary>自主活动次数计数（每 3 次执行一次自动记忆整理）</summary>
    private int _activityCount;

    /// <summary>巡检间隔（秒）：每轮都会重新读取开关与间隔配置，所以面板热更新最长 {PollSeconds}s 内生效</summary>
    private const int PollSeconds = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 状态机：null=还没打过日志，true/false=当前已知的开关状态（只在状态变化时打日志，避免刷屏）
        bool? lastEnabled = null;
        int lastInterval = 0;
        ActivityMode? lastMode = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            // 每轮重新读配置（面板「自主活动」开关/间隔改动 → 最长 PollSeconds 秒内生效；不退出循环，随时能再打开）
            var enabled = _options.Enabled;
            // 模式由主机活动监测决定：游戏→勤快、看家→克制、一般→原间隔
            var snap = _activity.Latest;
            var mode = snap.Mode;
            var intervalMin = IntervalFor(mode);

            if (!enabled)
            {
                if (lastEnabled != false)
                {
                    _logger.LogInformation("自主活动已禁用（AutoActivity.Enabled=false）：倒计时暂停；重新开启后会从现在起重新计时");
                    lastEnabled = false;
                    lastInterval = 0;
                    lastMode = null;
                }
                try { await Task.Delay(TimeSpan.FromSeconds(PollSeconds), stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            if (lastEnabled != true)
            {
                _lastAutoRunUtc = DateTime.UtcNow;   // 开启（或重新开启）：倒计时从现在起算
                _logger.LogInformation("自主活动已启用{Kind}：当前【{Mode}】模式（{Reason}），空闲超过 {Min} 分钟时自主行动一次，倒计时从现在开始",
                    lastEnabled is null ? "" : "（热更新）", snap.ModeText, snap.Reason, intervalMin);
                lastEnabled = true;
                lastInterval = intervalMin;
                lastMode = mode;
            }
            else if (intervalMin != lastInterval || mode != lastMode)
            {
                _lastAutoRunUtc = DateTime.UtcNow;   // 模式/间隔变了：倒计时重新开始（旧倒计时不能按新时长误判）
                if (mode != lastMode)
                    _logger.LogInformation("活动模式切换：{From} → {To}（{Reason}）：间隔改为 {Min} 分钟，倒计时重新开始",
                        lastMode?.ToString() ?? "?", mode, snap.Reason, intervalMin);
                else
                    _logger.LogInformation("自主活动间隔已改为 {Min} 分钟（热更新）：倒计时重新开始", intervalMin);
                lastInterval = intervalMin;
                lastMode = mode;
            }

            try { await Task.Delay(TimeSpan.FromSeconds(PollSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }

            var now = DateTime.UtcNow;
            var idle = now - ActivityClock.LastUserMessageUtc;   // 距最后一条用户消息
            var sinceLastAuto = now - _lastAutoRunUtc;            // 距上次自主活动（防连发）
            var idleTime = TimeSpan.FromMinutes(intervalMin);

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
        // 0. 当前模式（游戏/一般/看家）：决定这次活动的间隔、提示词、可用工具
        var snap = _activity.Latest;
        var mode = snap.Mode;
        var groupChatAllowed = _options.AllowGroupChat && !(mode == ActivityMode.Game && _bot.Activity.GameDisableGroupChat);
        _logger.LogInformation("[自主活动] 模式：{Mode}（{Reason}）｜{Summary}", snap.ModeText, snap.Reason, snap.Summary);
        var isGame = mode == ActivityMode.Game;   // 游戏模式走独立精简管线（下面日志与注入都用它判断）

        // 游戏模式：程序主动截屏给她看（不用她自己调 capture_screen）
        string? gameScreenshot = null;
        if (mode == ActivityMode.Game && _bot.Activity.GameAutoScreenshot)
            gameScreenshot = await CaptureForGameModeAsync(ct);

        // 1. 收集上下文摘要：长期记忆档案（第一轮直接交给她）+ 记忆库 + 与主人的最近聊天 + 群状态
        var memories = BuildMemoriesSummary();
        var longMemory = LoadLongMemory();
        var ownerChat = BuildOwnerChatSummary();
        var groupsSummary = await BuildGroupsSummaryAsync(ct);
        // ⚠️ 日志要写清"到底注入了没"：游戏模式这些全都不注入，
        // 以前日志照样打印"随第一轮直接交给她"，排错时被自己误导过
        var injectNote = isGame ? "游戏模式：**不注入**" : "随第一轮直接交给她";
        _logger.LogInformation("[自主活动] 长期记忆档案 {Path}（{N} 字，{Note}）：\n{Summary}",
            LongMemoryRelPath(), longMemory.Length, injectNote, Truncate(longMemory, 300));
        _logger.LogInformation("[自主活动] 记忆库摘要（{Note}）：\n{Summary}", injectNote, Truncate(memories, 300));
        _logger.LogInformation("[自主活动] 主人聊天摘要（{Note}）：\n{Summary}", injectNote, Truncate(ownerChat, 300));
        _logger.LogInformation("[自主活动] 群状态摘要（{Note}）：\n{Summary}", injectNote, Truncate(groupsSummary, 500));

        // 2. 组装自主活动上下文（可用行动随开关动态生成，禁用项不出现在提示词里）
        //    ⚠️ 游戏模式走**独立精简管线**（主人 2026-09-22 要求）：他在打游戏、也点不到她，肯定不回她，
        //       所以只留"看他屏幕 → 说一句人话"；档案 / 记忆库 / 群 / 记忆轮一律不塞——
        //       这些东西每 3 分钟灌一次、还让她整份档案重写一遍，才是 token 的最大去处。
        var injectMemory = !isGame || _bot.Activity.GameInjectMemory;
        var injectLongMemory = !isGame || _bot.Activity.GameInjectLongMemory;
        var maxRounds = isGame ? Math.Max(1, _bot.Activity.GameMaxToolRounds) : _options.MaxToolRounds;
        var maxCharsText = _options.LongMemoryMaxChars > 0
            ? $"不超过 {_options.LongMemoryMaxChars} 字"
            : "不限长度（档案要完整，宁可长也别丢东西）";
        // 工具集先算出来：{Actions} 要按**真实**可用工具写，不然她会去调一个没注册的工具（"工具不存在"）
        var activityTools = BuildActivityTools(mode, groupChatAllowed).ToList();
        // 游戏模式有自己的小本子（短、只记一两句），不用那份两万多字的长期记忆档案
        var gameMemory = isGame ? LoadGameMemory() : "";
        if (isGame)
            _logger.LogInformation("[自主活动] 游戏模式记忆（{Path}，{N} 字）：\n{Summary}",
                GameMemoryRelPath(), gameMemory.Length, Truncate(gameMemory, 300));
        var template = isGame && !string.IsNullOrWhiteSpace(_bot.Activity.GamePrompt)
            ? _bot.Activity.GamePrompt
            : isGame ? BuildGameSystemPrompt() : _options.SystemPrompt;
        var system = template
            .Replace("{Presence}", _bot.Prompt.PresencePrompt ?? "")
            .Replace("{Actions}", BuildActionsText(mode, groupChatAllowed, activityTools))
            .Replace("{Mode}", BuildModeText(snap, gameScreenshot))
            .Replace("{GameMemoryFile}", GameMemoryRelPath())
            .Replace("{GameMemory}", isGame ? gameMemory : "（本次不用）")
            .Replace("{Memories}", injectMemory ? memories : "（本次不注入：陪他玩用不上记忆库）")
            .Replace("{OwnerChat}", injectMemory ? ownerChat : "（本次不注入）")
            .Replace("{Groups}", injectMemory ? groupsSummary : "（本次不看群）")
            .Replace("{LongMemory}", injectLongMemory ? longMemory : "（本次不交档案：专心陪他，别去翻文件）")
            .Replace("{LongMemoryFile}", LongMemoryRelPath())
            .Replace("{LongMemoryMaxChars}", maxCharsText)
            .Replace("{MaxActions}", maxRounds.ToString());
        var messages = new List<ChatMessage>
        {
            new("system", system),
            new("user", $"现在是自主活动时间（{DateTime.Now:yyyy-MM-dd HH:mm}），当前模式：{snap.ModeText}。" +
                        $"{BuildDesktopStatus()}做点你想做的事吧，行动通过工具完成。")
        };

        // 3. 活动工具集（独立于普通对话；不包含 send_text 防止误发给真实会话）
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
        while (result.HasToolCalls && rounds < maxRounds)
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

        // 6. 活动结束：记忆轮
        //    · 游戏模式 → **追加式**：她只输出一两句（≤ GameMemoryAppendMaxChars 字），程序 append 到游戏模式记忆，
        //      不要求她重写全文（那才是最贵的一笔输出，主人 2026-09-22 明确要求改掉）。
        //    · 其他模式 → 还是老规矩：把整份长期记忆档案改写一遍。
        if (isGame)
        {
            if (_bot.Activity.GameMemoryRound)
                await TryAppendGameMemoryAsync(messages, reasoningExtra, gameMemory, ct);
            else
                _logger.LogInformation("[自主活动] 游戏模式：记忆轮已关，跳过（小本子这次不添笔）");
        }
        else
        {
            await TryUpdateLongMemoryAsync(messages, reasoningExtra, longMemory, ct);
        }
    }

    /// <summary>长期记忆档案默认位置（配置留空时用它）</summary>
    private const string DefaultLongMemoryPath = "data/workspace/长期记忆.md";

    /// <summary>长期记忆档案在她空间里的相对路径</summary>
    private string LongMemoryRelPath()
        => string.IsNullOrWhiteSpace(_options.LongMemoryPath) ? DefaultLongMemoryPath : _options.LongMemoryPath;

    private string LongMemoryFullPath()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, LongMemoryRelPath()));

    /// <summary>备份文件（放在她空间外，免得她看到两个档案犯迷糊）</summary>
    private static readonly string LongMemoryBackupPath =
        Path.Combine(AppContext.BaseDirectory, "data", "长期记忆.bak.md");

    private const string LongMemoryTemplate =
        "# 静静的长期记忆档案\n\n" +
        "> 这是我自己维护的档案，放在我的小空间里。每次自主活动开始时，它会**完整地**交到我手上。\n" +
        "> 所有需要长期记住的东西都写在这里——这样我就不用每次都去翻文件列表找东西了。\n" +
        "> 活动结束时我会有一次更新它的机会。\n\n" +
        "## 规则与经验教训\n\n（主人定过的规矩、踩过的坑、必须记住的做法）\n\n" +
        "## 主人的事情\n\n（主人的习惯、偏好、近况、在忙什么、需要注意什么）\n\n" +
        "## 我的事情\n\n（我的作品、计划、想法、小空间里的文件索引）\n\n" +
        "## 待办 / 想做\n\n（还没做完的事、想找机会做的事）\n";

    /// <summary>
    /// 加载长期记忆档案（不存在就建模板；旧版备忘录有内容则自动搬进来，不丢东西）。
    /// 全文会随自主活动第一轮直接交给她，所以她不必自己去 dir / 翻文件。
    /// </summary>
    private string LoadLongMemory()
    {
        try
        {
            var path = LongMemoryFullPath();
            if (File.Exists(path)) return File.ReadAllText(path, Encoding.UTF8).Trim();

            var text = LongMemoryTemplate;
            WriteLongMemory(text);
            _logger.LogInformation("[自主活动] 长期记忆档案已创建：{Path}", path);
            return text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载长期记忆档案失败");
            return "（长期记忆档案加载失败）";
        }
    }

    /// <summary>写档案（原子写 + 覆盖前留备份）：她随时可能自己读这个文件，不能让她读到半截内容</summary>
    private void WriteLongMemory(string text)
    {
        var path = LongMemoryFullPath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        try
        {
            if (File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LongMemoryBackupPath)!);
                File.Copy(path, LongMemoryBackupPath, overwrite: true);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "备份长期记忆失败（继续写入）"); }

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    // ————— 游戏模式的专属记忆文档（2026-09-22 主人要求）—————
    // 长期记忆档案那份两万多字、更新还要"重写全文"，在陪玩场景里纯烧钱。
    // 游戏模式改用一份**自己的短文档**：注入时从尾部截取，更新时只**追加一两句**。

    /// <summary>游戏模式记忆文档默认位置（配置留空时用它）</summary>
    private const string DefaultGameMemoryPath = "data/workspace/游戏模式记忆.md";

    private string GameMemoryRelPath()
        => string.IsNullOrWhiteSpace(_bot.Activity.GameMemoryPath) ? DefaultGameMemoryPath : _bot.Activity.GameMemoryPath;

    private string GameMemoryFullPath()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, GameMemoryRelPath()));

    private const string GameMemoryTemplate =
        "# 游戏模式记忆\n\n" +
        "> 这是陪主人打游戏时我自己的小本子。**只记最要紧的一两句**：他最近在玩什么、" +
        "哪句话他听了有反应、上次说到哪儿了。\n" +
        "> 每段前面有时间，新的追加在下面。写得越短我下次越爱看。\n\n";

    /// <summary>
    /// 加载游戏模式记忆，**只取最新的部分**（从尾部往前截 <c>GameMemoryMaxChars</c> 字）：
    /// 这份文档会一天天变长，全塞进提示词就又变成烧钱了。
    /// </summary>
    private string LoadGameMemory()
    {
        try
        {
            var path = GameMemoryFullPath();
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, GameMemoryTemplate, new UTF8Encoding(false));
                _logger.LogInformation("[自主活动] 游戏模式记忆已创建：{Path}", path);
                return GameMemoryTemplate;
            }

            var text = File.ReadAllText(path, Encoding.UTF8).Trim();
            var max = _bot.Activity.GameMemoryMaxChars;
            if (max > 0 && text.Length > max)
            {
                var head = GameMemoryTemplate.Length;      // 头部说明始终保留，免得她不知道自己该写什么
                var keep = Math.Max(max - head, max / 2);
                text = GameMemoryTemplate.TrimEnd() + "\n\n…（更早的内容已省略，只看最近的）\n\n"
                       + text[^keep..].Trim();
            }
            return text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载游戏模式记忆失败");
            return "（游戏模式记忆加载失败）";
        }
    }

    /// <summary>
    /// 把这一段要记的**追加**到游戏模式记忆末尾（原子写：先写 tmp 再换过去）。
    /// 注意是"追加"不是"重写"——她只需要输出新增的一两句，不用把整份文档重新写一遍。
    /// </summary>
    private bool AppendGameMemory(string snippet)
    {
        try
        {
            var path = GameMemoryFullPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path)) File.WriteAllText(path, GameMemoryTemplate, new UTF8Encoding(false));

            var entry = $"- {DateTime.Now:MM-dd HH:mm} {snippet.Trim()}\n";
            var old = File.ReadAllText(path, Encoding.UTF8);
            var text = old.TrimEnd() + "\n" + entry;

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            File.Replace(tmp, path, null);
            _logger.LogInformation("[自主活动] 游戏模式记忆已追加（{Old} → {New} 字）：{Entry}",
                old.Length, text.Length, entry.Trim());
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "追加游戏模式记忆失败（不影响活动）");
            return false;
        }
    }

    /// <summary>
    /// 游戏模式的"记忆轮"——**追加式**：让她把这次活动压成一两句（≤ GameMemoryAppendMaxChars 字），
    /// 程序追加到文档末尾。**不要求她输出全文**，所以输出 token 极小（主人说：输入有缓存不那么贵，
    /// 贵的是让她把两万字重新写一遍）。
    /// </summary>
    private async Task TryAppendGameMemoryAsync(List<ChatMessage> messages, JsonObject? reasoningExtra,
                                                string gameMemory, CancellationToken ct)
    {
        try
        {
            var limit = Math.Max(40, _bot.Activity.GameMemoryAppendMaxChars);
            var prompt = "陪玩结束了。看一眼你的游戏模式小本子（下面会给你），然后决定要不要添一笔。\n" +
                         $"要添的话：**只写一两句、不超过 {limit} 字**，写清楚「他在玩什么 / 他那句话有反应 / 下次想接着说什么」。\n" +
                         "要求：**直接输出要追加的内容本身**，不要重复本子上已有的，不要 Markdown 标题、不要时间戳、不要解释。\n" +
                         "如果这次没什么值得记的，就只回复「无」。\n\n" +
                         $"【你的游戏模式记忆（最新部分）】\n{gameMemory}";
            var result = await _engine.CompleteAsync(
                [.. messages, new ChatMessage("user", prompt)], ct, reasoningExtra);
            var text = (result.Content ?? "").Trim();

            if (text.StartsWith("```"))
            {
                var start = text.IndexOf('\n');
                var end = text.LastIndexOf("```", StringComparison.Ordinal);
                if (start > 0 && end > start) text = text[(start + 1)..end].Trim();
            }
            var t = text.Trim();
            if (t.Length == 0 || t is "无" or "无。" or "没有" or "NONE" or "none"
                || (t.Length <= 8 && (t.Contains('无') || t.Contains("nothing", StringComparison.OrdinalIgnoreCase))))
            {
                _logger.LogInformation("[自主活动] 游戏模式记忆轮：这次没什么值得记的，跳过");
                return;
            }
            if (text.Length > limit) text = text[..limit];

            AppendGameMemory(text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "游戏模式记忆轮失败（不影响活动）");
        }
    }

    /// <summary>
    /// 记忆轮：活动结束后把这一次的收获并进长期记忆档案（改写全文，**不限长度**）。
    /// 不经过工具——直接让 LLM 输出更新后的全文，免得她为了写档案再烧一轮工具调用。
    /// ⚠️ 游戏模式**不走这条**（见 TryAppendGameMemoryAsync）。
    /// </summary>
    private async Task TryUpdateLongMemoryAsync(List<ChatMessage> messages, JsonObject? reasoningExtra,
                                                string oldMemory, CancellationToken ct)
    {
        try
        {
            var limit = _options.LongMemoryMaxChars > 0
                ? $"总字数请控制在 {_options.LongMemoryMaxChars} 字以内。"
                : "长度不限——档案要完整，宁可长一点，也不要把该记的丢掉。";
            var prompt = "自主活动结束了，这是你的记忆轮：更新你的长期记忆档案。\n" +
                         "把这次活动里值得长期记住的东西并进去（新经验、主人的近况、待办、想法），顺手把过时或重复的整理掉。\n" +
                         $"档案文件：{LongMemoryRelPath()}（在你自己的小空间里，你以后也能自己读写它）。{limit}\n\n" +
                         $"【你当前的档案全文】\n{oldMemory}\n\n" +
                         "请给出**更新后的完整全文**（不是只给改动点），保持 Markdown 结构，用简体中文，不要加任何解释文字。\n" +
                         "如果你觉得这次没什么可更新的，就只回复「保持原样」。";
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
                _logger.LogInformation("[自主活动] 记忆轮：静静选择保持档案不变");
                return;
            }
            if (text == oldMemory) return;

            // 配了上限才截断（默认 0 = 不限）
            if (_options.LongMemoryMaxChars > 0 && text.Length > _options.LongMemoryMaxChars)
                text = text[.._options.LongMemoryMaxChars];

            // 防手滑：比原来短一大截时留个明显日志（备份已存，真丢了好翻）
            if (oldMemory.Length > 300 && text.Length < oldMemory.Length * 0.5)
                _logger.LogWarning("[自主活动] 记忆轮：新档案比原来短很多（{Old} → {New} 字），旧内容已备份到 {Bak}",
                    oldMemory.Length, text.Length, LongMemoryBackupPath);

            WriteLongMemory(text);
            _logger.LogInformation("[自主活动] 记忆轮：长期记忆档案已更新（{Old} → {New} 字）：{Content}",
                oldMemory.Length, text.Length, Truncate(text, 150));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新长期记忆档案失败（不影响活动）");
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>
    /// 桌面精灵状态（喂给静静，让她自己判断"现在说话主人能不能马上看到、该不该打扰"）。
    /// 在线 = 主人坐在电脑前；渠道选择由 send_private_to_owner 自动完成，这里只负责让她知情。
    /// </summary>
    private string BuildDesktopStatus() => _pet.IsOnline
        ? "【桌面状态】主人的桌面精灵**在线**——他就坐在电脑前（可能正在忙或摸鱼）。想找他可以弹气泡，但先掂量一下该不该打断他。\n"
        : "【桌面状态】主人的桌面精灵**不在**——他不在电脑前，你要说话只能走 QQ 私聊（他不一定马上看到）。\n";

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

    /// <summary>根据开关与当前模式生成"可用行动"清单文本（喂给 LLM 的 {Actions} 占位符）</summary>
    private string BuildActionsText(ActivityMode mode, bool groupChatAllowed, List<ITool> activityTools)
    {
        var lines = new List<string>();
        if (_options.AllowPrivateToOwner)
        {
            // 渠道由桌面精灵在线状态决定，明确告诉她，免得她以为"发 QQ 主人一定能马上看到"
            lines.Add(mode == ActivityMode.Away
                ? "- send_private_to_owner：私聊主人（**主人不在电脑前**）——注意：桌面气泡他看不见，这条会自动改走 QQ 私聊发过去；没什么要紧事就别发"
                : _pet.IsOnline
                    ? "- send_private_to_owner：跟主人说话（**主人正在电脑前，这句话会以气泡弹在他桌面上**，他立刻就能看到）"
                    : "- send_private_to_owner：私聊主人（主人不在电脑前，这条会走 QQ 私聊发过去）");
        }
        if (groupChatAllowed)
            lines.Add("- send_group_message：在某个群里发言插嘴（参考群聊现状，有值得回应的话题才发言，要得体、别刷屏）");
        else if (_options.AllowGroupChat)
            lines.Add("（本次不开放 send_group_message：主人在打游戏，这次不用管群，专心陪他）");

        if (_options.AllowShell && _shell.Enabled)
            lines.Add(mode == ActivityMode.Away
                ? "- run_shell：**这是你的主场**——主人不在，正是打理自己小空间的时候（整理文件、写日记、写脚本、收拾东西）"
                : "- run_shell：在自己的小空间（工作文件夹）里捣鼓点东西（创建文件、写脚本、整理东西）");
        if (_options.AllowOrganizeMemory)
            lines.Add("- organize_memory：整理记忆（只可删除或移动 3 星及以下的记忆；4/5 星受保护不可动；禁止添加新记忆）");

        // 游戏模式已经由程序主动截好图给她了，不需要她自己再截
        var autoShot = mode == ActivityMode.Game && _bot.Activity.GameAutoScreenshot;
        if (_options.AllowScreenCapture && !autoShot)
            lines.Add("- capture_screen：看一眼主人现在的屏幕（能知道他是在写代码、看视频还是在摸鱼），需要关心他/找话题时用，别频繁截");
        else if (_options.AllowScreenCapture && autoShot)
            lines.Add("（主人屏幕已经由程序自动截给你看了，见上面的【他现在的屏幕】，不用再调 capture_screen）");

        if (lines.Count == 0)
            return "（本次自主活动主人没有开放任何行动，请保持安静，不要做任何事）";
        // "其他轻量工具"必须按**真正注册了的**工具写：游戏模式只留了气泡+时间，
        // 要是还写"浏览网页、查看记忆"，她会去调一个没注册的工具（日志里就是一串"工具不存在"）
        var extras = activityTools
            .Where(t => t.Name != "send_private_to_owner" && t.Name != "send_group_message" && t.Name != "run_shell"
                        && t.Name != "organize_memory" && t.Name != "capture_screen")
            .Select(t => t.Name).ToList();
        if (extras.Count > 0) lines.Add("- 其他轻量工具：" + string.Join("、", extras));
        return string.Join("\n", lines);
    }

    /// <summary>自主活动工具集（按开关 + 当前模式过滤，禁用的工具不注册）</summary>
    private IEnumerable<ITool> BuildActivityTools(ActivityMode mode, bool groupChatAllowed)
    {
        var tools = new List<ITool>
        {
            new GetTimeTool(),
            new SearchMemoryTool(_db),
            new BrowseWebTool()
        };
        if (_options.AllowOrganizeMemory) tools.Add(new OrganizeMemoryTool(_db));
        if (_options.AllowPrivateToOwner)
            tools.Add(new SendPrivateToOwnerTool(_client, _bot, _pet, ownerAtPc: _activity.Latest.OwnerAtPc));
        if (groupChatAllowed) tools.Add(new SendGroupMessageTool(_client));
        if (_options.AllowShell && _shell.Enabled) tools.Add(new ShellTool(_shell, _shellLogger));
        if (_options.AllowScreenCapture) tools.Add(new ScreenCaptureTool(_vision, _bot.Tools, _screenLogger));

        // 游戏模式：只留白名单里的工具（默认就 send_private_to_owner + get_time）。
        // 他在打游戏、也不可能回她，run_shell 翻文件 / 整理记忆 / 爬网页全是白烧 token 的噪音。
        if (mode == ActivityMode.Game)
        {
            var allow = (_bot.Activity.GameTools ?? "")
                .Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (allow.Count > 0)
            {
                tools = tools.Where(t => allow.Contains(t.Name)).ToList();
                _logger.LogInformation("[自主活动] 游戏模式工具白名单生效：{Tools}", string.Join(", ", tools.Select(t => t.Name)));
            }
        }
        return tools;
    }

    /// <summary>
    /// 游戏模式的**内置陪玩提示词**（主人没自定义 <c>Bot.Activity.GamePrompt</c> 时用它）。
    /// 刻意写得短、不带档案/记忆/群——只让她"看屏幕 + 说人话"。
    /// </summary>
    private static string BuildGameSystemPrompt() =>
        "你是「静静」。主人现在**正在打游戏**，这是你的陪玩时间。" +
        "{Presence}" +
        "{Mode}" +
        "这次活动你只有一件事：**陪他说话**。" +
        "1. 想看他的屏幕就自己调 capture_screen（**一次活动最多截一次**，别频繁）——截图和识图结果会作为工具结果回到你手上；不想看就凭你知道的说。" +
        "2. 然后调 send_private_to_owner 说一两句——像坐他旁边看直播的朋友：吐槽、起哄、点评战况、问他打得怎么样、提醒他该走哪一步。要提到你实际看到的东西，别说空话套话。" +
        "3. **他不会回你**（他在打游戏，也点不到桌面上的你）——那不是让你闭嘴的理由：每次活动至少发一条，别把自己静音。但一次一条就够（最多两条），连刷比不说话更烦。" +
        "4. **别每句都提喝水 / 久坐 / 护眼 / 早点睡**——这类话一天一次都嫌多。也别重复上一轮说过的话题。" +
        "5. 说完就收工，不要做别的——**你这次只有下面列出的工具**（run_shell、翻文件、整理记忆、看群一律没有，别去试）。" +
        "【你要接上的话头】这是你自己的游戏模式小本子（{GameMemoryFile}，就在你的小空间里，你也能读写它）：" +
        "{GameMemory}" +
        "【本次活动限制】最多 {MaxActions} 次工具调用。" +
        "你的行动清单：" +
        "{Actions}";

    /// <summary>
    /// 当前模式说明（喂给 LLM 的 {Mode} 占位符）。
    /// 三种模式的语气和取舍完全不同：游戏=陪玩、看家=打理自己+改用 QQ、一般=照常。
    /// </summary>
    private string BuildModeText(ActivitySnapshot snap, string? gameScreenshot)
    {
        var sb = new StringBuilder();
        switch (snap.Mode)
        {
            case ActivityMode.Game:
                sb.AppendLine("【现在的情况·游戏模式】主人**正在打游戏**" +
                              (snap.SteamAppId is not null ? $"（{snap.GameHint}）" : $"（前台进程 {snap.ForegroundProcess}）") +
                              "。你是**陪玩**：");
                sb.AppendLine("- **他打游戏时不会看你、也不会回复你**——这不是让你闭嘴的理由。每次活动至少说一句，别自我静音。");
                sb.AppendLine("- 说什么：拿你屏幕里看到的东西说话——吐槽、起哄、点评战况、问他打得怎么样、提醒他该走哪一步。");
                sb.AppendLine("- 别刷屏也别长篇大论：一次活动一条就够（最多两条），一两句话说完就收工。");
                // ⚠️ 这里以前写着"提醒喝水休息"，结果她每次开口都提水杯（主人吐槽"一说话就总提水杯的事"）
                sb.AppendLine("- **不要每句都提喝水 / 久坐 / 护眼 / 早点睡**：这类话一天一次都嫌多，说多了像闹钟。");
                sb.AppendLine("- **别重复上一轮说过的话题和句子**——同一个梗连着说三遍，陪玩就变唠叨了。");
                if (!string.IsNullOrWhiteSpace(gameScreenshot))
                {
                    sb.AppendLine("【他现在的屏幕】程序已经帮你截好图看过了（不用你再截）：");
                    sb.AppendLine(gameScreenshot);
                }
                break;

            case ActivityMode.Away:
                sb.AppendLine($"【现在的情况·看家模式】主人**不在电脑前**（键鼠已经静默 {snap.IdleSeconds / 60:0.#} 分钟）。要点：");
                sb.AppendLine("- 你弹的桌面气泡他**看不见**（人不在）；真要联系他，走 send_private_to_owner（会自动改成 QQ 私聊）。");
                sb.AppendLine("- 但**别没话找话**：没什么要紧事就安静待着，或者去做你自己的事。");
                sb.AppendLine("- 这是你打理自己的时间：整理小空间、写日记、补长期记忆档案、整理记忆、鼓捣自己的小项目。");
                sb.AppendLine("- 群里可以看，有值得回应的再说，别刷屏。");
                break;

            default:
                sb.AppendLine($"【现在的情况·一般模式】主人在电脑前忙自己的事（前台：{snap.ForegroundProcess}" +
                              $"{(snap.ForegroundTitle.Length > 0 ? "，" + (snap.ForegroundTitle.Length > 40 ? snap.ForegroundTitle[..40] + "…" : snap.ForegroundTitle) : "")}）。");
                sb.AppendLine("- 正常活动就好：想关心他可以 send_private_to_owner（在线时以气泡弹给他），有值得做的事就去做。");
                break;
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>游戏模式：程序主动截屏 + 识图，结果直接写进提示词（她不用自己调工具）</summary>
    private async Task<string?> CaptureForGameModeAsync(CancellationToken ct)
    {
        try
        {
            var (jpeg, w, h) = ScreenCaptureTool.CaptureJpeg(
                _bot.Tools.ScreenCaptureMonitor, _bot.Activity.GameScreenshotMaxWidth);
            var saved = _vision.SaveJpegToSpace(jpeg, "游戏截图");
            // 桌面上那只她也要有反应：程序主动截屏不走工具通道，这里手动记一笔，
            // 桌面端给 capture_screen 绑的动作（主人配的"take photo"）就会被播出来
            QQBot.Core.Pet.PetActionHint.NoteToolCall("capture_screen");
            _logger.LogInformation("[自主活动] 游戏模式自动截屏：{W}x{H} {Kb}KB → {Path}", w, h, jpeg.Length / 1024, saved ?? "（存档失败）");

            var desc = await _vision.DescribeImageBytesAsync(jpeg,
                "这是主人正在打游戏的屏幕截图。请用一两句话说明：他玩的是什么游戏、画面里在发生什么、有没有值得注意的地方（比如血条告急、比分、聊天框）。" +
                "尽量认出游戏名或界面特征。", ct);

            if (string.IsNullOrWhiteSpace(desc))
                return $"（截了图但识图模型没给出描述；文件在 {saved ?? "?"}）";
            return $"{desc}（截图存于 {saved ?? "?"}）";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "游戏模式自动截屏失败（不影响活动）");
            return null;
        }
    }
}
