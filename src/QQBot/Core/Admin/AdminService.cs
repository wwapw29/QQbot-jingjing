using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QQBot.Core.Memory;
using QQBot.Core.OneBot;
using QQBot.Core.Options;
using QQBot.Core.Tools;
using QQBot.Core.Commands;
using QQBot.Core.Chat;

namespace QQBot.Core.Admin;

/// <summary>
/// 后台管理面板：进程内嵌轻量 HTTP 服务（System.Net.HttpListener，零额外依赖）。
/// 提供管理页面（单 HTML，位于运行目录 admin.html）+ REST API。
/// 认证：除页面文件外，/api/* 需 Authorization: Bearer {Admin.Token}。
/// </summary>
public sealed class AdminService : IHostedService
{
    private readonly BotOptions _options;
    private readonly Database _db;
    private readonly IConfiguration _config;
    private readonly OneBotClient _client;
    private readonly ToolRegistry _tools;
    private readonly CommandRouter _router;
    private readonly ILogger<AdminService> _logger;
    private readonly DateTime _startTime = DateTime.Now;   // 实例字段：DI 创建（程序启动）时初始化
    private readonly HttpClient _evalHttp = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly object _evalLock = new();   // 评测数据文件读写锁
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public AdminService(BotOptions options, Database db, IConfiguration config, OneBotClient client,
                        ToolRegistry tools, CommandRouter router, ILogger<AdminService> logger)
    {
        _options = options;
        _db = db;
        _config = config;
        _client = client;
        _tools = tools;
        _router = router;
        _logger = logger;
    }

    /// <summary>配置项（Path 相对 Bot 节点；Type: bool|int|string）</summary>
    private sealed record CfgItem(string[] Path, string Label, string Desc, string Type, bool NeedsRestart = false);

    /// <summary>配置组（MainSwitch 非空时：关闭则子项收起不生效，开启展开；可手动展开/收起）</summary>
    private sealed record CfgGroup(string Id, string Label, string Desc, string[]? MainSwitch, CfgItem[] Items);

    private static readonly CfgGroup[] ConfigGroups =
    [
        new("llm", "主模型（LLM）", "对话回复使用的模型与接口", null,
        [
            new CfgItem(["Llm", "Model"], "模型名", "对话模型；可点右侧「获取列表」从 API 拉取", "string"),
            new CfgItem(["Llm", "BaseUrl"], "API 地址", "OpenAI 兼容接口地址（热更新生效）", "string"),
            new CfgItem(["Llm", "ApiKey"], "API 密钥", "热更新生效", "string"),
            new CfgItem(["Llm", "Temperature"], "采样温度", "越高越随机（0~2）", "double"),
            new CfgItem(["Llm", "MaxTokens"], "单次回复 token 上限", "", "int"),
            new CfgItem(["Llm", "TimeoutSeconds"], "请求超时（秒）", "", "int"),
            new CfgItem(["Llm", "MaxRetries"], "失败重试次数", "", "int"),
            new CfgItem(["Llm", "MaxToolRounds"], "工具轮上限", "单条消息最多工具调用轮数", "int"),
            new CfgItem(["Llm", "DisableReasoning"], "关闭思维链(cot)", "关=先思考再输出(带 END_REASONING 标记)；开=直接输出(省 token)", "bool"),
            new CfgItem(["Llm", "DisableReasoningPayload"], "关闭思维链附加字段", "关闭思维链时注入请求体顶层的 JSON（豆包用 {\"thinking\":{\"type\":\"disabled\"}}）", "text"),
        ]),
        new("napcat", "NapCat 连接", "与 NapCat 的通信设置（改 WsUrl 会自动重连）", null,
        [
            new CfgItem(["WsUrl"], "WebSocket 地址", "接收事件（改后自动重连）", "string"),
            new CfgItem(["HttpBase"], "HTTP 地址", "调用 OneBot API（热更新生效）", "string"),
            new CfgItem(["AccessToken"], "访问令牌", "NapCat 配置的鉴权令牌", "string"),
        ]),
        new("trigger", "触发规则", "哪些消息会唤起静静", null,
        [
            new CfgItem(["Trigger", "PrivateEnabled"], "私聊响应", "是否响应私聊消息", "bool"),
            new CfgItem(["Trigger", "GroupAtOnly"], "群聊仅 @ 响应", "群聊只有 @ 静静 时才响应", "bool"),
            new CfgItem(["Trigger", "GroupKeywordTrigger"], "群聊关键词触发", "未被@时，正文(不含引用段)含触发词也响应（需配合下方触发词）", "bool"),
            new CfgItem(["Trigger", "TriggerWords"], "群聊触发词", "逗号/顿号分隔（如 静静,静静酱）；正文含任一即触发", "string"),
            new CfgItem(["Trigger", "MergeSeconds"], "消息合并窗口(秒)", "转发+留言等连续消息合并成整体回复；0=关闭；窗口内再次@则拆分", "int"),
            new CfgItem(["Prompt", "PrivateExtra"], "私聊附加提示", "私聊场景追加的提示词", "text"),
            new CfgItem(["Prompt", "GroupExtra"], "群聊附加提示", "群聊场景追加的提示词", "text"),
        ]),
        new("basic", "基础", "通用行为", null,
        [
            new CfgItem(["PingEcho"], "ping 回显", "收到纯 ping 消息回 pong（连通性测试）", "bool"),
            new CfgItem(["Debug"], "调试模式", "打印完整 prompt / LLM 请求日志", "bool"),
            new CfgItem(["Prompt", "AutoInjectGroupHistory"], "群记录注入", "被 @ 自动拉群记录注入（替代 get_chat_history）", "bool"),
            new CfgItem(["Prompt", "MaxContextMessages"], "历史消息条数", "会话保留的最近消息条数（群注入/拉取上限）", "int"),
            new CfgItem(["Reply", "MaxRepliesPerTurn"], "最大续写次数", "多轮回复数组的最大条数（告诉 LLM 每次回复最多发几条，逐条间隔发送）", "int"),
            new CfgItem(["Reply", "IntervalMs"], "连发间隔毫秒", "数组内多条消息的发送间隔（毫秒）", "int"),
        ]),
        new("planning", "规划轮", "回复前先做一次规划（手动 cot）", ["Planning", "Enabled"],
        [
            new CfgItem(["Planning", "Visible"], "规划可见", "把规划内容也发给用户看（调试用）", "bool"),
            new CfgItem(["Planning", "MaxChars"], "规划长度上限", "超过截断", "int"),
        ]),
        new("vision", "识图", "图片下载压缩后用识图模型识别并注入描述", ["Vision", "Enabled"],
        [
            new CfgItem(["Vision", "UseMainModel"], "使用主模型识图", "开=直接用主模型看图(忽略识图Model/BaseUrl/ApiKey，不带描述指令直接发图；测主模型视觉用，热更新)", "bool"),
            new CfgItem(["Vision", "FileTtlSeconds"], "Files API 有效期秒", "DeepSeek Files API 图片上传有效期（默认 86400=24 小时；1 小时~30 天）", "int"),
            new CfgItem(["Vision", "Model"], "识图模型", "必须支持视觉（BaseUrl/ApiKey 留空=复用主 LLM）", "string"),
            new CfgItem(["Vision", "BaseUrl"], "识图 API 地址", "留空=复用主模型（热更新生效）", "string"),
            new CfgItem(["Vision", "ApiKey"], "识图 API 密钥", "留空=复用主模型（热更新生效）", "string"),
            new CfgItem(["Vision", "JpegQuality"], "压缩质量", "1~100，保持原尺寸", "int"),
            new CfgItem(["Vision", "MaxImagesPerMessage"], "单条最多识图", "超出截断", "int"),
            new CfgItem(["Vision", "DescribePrompt"], "描述指令", "发给识图模型的提示（多行）", "text"),
        ]),
        new("auto", "自主活动", "空闲时静静自主行动", ["AutoActivity", "Enabled"],
        [
            new CfgItem(["AutoActivity", "IntervalMinutes"], "触发间隔（分钟）", "空闲多久触发一次", "int"),
            new CfgItem(["AutoActivity", "MaxGroups"], "遍历群数上限", "自主活动最多看几个群", "int"),
            new CfgItem(["AutoActivity", "RecentMessagesPerGroup"], "每群看几条", "拉取最近消息数", "int"),
            new CfgItem(["AutoActivity", "MaxToolRounds"], "工具轮上限", "自主活动工具调用轮数上限", "int"),
            new CfgItem(["AutoActivity", "AllowPrivateToOwner"], "允许私聊主人", "自主时主动私聊主人", "bool"),
            new CfgItem(["AutoActivity", "AllowGroupChat"], "允许群聊发言", "自主时在群里说话", "bool"),
            new CfgItem(["AutoActivity", "AllowShell"], "允许执行命令", "自主时可用 run_shell", "bool"),
            new CfgItem(["AutoActivity", "AllowOrganizeMemory"], "允许整理记忆", "自主时整理记忆", "bool"),
            new CfgItem(["AutoActivity", "MemoPath"], "备忘录路径", "相对运行目录", "string"),
            new CfgItem(["AutoActivity", "MemoMaxChars"], "备忘录字数上限", "", "int"),
        ]),
        new("shell", "命令执行", "run_shell 沙箱", ["Shell", "Enabled"],
        [
            new CfgItem(["Shell", "SandboxPath"], "沙箱目录", "命令工作目录（相对运行目录）", "string"),
            new CfgItem(["Shell", "TimeoutSeconds"], "超时（秒）", "", "int"),
            new CfgItem(["Shell", "MaxOutputChars"], "输出截断", "", "int"),
        ]),
        new("comfy", "生图（ComfyUI）", "画图相关设置", null,
        [
            new CfgItem(["ComfyUI", "BaseUrl"], "ComfyUI 地址", "http://127.0.0.1:8188（热更新生效）", "string"),
            new CfgItem(["ComfyUI", "EnableEnhance"], "提示词扩写", "生图前用 LLM 扩写提示词", "bool"),
            new CfgItem(["ComfyUI", "SerializeImage"], "生图串行", "同一时刻只跑一个生图任务（防显存爆）", "bool"),
            new CfgItem(["ComfyUI", "Width"], "出图宽度", "", "int"),
            new CfgItem(["ComfyUI", "Height"], "出图高度", "", "int"),
            new CfgItem(["ComfyUI", "Steps"], "采样步数", "", "int"),
            new CfgItem(["ComfyUI", "TimeoutSeconds"], "生成超时（秒）", "", "int"),
            new CfgItem(["ComfyUI", "QualityTags"], "质量词", "自动追加的质量标签", "text"),
            new CfgItem(["ComfyUI", "EnhanceInstruction"], "扩写指令", "提示词扩写规则（{Prompt}=用户描述，多行）", "text"),
        ]),
    ];

    /// <summary>基础提示词清单（提示词页专用：只含与功能无关的身份/场景人设与提取配置）</summary>
    private static readonly (string[] Path, string Label, string Desc, string Type)[] PromptDefs =
    [
        (["Prompt", "GlobalPrePrompt"], "全局前置人设", "每个场景都注入的身份/性格基调", "text"),
        (["Prompt", "GlobalPrePromptRole"], "全局前置角色", "user / system", "string"),
        (["Prompt", "GlobalPostPrompt"], "全局后置提示", "所有回复末尾附加的提示（如括号描述动作）", "text"),
        (["Prompt", "GlobalPostPromptRole"], "全局后置角色", "user / system", "string"),
        (["Prompt", "Owner", "SystemPrompt"], "主人 · 系统人设", "与主人对话时的规则与性格", "text"),
        (["Prompt", "Owner", "PrePrompt"], "主人 · 前置补充", "", "text"),
        (["Prompt", "Owner", "PostPrompt"], "主人 · 后置约束", "", "text"),
        (["Prompt", "Owner", "Extra"], "主人 · 附加提示", "", "text"),
        (["Prompt", "Guest", "SystemPrompt"], "客人 · 系统人设", "与客人对话时的规则与性格", "text"),
        (["Prompt", "Guest", "PrePrompt"], "客人 · 前置补充", "", "text"),
        (["Prompt", "Guest", "PostPrompt"], "客人 · 后置约束", "", "text"),
        (["Prompt", "Guest", "Extra"], "客人 · 附加提示", "", "text"),
        (["Prompt", "OwnerPrivate", "SystemPrompt"], "私聊 · 主人 · 人设", "私聊场景覆盖主人人设", "text"),
        (["Prompt", "OwnerPrivate", "PrePrompt"], "私聊 · 主人 · 前置", "", "text"),
        (["Prompt", "OwnerPrivate", "PostPrompt"], "私聊 · 主人 · 后置", "", "text"),
        (["Prompt", "OwnerPrivate", "Extra"], "私聊 · 主人 · 附加", "", "text"),
        (["Prompt", "GuestPrivate", "SystemPrompt"], "私聊 · 客人 · 人设", "", "text"),
        (["Prompt", "GuestPrivate", "PrePrompt"], "私聊 · 客人 · 前置", "", "text"),
        (["Prompt", "GuestPrivate", "PostPrompt"], "私聊 · 客人 · 后置", "", "text"),
        (["Prompt", "GuestPrivate", "Extra"], "私聊 · 客人 · 附加", "", "text"),
        (["Prompt", "OwnerGroup", "SystemPrompt"], "群聊 · 主人 · 人设", "", "text"),
        (["Prompt", "OwnerGroup", "PrePrompt"], "群聊 · 主人 · 前置", "", "text"),
        (["Prompt", "OwnerGroup", "PostPrompt"], "群聊 · 主人 · 后置", "", "text"),
        (["Prompt", "OwnerGroup", "Extra"], "群聊 · 主人 · 附加", "", "text"),
        (["Prompt", "GuestGroup", "SystemPrompt"], "群聊 · 客人 · 人设", "", "text"),
        (["Prompt", "GuestGroup", "PrePrompt"], "群聊 · 客人 · 前置", "", "text"),
        (["Prompt", "GuestGroup", "PostPrompt"], "群聊 · 客人 · 后置", "", "text"),
        (["Prompt", "GuestGroup", "Extra"], "群聊 · 客人 · 附加", "", "text"),
        (["Prompt", "ReplyExtraction", "Strategy"], "回复提取策略", "reasoningContent / delimiter / regex", "string"),
        (["Prompt", "ReplyExtraction", "Delimiter"], "提取分隔符", "Strategy=delimiter 时用", "string"),
        (["Prompt", "ReplyExtraction", "Regex"], "提取正则", "Strategy=regex 时用", "string"),
        (["Prompt", "ContinuePrompt"], "续说提示词", "已废弃：多轮回复已改为数组格式（LLM 一次输出多条消息），此配置不再生效", "text"),
        (["Prompt", "PlanningPrompt"], "规划轮提示词", "正式回复前的内部规划指令模板；{Tools}=工具摘要 {UserText}=用户消息（留空=内置默认；改后即时生效）", "text"),
    ];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Admin.Enabled) return Task.CompletedTask;
        try
        {
            _listener = new HttpListener();
            // 仅本机监听（部署到局域网时改成 http://+:Port/ 需管理员权限）
            _listener.Prefixes.Add($"http://127.0.0.1:{_options.Admin.Port}/");
            _listener.Start();
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
            _logger.LogInformation("后台面板已启动：http://127.0.0.1:{Port}/ （Token：{Token}）",
                _options.Admin.Port, _options.Admin.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "后台面板启动失败（端口被占用或权限不足？）");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _cts?.Cancel();
            _listener?.Close();   // Close 中断 GetContextAsync 阻塞
            _listener?.Abort();
        }
        catch { }
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext? ctx = null;
            try
            {
                ctx = await _listener!.GetContextAsync();
                _ = Task.Run(() => HandleAsync(ctx, ct));
            }
            catch (Exception)
            {
                if (ct.IsCancellationRequested) break;
                // 监听器被 Close/异常：退出一小段再重试，避免死循环刷日志
                try { await Task.Delay(1000, ct); } catch { break; }
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            var isPage = IsStaticAsset(path);

            // 认证：静态资源（页面/脚本）可匿名加载（JS 带 Token 调 API），其余接口需 Bearer Token
            if (!isPage && !IsAuthorized(ctx))
            {
                ctx.Response.StatusCode = 401;
                await WriteTextAsync(ctx, "{\"error\":\"unauthorized\"}", "application/json");
                return;
            }

            if (isPage) { ServeStaticFile(ctx, path); return; }
            if (path == "/api/stats") { await ServeJsonAsync(ctx, BuildStats()); return; }
            if (path == "/api/config/switches") { await HandleSwitchesAsync(ctx); return; }
            if (path == "/api/config/prompts") { await HandlePromptsAsync(ctx); return; }
            if (path == "/api/tools") { await HandleToolsAsync(ctx); return; }
            if (path == "/api/console/commands") { await ServeJsonAsync(ctx, BuildConsoleCatalog()); return; }
            if (path == "/api/console/run") { await HandleConsoleRunAsync(ctx); return; }
            if (path == "/api/logs/days") { await ServeJsonAsync(ctx, BuildLogDays()); return; }
            if (path == "/api/logs") { await HandleLogsAsync(ctx); return; }
            if (path == "/api/groups" || path.StartsWith("/api/groups/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleGroupsAsync(ctx, path);
                return;
            }
            if (path == "/api/llm/models") { await HandleModelListAsync(ctx); return; }
            if (path == "/api/memories" || path.StartsWith("/api/memories/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleMemoriesAsync(ctx, path);
                return;
            }
            if (path == "/api/entities") { await ServeJsonAsync(ctx, BuildEntities()); return; }
            if (path == "/api/eval" || path.StartsWith("/api/eval/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleEvalAsync(ctx, path);
                return;
            }

            ctx.Response.StatusCode = 404;
            await WriteTextAsync(ctx, "{\"error\":\"not found\"}", "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "后台面板请求处理失败：{Path}", ctx.Request.Url?.AbsolutePath);
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    private bool IsAuthorized(HttpListenerContext ctx)
    {
        if (string.IsNullOrWhiteSpace(_options.Admin.Token)) return true;   // 未配置 token 则放行（不建议）
        var auth = ctx.Request.Headers["Authorization"];
        return auth?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
            && auth["Bearer ".Length..].Trim() == _options.Admin.Token;
    }

    /// <summary>静态资源扩展名白名单</summary>
    private static readonly HashSet<string> StaticExts = [".html", ".js", ".css", ".json", ".png", ".jpg", ".jpeg", ".svg", ".ico", ".woff2"];

    /// <summary>是否为 wwwroot 静态资源请求（/ 与 /admin.html 之外，还支持 d3.min.js 等）</summary>
    private static bool IsStaticAsset(string path)
    {
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.Contains("..") || path.Contains('\\')) return false;
        return path == "/" || StaticExts.Contains(Path.GetExtension(path).ToLowerInvariant());
    }

    /// <summary>服务 wwwroot 下的静态文件（防路径穿越；按扩展名给 MIME）</summary>
    private void ServeStaticFile(HttpListenerContext ctx, string requestPath)
    {
        var wwwroot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "wwwroot"));
        var rel = requestPath == "/" ? "admin.html" : requestPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(wwwroot, rel));
        if (!full.StartsWith(wwwroot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            ctx.Response.StatusCode = 404;
            WriteTextAsync(ctx, "not found", "text/plain").GetAwaiter().GetResult();
            return;
        }
        var bytes = File.ReadAllBytes(full);
        var mime = Path.GetExtension(full).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".ico" => "image/x-icon",
            _ => "application/octet-stream",
        };
        ctx.Response.ContentType = mime;
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    /// <summary>首页统计：消息/会话/记忆/人数 + 近 7 天趋势 + 记忆分布 + 机器人信息</summary>
    private JsonObject BuildStats()
    {
        const int trendDays = 7;
        var trendRaw = _db.MessageTrendByDay(trendDays);
        // 补 0：生成近 N 天的日期序列（旧→新）
        var trend = new JsonArray();
        for (int i = trendDays - 1; i >= 0; i--)
        {
            var day = DateTime.Now.Date.AddDays(-i);
            var full = day.ToString("yyyy-MM-dd");
            var hit = trendRaw.FirstOrDefault(t => t.Day == full).Count;
            trend.Add(new JsonObject { ["day"] = day.ToString("MM-dd"), ["count"] = hit });
        }

        var memoryScopes = new JsonArray();
        foreach (var (scope, count) in _db.MemoryByScope())
        {
            memoryScopes.Add(new JsonObject { ["scope"] = scope, ["count"] = count });
        }

        return new JsonObject
        {
            ["messages"] = _db.CountMessages(),
            ["sessions"] = _db.CountSessions(),
            ["memories"] = _db.CountMemories(),
            ["users"] = _db.CountUsers(),
            ["trend"] = trend,
            ["memoryByScope"] = memoryScopes,
            ["bot"] = new JsonObject
            {
                ["qq"] = _options.SelfId,
                ["owner"] = _options.OwnerId,
                ["model"] = _options.Llm.Model,
                ["visionEnabled"] = _options.Vision.Enabled,
                ["planningEnabled"] = _options.Planning.Enabled,
                ["autoActivity"] = _options.AutoActivity.Enabled,
                ["uptime"] = (DateTime.Now - _startTime).TotalSeconds,
            }
        };
    }


    /// <summary>基础提示词：GET 返回清单（含当前值）；PUT 保存（复用通用保存逻辑）</summary>
    private async Task HandlePromptsAsync(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod == "GET")
        {
            var arr = new JsonArray();
            foreach (var (path, label, desc, type) in PromptDefs)
            {
                arr.Add(new JsonObject
                {
                    ["path"] = string.Join(".", path),
                    ["label"] = label,
                    ["desc"] = desc,
                    ["type"] = type,
                    ["value"] = ReadConfig(path),
                });
            }
            await ServeJsonAsync(ctx, arr);
            return;
        }
        await SaveConfigUpdatesAsync(ctx);
    }

    /// <summary>通用配置保存：写回 appsettings（保留注释）→ 同步 src → 热更新重绑定；WsUrl 变更触发重连</summary>
    private async Task SaveConfigUpdatesAsync(HttpListenerContext ctx)
    {
        {
            var bodyText = await ReadBodyAsync(ctx);
            var updates = JsonNode.Parse(bodyText) as JsonArray;
            if (updates is null)
            {
                ctx.Response.StatusCode = 400;
                await WriteTextAsync(ctx, "{\"error\":\"bad request\"}", "application/json");
                return;
            }

            var cfgPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
            if (!File.Exists(cfgPath))
            {
                ctx.Response.StatusCode = 500;
                await WriteTextAsync(ctx, "{\"error\":\"appsettings not found\"}", "application/json");
                return;
            }

            var changed = new List<string>();
            foreach (var u in updates.OfType<JsonObject>())
            {
                var p = u["path"]?.GetValue<string>();
                var v = u["value"];
                if (p is null || v is null) continue;
                var parts = p.Split('.');
                if (parts.Length == 0 || !parts[0].Equals("Bot", StringComparison.OrdinalIgnoreCase)) continue;

                var json = File.ReadAllText(cfgPath, Encoding.UTF8);
                var next = SetJsonValue(json, parts[1..], v);
                if (next != json)
                {
                    File.WriteAllText(cfgPath, next, Encoding.UTF8);
                    changed.Add(p);
                }
            }

            if (changed.Count > 0)
            {
                // 同步到源码目录（restart.bat 会用 src 覆盖运行目录，防止改动丢失）
                var srcPath = FindSrcAppsettings();
                if (srcPath is not null)
                {
                    File.WriteAllText(srcPath, File.ReadAllText(cfgPath, Encoding.UTF8), Encoding.UTF8);
                }
                // 热更新：重载配置并重新绑定到 BotOptions（大部分即时生效）
                try
                {
                    if (_config is IConfigurationRoot root) root.Reload();
                    _config.GetSection("Bot").Bind(_options);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "配置热更新失败"); }

                // WsUrl 变更：主动断开 WS，重连循环会用新地址
                if (changed.Any(c => c.Equals("Bot.WsUrl", StringComparison.OrdinalIgnoreCase)))
                {
                    _client.Reconnect();
                    _logger.LogInformation("后台面板：WsUrl 已变更，触发 WebSocket 重连");
                }
            }

            _logger.LogInformation("后台面板：配置更新 {N} 项：{List}", changed.Count, string.Join(",", changed));
            await ServeJsonAsync(ctx, new JsonObject { ["ok"] = true, ["changed"] = changed.Count });
            return;
        }

    }


    /// <summary>
    /// GET 返回分组配置清单（含当前值，主开关状态）；PUT 保存配置（写回 appsettings，保留注释）并热更新。
    /// PUT body: [{"path":"Bot.AutoActivity.IntervalMinutes","value":45}, ...]
    /// </summary>
    private async Task HandleSwitchesAsync(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod == "GET")
        {
            var groups = new JsonArray();
            foreach (var g in ConfigGroups)
            {
                var gObj = new JsonObject
                {
                    ["id"] = g.Id,
                    ["label"] = g.Label,
                    ["desc"] = g.Desc,
                    ["mainSwitch"] = g.MainSwitch is null ? null : string.Join(".", g.MainSwitch),
                    ["mainValue"] = g.MainSwitch is null ? (JsonNode?)null : ReadConfig(g.MainSwitch),
                };
                var items = new JsonArray();
                foreach (var it in g.Items)
                {
                    items.Add(new JsonObject
                    {
                        ["path"] = string.Join(".", it.Path),
                        ["label"] = it.Label,
                        ["desc"] = it.Desc,
                        ["type"] = it.Type,
                        ["needsRestart"] = it.NeedsRestart,
                        ["value"] = ReadConfig(it.Path),
                    });
                }
                gObj["items"] = items;
                groups.Add(gObj);
            }
            await ServeJsonAsync(ctx, groups);
            return;
        }

        if (ctx.Request.HttpMethod == "PUT")
        {
            await SaveConfigUpdatesAsync(ctx);
            return;
        }

        ctx.Response.StatusCode = 405;
        await WriteTextAsync(ctx, "{\"error\":\"method not allowed\"}", "application/json");
    }

    /// <summary>
    /// 工具管理：GET 返回工具清单（名称/默认描述/当前生效描述/覆盖标记/启停）；
    /// PUT 保存（body: [{"path":"Bot.Tools.Descriptions.xxx","value":"..."}, {"path":"Bot.Tools.Disabled","value":["a","b"]}]）
    /// 保存后配置热更新，下次 LLM 请求立即生效。
    /// </summary>
    private async Task HandleToolsAsync(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod == "GET")
        {
            var arr = new JsonArray();
            var overrides = _options.Tools.Descriptions ?? new Dictionary<string, string>();
            foreach (var t in _tools.AllTools)
            {
                var overridden = overrides.TryGetValue(t.Name, out var ov) && !string.IsNullOrWhiteSpace(ov);
                arr.Add(new JsonObject
                {
                    ["name"] = t.Name,
                    ["defaultDesc"] = t.Description,
                    ["desc"] = overridden ? ov : t.Description,
                    ["overridden"] = overridden,
                    ["disabled"] = _tools.IsDisabled(t.Name),
                    ["guestAllowed"] = _tools.IsGuestAllowed(t.Name),
                });
            }
            await ServeJsonAsync(ctx, arr);
            return;
        }

        if (ctx.Request.HttpMethod == "PUT")
        {
            await SaveConfigUpdatesAsync(ctx);
            return;
        }

        ctx.Response.StatusCode = 405;
        await WriteTextAsync(ctx, "{\"error\":\"method not allowed\"}", "application/json");
    }

    /// <summary>命令目录（控制台提示用）</summary>
    private JsonNode BuildConsoleCatalog()
    {
        var arr = new JsonArray();
        foreach (var c in BuiltinCommands.Catalog)
        {
            arr.Add(new JsonObject
            {
                ["name"] = c.Name,
                ["description"] = c.Description,
                ["usage"] = c.Usage,
            });
        }
        return arr;
    }

    /// <summary>执行 ! 命令（POST body: {"cmd":"!status"}），回复返回面板显示，不发送 QQ</summary>
    private async Task HandleConsoleRunAsync(HttpListenerContext ctx)
    {
        if (ctx.Request.HttpMethod != "POST")
        {
            ctx.Response.StatusCode = 405;
            await WriteTextAsync(ctx, "{\"error\":\"method not allowed\"}", "application/json");
            return;
        }
        var bodyText = await ReadBodyAsync(ctx);
        var node = JsonNode.Parse(bodyText) as JsonObject;
        var cmd = node?["cmd"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(cmd))
        {
            await ServeJsonAsync(ctx, new JsonObject { ["error"] = "缺少 cmd 参数" });
            return;
        }
        try
        {
            var (handled, reply) = await _router.ExecuteForConsoleAsync(cmd, CancellationToken.None);
            if (!handled)
            {
                await ServeJsonAsync(ctx, new JsonObject { ["error"] = "命令必须以 " + _options.Command.Prefix + " 开头" });
                return;
            }
            await ServeJsonAsync(ctx, new JsonObject { ["reply"] = reply });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "控制台命令执行失败");
            await ServeJsonAsync(ctx, new JsonObject { ["error"] = "执行失败：" + ex.Message });
        }
    }

    /// <summary>日志目录（相对运行目录，按天落盘 yyyy-MM-dd.log）</summary>
    private string LogsDir()
    {
        var dir = _options.Admin.LogsDir;
        return string.IsNullOrWhiteSpace(dir) || Path.IsPathRooted(dir)
            ? (dir ?? "data/logs")
            : Path.Combine(AppContext.BaseDirectory, dir);
    }

    /// <summary>可用日志日期列表（倒序，最新在前）</summary>
    private JsonNode BuildLogDays()
    {
        var arr = new JsonArray();
        try
        {
            foreach (var f in Directory.GetFiles(LogsDir(), "*.log")
                         .OrderByDescending(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (DateTime.TryParseExact(name, "yyyy-MM-dd", null,
                        System.Globalization.DateTimeStyles.None, out _))
                {
                    arr.Add(JsonValue.Create(name));
                }
            }
        }
        catch { }
        return arr;
    }

    /// <summary>
    /// 群管理（群黑名单，避免 bot 互相触发）：
    ///  - GET  /api/groups → [{id, name, blacklist:[qq,...]}]（NapCat get_group_list 优先，失败回退本地已知群）
    ///  - PUT  /api/groups/{gid}/blacklist  body {qq} → 加入黑名单，返回该群新列表
    ///  - DELETE /api/groups/{gid}/blacklist?qq=xxx → 移出黑名单，返回该群新列表
    /// </summary>
    private async Task HandleGroupsAsync(HttpListenerContext ctx, string path)
    {
        var method = ctx.Request.HttpMethod;

        if (path == "/api/groups")
        {
            if (method != "GET") { ctx.Response.StatusCode = 405; await WriteTextAsync(ctx, "{\"error\":\"method not allowed\"}", "application/json"); return; }
            var arr = new JsonArray();
            try
            {
                var groups = await _client.GetGroupListAsync(CancellationToken.None);
                foreach (var (gid, name) in groups)
                {
                    var bl = await BuildGroupBlacklistJsonAsync(gid);
                    arr.Add(new JsonObject { ["id"] = gid, ["name"] = name, ["blacklist"] = bl });
                }
                if (groups.Count == 0)
                {
                    // 兜底：本地已知群（消息记录统计）
                    foreach (var (gid, count) in _db.GetKnownGroups())
                    {
                        arr.Add(new JsonObject { ["id"] = gid, ["name"] = $"(群 {gid})", ["blacklist"] = await BuildGroupBlacklistJsonAsync(gid), ["fallback"] = true });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "拉取群列表失败，回退本地已知群");
                foreach (var (gid, count) in _db.GetKnownGroups())
                {
                    arr.Add(new JsonObject { ["id"] = gid, ["name"] = $"(群 {gid})", ["blacklist"] = await BuildGroupBlacklistJsonAsync(gid), ["fallback"] = true });
                }
            }
            await ServeJsonAsync(ctx, arr);
            return;
        }

        // /api/groups/{gid}/blacklist
        if (path.StartsWith("/api/groups/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/blacklist", StringComparison.OrdinalIgnoreCase))
        {
            var idStr = path["/api/groups/".Length..^"/blacklist".Length];
            if (!long.TryParse(idStr, out var gid)) { ctx.Response.StatusCode = 400; await WriteTextAsync(ctx, "{\"error\":\"bad group id\"}", "application/json"); return; }

            if (method == "PUT")
            {
                var body = JsonNode.Parse(await ReadBodyAsync(ctx)) as JsonObject;
                var qq = body?["qq"]?.GetValue<long>() ?? 0;
                if (qq <= 0) { ctx.Response.StatusCode = 400; await WriteTextAsync(ctx, "{\"error\":\"qq required\"}", "application/json"); return; }
                // 先校验该 QQ 是否在群内（get_group_member_list）；拉取失败则放行（避免误拦）
                var members = await _client.GetGroupMemberListAsync(gid, CancellationToken.None);
                if (members.Count > 0 && !members.Any(m => m.Qq == qq))
                {
                    ctx.Response.StatusCode = 400;
                    await WriteTextAsync(ctx, "{\"error\":\"该 QQ 不在这个群里\"}", "application/json");
                    return;
                }
                _db.AddGroupBlacklist(gid, qq);
                _logger.LogInformation("后台面板：群 {Gid} 加入黑名单 {Qq}", gid, qq);
                await ServeJsonAsync(ctx, await BuildGroupBlacklistJsonAsync(gid));
                return;
            }
            if (method == "DELETE")
            {
                var qqStr = ctx.Request.QueryString["qq"];
                if (long.TryParse(qqStr, out var qq) && qq > 0)
                {
                    _db.RemoveGroupBlacklist(gid, qq);
                    _logger.LogInformation("后台面板：群 {Gid} 移出黑名单 {Qq}", gid, qq);
                }
                await ServeJsonAsync(ctx, await BuildGroupBlacklistJsonAsync(gid));
                return;
            }
            ctx.Response.StatusCode = 405;
            await WriteTextAsync(ctx, "{\"error\":\"method not allowed\"}", "application/json");
            return;
        }

        ctx.Response.StatusCode = 404;
        await WriteTextAsync(ctx, "{\"error\":\"not found\"}", "application/json");
    }

    /// <summary>该群黑名单列表（带群昵称：拉群成员映射 qq→昵称，失败退化为纯 qq）</summary>
    private async Task<JsonArray> BuildGroupBlacklistJsonAsync(long gid)
    {
        var bl = new JsonArray();
        var qqs = _db.GetGroupBlacklist(gid);
        if (qqs.Count == 0) return bl;
        Dictionary<long, string> nameMap = new();
        try
        {
            foreach (var (qq, name) in await _client.GetGroupMemberListAsync(gid, CancellationToken.None))
                nameMap[qq] = name;
        }
        catch { /* 拉成员失败：昵称退化为 qq */ }
        foreach (var qq in qqs)
        {
            var name = nameMap.TryGetValue(qq, out var n) ? n : qq.ToString();
            bl.Add(new JsonObject { ["qq"] = qq, ["name"] = name });
        }
        return bl;
    }

    private JsonArray BuildGroupBlacklistJson(long gid)
    {
        var bl = new JsonArray();
        foreach (var qq in _db.GetGroupBlacklist(gid)) bl.Add(new JsonObject { ["qq"] = qq, ["name"] = qq.ToString() });
        return bl;
    }

    /// <summary>日志查询：GET /api/logs?date=yyyy-MM-dd&amp;level=INF&amp;q=关键词&amp;offset=0&amp;limit=200
    /// 按行过滤（级别/关键词），倒序（最新在前），offset/limit 分页。</summary>
    private async Task HandleLogsAsync(HttpListenerContext ctx)
    {
        var q = ctx.Request.QueryString;
        var date = q["date"];
        if (string.IsNullOrWhiteSpace(date)) date = DateTime.Now.ToString("yyyy-MM-dd");
        var level = q["level"]?.Trim().ToUpperInvariant() ?? "";
        var kw = q["q"]?.Trim() ?? "";
        int offset = int.TryParse(q["offset"], out var o) ? Math.Max(0, o) : 0;
        int limit = int.TryParse(q["limit"], out var l) ? Math.Clamp(l, 1, 1000) : 200;

        var path = Path.Combine(LogsDir(), date + ".log");
        if (!File.Exists(path))
        {
            await ServeJsonAsync(ctx, new JsonObject { ["date"] = date, ["total"] = 0, ["lines"] = new JsonArray() });
            return;
        }

        var lines = new List<(string Level, string Text)>();
        try
        {
            // FileShare.ReadWrite：日志文件正被 FileLoggerProvider 持续写入，需允许共享读写
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                var (lv, text) = SplitLogLine(line);
                if (lv is null) continue;                                  // 续行/空行跳过
                if (level.Length > 0 && lv != level) continue;
                if (kw.Length > 0 && !text.Contains(kw, StringComparison.OrdinalIgnoreCase)) continue;
                lines.Add((lv, text));
            }
        }
        catch (Exception ex)
        {
            await ServeJsonAsync(ctx, new JsonObject { ["error"] = "读取日志失败：" + ex.Message });
            return;
        }

        // 倒序（最新在前），分页
        lines.Reverse();
        var page = lines.Skip(offset).Take(limit);
        var arr = new JsonArray();
        foreach (var (lv, raw) in page)
        {
            arr.Add(new JsonObject { ["level"] = lv, ["text"] = raw });
        }
        await ServeJsonAsync(ctx, new JsonObject { ["date"] = date, ["total"] = lines.Count, ["lines"] = arr });
    }

    /// <summary>解析一行日志：匹配 yyyy-MM-dd HH:mm:ss.fff [LVL] 开头 → 返回 (级别, 原始行)；续行返回 null</summary>
    private static (string? Level, string Text) SplitLogLine(string line)
    {
        // 行首时间戳 23 字符（yyyy-MM-dd HH:mm:ss.fff）+ 空格 + [LVL]
        if (line.Length < 28 || line[10] != ' ' || line[23] != ' ' || line[24] != '[') return (null, line);
        var lv = line.Substring(25, 3);
        return (lv, line);
    }

    /// <summary>从 IConfiguration 读取配置值（相对 Bot 节点）</summary>
    private JsonNode? ReadConfig(string[] path)
    {
        var key = "Bot:" + string.Join(":", path);
        var raw = _config[key];
        if (raw is null) return null;
        if (bool.TryParse(raw, out var b)) return JsonValue.Create(b);
        if (long.TryParse(raw, out var n)) return JsonValue.Create(n);
        return JsonValue.Create(raw);
    }

    /// <summary>JSON 序列化：不转义中文等非 ASCII 字符（appsettings 直接显示中文，可读性好）</summary>
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>在 JSON 文本中按路径替换值（保留 JSONC 注释；value 为 JsonNode：bool/number/string/array）</summary>
    private static string SetJsonValue(string json, string[] path, JsonNode value)
    {
        // 序列化值文本：bool/number 原样，string 带引号转义（中文不转义），array 用 ToJsonString
        var valueText = value is JsonValue jv && jv.GetValueKind() == JsonValueKind.String
            ? JsonSerializer.Serialize(jv.GetValue<string>(), s_jsonOpts)
            : value.ToJsonString();

        int idx = 0;
        for (int i = 0; i < path.Length; i++)
        {
            var keyPattern = "\"" + path[i] + "\"";
            int pos = json.IndexOf(keyPattern, idx, StringComparison.OrdinalIgnoreCase);
            if (pos < 0) return json;
            idx = pos + keyPattern.Length;
            while (idx < json.Length && (char.IsWhiteSpace(json[idx]) || json[idx] == ':')) idx++;

            if (i == path.Length - 1)
            {
                // 数组值（如 Tools.Disabled）：找到匹配的 [ ... ] 整个区间替换（处理嵌套与字符串转义）
                if (idx < json.Length && json[idx] == '[')
                {
                    int depth = 0, end = idx;
                    bool inStr = false, esc = false;
                    while (end < json.Length)
                    {
                        char c = json[end];
                        if (esc) esc = false;
                        else if (inStr)
                        {
                            if (c == '\\') esc = true;
                            else if (c == '"') inStr = false;
                        }
                        else if (c == '"') inStr = true;
                        else if (c == '[') depth++;
                        else if (c == ']' && --depth == 0) { end++; break; }
                        end++;
                    }
                    return json[..idx] + valueText + json[end..];
                }
                // 字符串值：扫描到闭合引号（处理 \" 转义），替换整个引号区间——
                // 不能按逗号截断，否则含半角逗号的值（如 QualityTags "best quality, highly detailed"）会残留后半段写坏 JSON
                if (idx < json.Length && json[idx] == '"')
                {
                    int qEnd = idx + 1;
                    bool esc = false;
                    while (qEnd < json.Length)
                    {
                        char c = json[qEnd];
                        if (esc) esc = false;
                        else if (c == '\\') esc = true;
                        else if (c == '"') { qEnd++; break; }
                        qEnd++;
                    }
                    return json[..idx] + valueText + json[qEnd..];
                }
                // bool/number：替换到逗号/右括号/换行为止（保留行尾注释）
                int e = idx;
                while (e < json.Length && json[e] != ',' && json[e] != '}' && json[e] != '\r' && json[e] != '\n') e++;
                return json[..idx] + valueText + json[e..];
            }

            // 进入子对象：跳过 { 与空白
            while (idx < json.Length && (char.IsWhiteSpace(json[idx]) || json[idx] == '{')) idx++;
        }
        return json;
    }

    /// <summary>向上查找源码 appsettings.json（restart 同步源；找不到返回 null）</summary>
    private static string? FindSrcAppsettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "QQBot", "appsettings.json");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }



    /// <summary>已知用户/群列表（记忆库归属下拉用）</summary>
    private JsonObject BuildEntities()
    {
        var users = new JsonArray();
        foreach (var (qq, nick) in _db.ListKnownUsers())
        {
            users.Add(new JsonObject { ["id"] = qq, ["name"] = nick ?? qq.ToString(), ["isOwner"] = qq == _options.OwnerId, ["isSelf"] = qq == _options.SelfId });
        }
        var groups = new JsonArray();
        foreach (var g in _db.ListKnownGroups())
        {
            groups.Add(new JsonObject { ["id"] = g, ["name"] = g.ToString() });
        }
        return new JsonObject { ["users"] = users, ["groups"] = groups };
    }

    /// <summary>记忆 CRUD：GET 列表 / POST 新增 / PUT 更新 / DELETE 删除（单条或 ids 批量）</summary>
    private async Task HandleMemoriesAsync(HttpListenerContext ctx, string path)
    {
        var method = ctx.Request.HttpMethod;

        // 记忆图谱：GET /api/memories/graph?root=123（缺省=最高星级记忆）
        if (path == "/api/memories/graph")
        {
            if (method != "GET") { ctx.Response.StatusCode = 405; await WriteTextAsync(ctx, "{\"error\":\"method not allowed\"}", "application/json"); return; }
            long root = 0;
            if (ctx.Request.QueryString["root"] is string rs && long.TryParse(rs, out var rv)) root = rv;
            // root=0 → 全量图（3D 球状用）；root>0 → 以该记忆为中心 BFS 关联图
            var (nodes, links) = root > 0
                ? _db.LoadMemoryGraph(root, 3, 80)
                : _db.LoadFullGraph(300);
            if (root <= 0 && nodes.Count > 0) root = nodes[0].Id;
            if (nodes.Count == 0) { await ServeJsonAsync(ctx, new JsonObject { ["nodes"] = new JsonArray(), ["links"] = new JsonArray() }); return; }
            var nodeArr = new JsonArray();
            foreach (var m in nodes)
            {
                nodeArr.Add(new JsonObject
                {
                    ["id"] = m.Id, ["scope"] = m.Scope, ["qqId"] = m.QqId, ["groupId"] = m.GroupId,
                    ["content"] = m.Content, ["trigger"] = m.Trigger, ["importance"] = m.Importance, ["category"] = m.Category,
                });
            }
            var linkArr = new JsonArray();
            foreach (var (from, to, w) in links)
            {
                linkArr.Add(new JsonObject { ["source"] = from, ["target"] = to, ["weight"] = Math.Round(w, 3) });
            }
            await ServeJsonAsync(ctx, new JsonObject { ["root"] = root, ["nodes"] = nodeArr, ["links"] = linkArr });
            return;
        }

        // 一键自动建边：POST /api/memories/autolink
        if (path == "/api/memories/autolink")
        {
            if (method != "POST") { ctx.Response.StatusCode = 405; await WriteTextAsync(ctx, "{\"error\":\"method not allowed\"}", "application/json"); return; }
            var created = _db.AutoLinkMemories();
            _logger.LogInformation("后台面板：一键自动建边完成，新建 {N} 条关联", created);
            await ServeJsonAsync(ctx, new JsonObject { ["ok"] = true, ["created"] = created });
            return;
        }

        // 单条路径 /api/memories/{id}
        if (path.StartsWith("/api/memories/", StringComparison.OrdinalIgnoreCase))
        {
            var idStr = path["/api/memories/".Length..];
            if (!long.TryParse(idStr, out var id) || id <= 0)
            {
                ctx.Response.StatusCode = 400;
                await WriteTextAsync(ctx, "{\"error\":\"bad id\"}", "application/json");
                return;
            }

            if (method == "PUT")
            {
                var body = JsonNode.Parse(await ReadBodyAsync(ctx)) as JsonObject;
                if (body is null) { ctx.Response.StatusCode = 400; await WriteTextAsync(ctx, "{\"error\":\"bad request\"}", "application/json"); return; }
                string? content = body["content"]?.GetValue<string>();
                string? trigger = body["trigger"]?.GetValue<string>();
                int? importance = body["importance"] is null ? null : body["importance"]!.GetValue<int>();
                string? scope = body["scope"]?.GetValue<string>();
                long? qqId = body["qqId"] is null ? null : body["qqId"]!.GetValue<long>();
                long? groupId = body["groupId"] is null ? null : body["groupId"]!.GetValue<long>();
                var ok = _db.UpdateMemoryFull(id, content, trigger, importance, scope, qqId, groupId);
                await ServeJsonAsync(ctx, new JsonObject { ["ok"] = ok });
                return;
            }
            if (method == "DELETE")
            {
                var ok = _db.DeleteMemoryById(id);
                await ServeJsonAsync(ctx, new JsonObject { ["ok"] = ok, ["deleted"] = ok ? 1 : 0 });
                return;
            }
            ctx.Response.StatusCode = 405;
            await WriteTextAsync(ctx, "{\"error\":\"method not allowed\"}", "application/json");
            return;
        }

        if (path == "/api/memories")
        {
            if (method == "GET")
            {
                var q = ctx.Request.QueryString;
                var scope = q["scope"];          // global | user | group
                var qq = q["qq"] is string qs && long.TryParse(qs, out var qqv) ? qqv : (long?)null;
                var grp = q["group"] is string gs && long.TryParse(gs, out var gv) ? gv : (long?)null;
                var kw = q["q"];
                var page = q["page"] is string ps && int.TryParse(ps, out var pv) ? pv : 0;
                var size = q["size"] is string ss && int.TryParse(ss, out var sv) ? sv : 50;
                var (records, total) = _db.ListMemories(scope, qq, grp, kw, page, size);
                var arr = new JsonArray();
                foreach (var m in records)
                {
                    arr.Add(new JsonObject
                    {
                        ["id"] = m.Id,
                        ["scope"] = m.Scope,
                        ["qqId"] = m.QqId,
                        ["groupId"] = m.GroupId,
                        ["content"] = m.Content,
                        ["trigger"] = m.Trigger,
                        ["importance"] = m.Importance,
                        ["category"] = m.Category,
                        ["useCount"] = m.UseCount,
                        ["updatedAt"] = m.UpdatedAt.ToString("yyyy-MM-dd HH:mm"),
                    });
                }
                await ServeJsonAsync(ctx, new JsonObject { ["total"] = total, ["page"] = page, ["size"] = size, ["list"] = arr });
                return;
            }
            if (method == "POST")
            {
                var body = JsonNode.Parse(await ReadBodyAsync(ctx)) as JsonObject;
                if (body is null || string.IsNullOrWhiteSpace(body["content"]?.GetValue<string>()))
                {
                    ctx.Response.StatusCode = 400;
                    await WriteTextAsync(ctx, "{\"error\":\"content required\"}", "application/json");
                    return;
                }
                var scope = body["scope"]?.GetValue<string>() ?? "global";
                if (scope is not ("global" or "user" or "group")) scope = "global";
                long? qqId = body["qqId"] is null ? null : body["qqId"]!.GetValue<long>();
                long? groupId = body["groupId"] is null ? null : body["groupId"]!.GetValue<long>();
                var content = body["content"]!.GetValue<string>();
                var trigger = body["trigger"]?.GetValue<string>() ?? "";
                var importance = body["importance"]?.GetValue<int>() ?? 1;
                var category = body["category"]?.GetValue<string>();
                var id = _db.UpsertMemory(scope, qqId, groupId, content, trigger, Math.Clamp(importance, 1, 5), category, "admin");
                await ServeJsonAsync(ctx, new JsonObject { ["ok"] = id > 0, ["id"] = id });
                return;
            }
            if (method == "DELETE")
            {
                // 批量删除：?ids=1,2,3
                var ids = ctx.Request.QueryString["ids"];
                var deleted = 0;
                if (!string.IsNullOrWhiteSpace(ids))
                {
                    foreach (var part in ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (long.TryParse(part, out var id) && _db.DeleteMemoryById(id)) deleted++;
                    }
                }
                await ServeJsonAsync(ctx, new JsonObject { ["ok"] = true, ["deleted"] = deleted });
                return;
            }
            ctx.Response.StatusCode = 405;
            await WriteTextAsync(ctx, "{\"error\":\"method not allowed\"}", "application/json");
            return;
        }
    }

    /// <summary>拉取 OpenAI 兼容 API 的可用模型列表（GET /api/llm/models?baseUrl=&amp;apiKey=）</summary>
    private async Task HandleModelListAsync(HttpListenerContext ctx)
    {
        var q = ctx.Request.QueryString;
        var baseUrl = q["baseUrl"]?.Trim();
        var apiKey = q["apiKey"]?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            await ServeJsonAsync(ctx, new JsonObject { ["error"] = "缺少 baseUrl 参数" });
            return;
        }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            if (!string.IsNullOrWhiteSpace(apiKey))
                http.DefaultRequestHeaders.Add("Authorization", "Bearer " + apiKey);
            var url = baseUrl.TrimEnd('/') + "/models";
            var resp = await http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                await ServeJsonAsync(ctx, new JsonObject { ["error"] = "API 返回 " + (int)resp.StatusCode + "：" + (body.Length > 200 ? body[..200] : body) });
                return;
            }
            var node = JsonNode.Parse(body);
            var arr = new JsonArray();
            if (node?["data"] is JsonArray data)
            {
                foreach (var m in data.OfType<JsonObject>())
                {
                    var id = m["id"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(id)) arr.Add(JsonValue.Create(id));
                }
            }
            await ServeJsonAsync(ctx, new JsonObject { ["models"] = arr });
        }
        catch (Exception ex)
        {
            await ServeJsonAsync(ctx, new JsonObject { ["error"] = "请求失败：" + ex.Message });
        }
    }

    /// <summary>读取请求体文本</summary>
    private static async Task<string> ReadBodyAsync(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private async Task ServeJsonAsync(HttpListenerContext ctx, JsonNode node)
    {
        await WriteTextAsync(ctx, node.ToJsonString(), "application/json; charset=utf-8");
    }

    private static async Task WriteTextAsync(HttpListenerContext ctx, string text, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    // ===================== 评测页（Eval） =====================

    /// <summary>评测数据文件（场景 + 分数记录，JSON）</summary>
    private static string EvalFilePath => Path.Combine(AppContext.BaseDirectory, "eval_data.json");

    /// <summary>内置默认评分提示词模板（{Requirement}/{Reply} 占位）</summary>
    private const string DefaultJudgePrompt =
        "你是一位 AI 对话质量评审。请评估【静静回复】对【测试要求】的完成度，从三个维度打分：\n" +
        "1. 工具使用：该调用工具（如查时间/查记录/画图）时是否正确调用（0~30 分）\n" +
        "2. 格式合规：回复结构是否合法、无明显格式错误（0~30 分）\n" +
        "3. 人设与内容：是否贴合静静的人设（外冷内热的小女仆、简体中文、语气自然）、是否满足要求（0~40 分）\n" +
        "只输出 JSON：{\"score\": 总分(0-100整数), \"comment\": \"一句话点评(指出扣分点)\"}，不要输出任何多余文字。\n" +
        "【测试要求】{Requirement}\n" +
        "【静静回复】{Reply}";

    /// <summary>评测 API：GET 状态 / PUT 评分配置 / POST 场景 / DELETE 场景 / POST 运行</summary>
    private async Task HandleEvalAsync(HttpListenerContext ctx, string path)
    {
        var method = ctx.Request.HttpMethod;
        if (method == "GET" && path == "/api/eval")
        {
            await ServeJsonAsync(ctx, BuildEvalState());
            return;
        }
        if (method == "PUT" && path == "/api/eval/config")
        {
            await SaveConfigUpdatesAsync(ctx);   // 评分 LLM 配置写 appsettings（Bot.Eval.*）
            return;
        }
        if (method == "POST" && path == "/api/eval/scenarios")
        {
            await AddEvalScenarioAsync(ctx);
            return;
        }
        if (method == "PUT" && path == "/api/eval/scenarios")
        {
            await UpdateEvalScenarioAsync(ctx);
            return;
        }
        if (method == "DELETE" && path.StartsWith("/api/eval/scenarios/", StringComparison.OrdinalIgnoreCase))
        {
            var id = path[(path.LastIndexOf('/') + 1)..];
            lock (_evalLock)
            {
                var data = LoadEvalData();
                if (data["scenarios"] is JsonArray arr)
                {
                    for (int i = arr.Count - 1; i >= 0; i--)
                        if (arr[i]?["id"]?.GetValue<string>() == id) arr.RemoveAt(i);
                }
                SaveEvalData(data);
            }
            await ServeJsonAsync(ctx, new JsonObject { ["ok"] = true });
            return;
        }
        if (method == "POST" && path == "/api/eval/run")
        {
            await RunEvalAsync(ctx);
            return;
        }
        ctx.Response.StatusCode = 405;
        await WriteTextAsync(ctx, "{\"error\":\"method not allowed\"}", "application/json");
    }

    /// <summary>读取评测数据文件（无文件返回空结构）</summary>
    private static JsonObject LoadEvalData()
    {
        try
        {
            if (File.Exists(EvalFilePath))
                return JsonNode.Parse(File.ReadAllText(EvalFilePath)) as JsonObject ?? new JsonObject { ["scenarios"] = new JsonArray() };
        }
        catch { /* 文件损坏按空处理 */ }
        return new JsonObject { ["scenarios"] = new JsonArray() };
    }

    private static void SaveEvalData(JsonObject data)
    {
        File.WriteAllText(EvalFilePath, data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>评分 LLM 配置 + 场景列表（GET /api/eval）</summary>
    private JsonObject BuildEvalState()
    {
        JsonObject data;
        lock (_evalLock) data = LoadEvalData();
        return new JsonObject
        {
            ["config"] = new JsonObject
            {
                ["baseUrl"] = _config["Bot:Eval:BaseUrl"] ?? "",
                ["apiKey"] = _config["Bot:Eval:ApiKey"] ?? "",
                ["model"] = _config["Bot:Eval:Model"] ?? "",
                ["judgePrompt"] = _config["Bot:Eval:JudgePrompt"] ?? "",
            },
            ["scenarios"] = (data["scenarios"] as JsonArray)?.DeepClone() ?? new JsonArray(),
        };
    }

    private async Task AddEvalScenarioAsync(HttpListenerContext ctx)
    {
        var body = JsonNode.Parse(await ReadBodyAsync(ctx)) as JsonObject;
        var name = body?["name"]?.GetValue<string>()?.Trim();
        var requirement = body?["requirement"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = "未命名场景";
        lock (_evalLock)
        {
            var data = LoadEvalData();
            var arr = data["scenarios"] as JsonArray ?? new JsonArray();
            arr.Add(new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("N")[..12],
                ["name"] = name,
                ["requirement"] = requirement ?? "",
                ["reply"] = "",
                ["comment"] = "",
                ["lastScore"] = null,
                ["curScore"] = null,
                ["formatOk"] = false,
                ["updatedAt"] = "",
            });
            data["scenarios"] = arr;
            SaveEvalData(data);
        }
        await ServeJsonAsync(ctx, BuildEvalState());
    }

    private async Task UpdateEvalScenarioAsync(HttpListenerContext ctx)
    {
        var body = JsonNode.Parse(await ReadBodyAsync(ctx)) as JsonObject;
        var id = body?["id"]?.GetValue<string>();
        lock (_evalLock)
        {
            var data = LoadEvalData();
            var arr = data["scenarios"] as JsonArray;
            var sc = arr?.OfType<JsonObject>().FirstOrDefault(s => s["id"]?.GetValue<string>() == id);
            if (sc is not null)
            {
                if (body?["name"]?.GetValue<string>() is { } name) sc["name"] = name.Trim();
                if (body?["requirement"]?.GetValue<string>() is { } req) sc["requirement"] = req.Trim();
                SaveEvalData(data);
            }
        }
        await ServeJsonAsync(ctx, BuildEvalState());
    }

    /// <summary>运行评测：生成静静回复 → 评分 → 更新场景分数（POST /api/eval/run）</summary>
    private async Task RunEvalAsync(HttpListenerContext ctx)
    {
        var body = JsonNode.Parse(await ReadBodyAsync(ctx)) as JsonObject;
        var id = body?["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id)) id = null;   // 空/缺省 = 运行全部
        var results = new JsonArray();
        List<JsonObject> targets;
        lock (_evalLock)
        {
            var data = LoadEvalData();
            var arr = data["scenarios"] as JsonArray ?? new JsonArray();
            targets = arr.OfType<JsonObject>()
                .Where(s => id is null || s["id"]?.GetValue<string>() == id)
                .ToList();
        }
        foreach (var sc in targets)
        {
            var req = sc["requirement"]?.GetValue<string>() ?? "";
            var result = await RunSingleEvalAsync(req, CancellationToken.None);
            lock (_evalLock)
            {
                var data = LoadEvalData();
                var arr = data["scenarios"] as JsonArray;
                var target = arr?.OfType<JsonObject>().FirstOrDefault(s => s["id"]?.GetValue<string>() == sc["id"]?.GetValue<string>());
                if (target is not null)
                {
                    target["lastScore"] = target["curScore"]?.DeepClone();   // 原本次 → 上次（须克隆，节点不能直接移动）
                    target["curScore"] = result.Score is null ? null : JsonValue.Create(result.Score);
                    target["reply"] = result.Reply;
                    target["comment"] = result.Comment ?? "";
                    target["formatOk"] = result.FormatOk;
                    target["updatedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    SaveEvalData(data);
                }
            }
            results.Add(new JsonObject
            {
                ["id"] = sc["id"]?.GetValue<string>(),
                ["name"] = sc["name"]?.GetValue<string>(),
                ["score"] = result.Score,
                ["reply"] = result.Reply,
                ["comment"] = result.Comment,
                ["formatOk"] = result.FormatOk,
                ["error"] = result.Error,
                ["debug"] = result.Debug,
            });
        }
        await ServeJsonAsync(ctx, new JsonObject { ["ok"] = true, ["results"] = results, ["state"] = BuildEvalState() });
    }

    private sealed record EvalOutcome(int? Score, string Reply, string? Comment, bool FormatOk, string? Error, JsonObject? Debug);

    /// <summary>LLM 调用结果：正文 + 请求体/响应体原文（Debug 侧栏展示用）</summary>
    private sealed record LlmCallResult(string Content, string RequestJson, string ResponseJson, string? ToolCalls, List<ToolCall>? RawToolCalls);

    /// <summary>单场景：主 LLM 生成回复 → 评分 LLM 打分；Debug 收集两路请求/响应原文</summary>
    private async Task<EvalOutcome> RunSingleEvalAsync(string requirement, CancellationToken ct)
    {
        try
        {
            // 1) 生成回复：规划轮（开关开时）→ 主 LLM（带 tools）
            var (replyRaw, genReq, genResp, planReq, planResp) = await GenerateEvalReplyAsync(requirement, ct);
            // 2) 自动格式判定：能否解析出 {"reply":...} JSON（工具调用响应视为合规）
            var (replyText, formatOk) = ExtractEvalReply(replyRaw);
            if (replyRaw.StartsWith("[工具调用]", StringComparison.Ordinal)) formatOk = true;
            // 3) 评分
            var (score, comment, judgeReq, judgeResp) = await JudgeEvalAsync(requirement, replyText, ct);
            var debug = new JsonObject
            {
                ["genRequest"] = genReq,
                ["genResponse"] = genResp,
                ["judgeRequest"] = judgeReq,
                ["judgeResponse"] = judgeResp,
            };
            if (planReq is not null) debug["planRequest"] = planReq;
            if (planResp is not null) debug["planResponse"] = planResp;
            return new EvalOutcome(score, replyText, comment, formatOk, null, debug);
        }
        catch (Exception ex)
        {
            return new EvalOutcome(null, "", null, false, ex.Message, null);
        }
    }

    /// <summary>
    /// 生成静静回复（主 LLM，与生产完整回复循环同构）：
    /// 规划轮（开关开时）→ 输出回复数组 → 逐项收集回复 + 执行内嵌工具调用（沙箱）→ 结果回填 →
    /// 继续下一轮（LLM 汇报工具结果）→ 直到无工具调用或达轮次上限。
    /// 返回 (全部轮次回复拼接, 最后请求, 最后响应, 规划请求?, 规划响应?)。
    /// </summary>
    private async Task<(string Content, string RequestJson, string ResponseJson, string? PlanRequest, string? PlanResponse)> GenerateEvalReplyAsync(string requirement, CancellationToken ct)
    {
        var baseUrl = (_config["Bot:Llm:BaseUrl"] ?? "").TrimEnd('/');
        var apiKey = _config["Bot:Llm:ApiKey"] ?? "";
        var model = _config["Bot:Llm:Model"] ?? "";
        var role = _config["Bot:Prompt:GlobalPrePromptRole"] ?? "system";
        var pre = _config["Bot:Prompt:GlobalPrePrompt"] ?? "";
        var post = _config["Bot:Prompt:GlobalPostPrompt"] ?? "";
        var sys = string.Join("\n\n", new[] { pre, post }.Where(s => !string.IsNullOrWhiteSpace(s)));

        // 基础消息：人设 + 当前消息
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = string.IsNullOrWhiteSpace(role) ? "system" : role, ["content"] = sys },
            new JsonObject { ["role"] = "user", ["content"] = requirement },
        };

        // ① 规划轮（受 Planning.Enabled 开关影响）
        string? planText = null;
        string? planReq = null, planResp = null;
        if (string.Equals(_config["Bot:Planning:Enabled"], "true", StringComparison.OrdinalIgnoreCase))
        {
            var planPrompt = BuildEvalPlanningPrompt(requirement);
            var planMsgs = new JsonArray
            {
                new JsonObject { ["role"] = string.IsNullOrWhiteSpace(role) ? "system" : role, ["content"] = sys },
                new JsonObject { ["role"] = "user", ["content"] = planPrompt },
            };
            var plan = await CallLlmAsync(baseUrl, apiKey, model, planMsgs, ct);
            planReq = plan.RequestJson;
            planResp = plan.ResponseJson;
            if (!string.IsNullOrWhiteSpace(plan.Content))
            {
                planText = plan.Content.Trim();
                if (int.TryParse(_config["Bot:Planning:MaxChars"], out var maxChars) && maxChars > 0 && planText.Length > maxChars)
                    planText = planText[..maxChars];
            }
            if (planText is not null)
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = $"【你的规划】\n{planText}\n\n请按照你的规划执行。" });
        }

        // 格式指令（数组；DisableReasoning 分支）
        if (!int.TryParse(_config["Bot:Reply:MaxRepliesPerTurn"], out var maxItems) || maxItems <= 0) maxItems = 4;
        var disableReasoning = string.Equals(_config["Bot:Llm:DisableReasoning"], "true", StringComparison.OrdinalIgnoreCase);
        var arrayRule = $"请以 JSON 数组格式回复，数组的每一项代表一条要发送的消息：" +
            $"[{{\"reply\":\"第一句\"}},{{\"reply\":\"第二句\",\"tool_calls\":[{{\"type\":\"function\",\"function\":{{\"name\":\"get_time\",\"arguments\":\"{{}}\"}}}}]}}]，最多 {maxItems} 项。" +
            "需要调用工具时在对应项加 tool_calls 字段；不调用工具则省略。";
        var formatInstr = disableReasoning
            ? arrayRule + "只输出 JSON 数组本身，不要输出任何思考过程、标记、markdown 代码块或多余文字。"
            : "先输出你的思考过程（cot，仅供内部推理，用户看不到）；思考结束后输出标记 ```END_REASONING```；" +
              "标记之后只输出：" + arrayRule + "标记之前不要输出任何正文，标记之后不要输出任何额外文字。";

        // 工具定义（主人视角全量）+ 评测虚拟消息（IsOwner=true，防副作用沙箱执行）
        var tools = _tools.BuildToolDefinitions();
        var evalMsg = new IncomingMessage(0, 0, _options.OwnerId, "主人", 0, true, requirement, new JsonArray(),
            "eval:" + Math.Abs(requirement.GetHashCode() & 0xFFFFF), true);
        var toolCtx = new ToolContext(evalMsg);

        // ② 完整回复循环：输出数组 → 收集回复 + 执行工具（沙箱）→ 回填 → 继续
        var allReplies = new List<string>();
        string lastRequest = "", lastResponse = "";
        int toolRounds = 0;
        while (true)
        {
            var reqMsgs = new JsonArray();
            foreach (var m in messages) reqMsgs.Add(m.DeepClone());
            reqMsgs.Add(new JsonObject { ["role"] = "user", ["content"] = formatInstr });
            var result = await CallLlmAsync(baseUrl, apiKey, model, reqMsgs, ct, tools);
            lastRequest = result.RequestJson;
            lastResponse = result.ResponseJson;

            // 本轮回复文本（数组拼接 / 工具调用摘要）
            var (replyText, _) = ExtractEvalReply(result.Content);
            if (!string.IsNullOrWhiteSpace(replyText)) allReplies.Add(replyText);

            // 收集工具调用：数组内嵌 + 直接 tool_calls 两种形态
            var calls = new List<(string Id, string Name, string Args)>();
            calls.AddRange(ExtractEvalArrayToolCalls(result.Content));
            if (result.RawToolCalls is not null)
                calls.AddRange(result.RawToolCalls.Select(t => (t.Id, t.Name, t.Arguments)));

            if (calls.Count == 0) break;   // 无工具调用 → 回复结束

            // assistant 消息带 tool_calls 回传 + 执行（沙箱）→ tool 消息回填
            var assistant = new JsonObject { ["role"] = "assistant", ["content"] = null };
            var tcArr = new JsonArray();
            foreach (var c in calls)
                tcArr.Add(new JsonObject { ["id"] = c.Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Args } });
            assistant["tool_calls"] = tcArr;
            messages.Add(assistant);
            foreach (var c in calls)
            {
                var output = await ExecuteEvalToolAsync(c.Name, c.Args, toolCtx, ct);
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = c.Id, ["content"] = output });
            }
            toolRounds++;
            if (toolRounds >= _options.Llm.MaxToolRounds) break;
        }

        return (string.Join("\n", allReplies), lastRequest, lastResponse, planReq, planResp);
    }

    /// <summary>评测工具沙箱：仅放行无副作用只读工具；其余返回拦截说明（防止测试时真实发消息/生图/写记忆）</summary>
    private static readonly HashSet<string> EvalSafeTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "get_time", "search_memory", "get_chat_history", "get_friend_list", "browse_web",
    };

    private async Task<string> ExecuteEvalToolAsync(string name, string argsJson, ToolContext ctx, CancellationToken ct)
    {
        if (!EvalSafeTools.Contains(name))
            return $"[评测沙箱] 工具 {name} 在评测环境不执行（防止副作用；实际对话中可正常执行）。";
        try
        {
            return await _tools.ExecuteAsync(name, argsJson, ctx, ct) ?? $"工具 {name} 不存在";
        }
        catch (Exception ex)
        {
            return $"工具执行出错：{ex.Message}";
        }
    }

    /// <summary>从回复数组中提取内嵌的 tool_calls（新数组格式）</summary>
    private static List<(string Id, string Name, string Args)> ExtractEvalArrayToolCalls(string content)
    {
        var result = new List<(string, string, string)>();
        var s = content?.Trim();
        if (string.IsNullOrWhiteSpace(s)) return result;
        var start = s.IndexOf('[');
        var end = s.LastIndexOf(']');
        if (start < 0 || end <= start) return result;
        try
        {
            var arr = JsonNode.Parse(s[start..(end + 1)]) as JsonArray;
            if (arr is null) return result;
            foreach (var node in arr.OfType<JsonObject>())
            {
                if (node["tool_calls"] is not JsonArray tcs) continue;
                foreach (var tc in tcs.OfType<JsonObject>())
                {
                    var fn = tc["function"] as JsonObject;
                    var name = fn?["name"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    // arguments 兼容：字符串（标准）或对象（LLM 不规范输出 → 序列化成 JSON 字符串）
                    var argsNode = fn?["arguments"];
                    string argsStr;
                    if (argsNode is JsonValue av && av.TryGetValue<string>(out var asStr)) argsStr = asStr;
                    else if (argsNode is JsonObject aobj) argsStr = aobj.ToJsonString();
                    else argsStr = "{}";
                    result.Add((tc["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"), name, argsStr));
                }
            }
        }
        catch { /* 解析失败返回空 */ }
        return result;
    }

    /// <summary>评测规划轮提示词（Bot:Prompt:PlanningPrompt 模板，空=内置默认）</summary>
    private string BuildEvalPlanningPrompt(string requirement)
    {
        var template = _config["Bot:Prompt:PlanningPrompt"];
        if (string.IsNullOrWhiteSpace(template))
        {
            template = DefaultEvalPlanningPrompt;
        }
        return template.Replace("{Tools}", BuildToolsSummaryText()).Replace("{UserText}", requirement);
    }

    private const string DefaultEvalPlanningPrompt =
        "在正式回复前，请先做一次回复规划（这是你的内部规划，用于理清思路，用户不会直接看到）。\n" +
        "【用户消息】{UserText}\n" +
        "【你可用的工具】\n{Tools}" +
        "请规划：\n" +
        "1. 是否需要调用工具？如果需要，先调用哪些、为什么；不需要则简单说明。\n" +
        "2. 回复的要点、结构和语气（结合当前场景与你的身份）。\n" +
        "输出 3~5 行简洁的普通文字规划即可。注意：这是内部规划，不要输出正式回复内容，" +
        "不要输出 JSON、代码块或其他任何结构化格式标记，直接用普通文字写规划。";

    /// <summary>工具摘要文本（规划轮提示词用，与生产 BuildToolsSummary 同构）</summary>
    private string BuildToolsSummaryText()
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            foreach (var t in _tools.BuildToolDefinitions().OfType<JsonObject>())
            {
                var fn = t["function"] as JsonObject;
                var name = fn?["name"]?.GetValue<string>();
                var desc = fn?["description"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var shortDesc = string.IsNullOrWhiteSpace(desc) ? "" : (desc.Length > 80 ? desc[..80] + "…" : desc);
                sb.Append("- ").Append(name).Append("：").Append(shortDesc).Append('\n');
            }
        }
        catch { /* 摘要失败不影响评测 */ }
        return sb.ToString();
    }

    /// <summary>评分 LLM（Bot.Eval 配置，留空复用主 LLM）</summary>
    private async Task<(int? Score, string? Comment, string RequestJson, string ResponseJson)> JudgeEvalAsync(string requirement, string reply, CancellationToken ct)
    {
        var baseUrl = (_config["Bot:Eval:BaseUrl"] ?? "").TrimEnd('/');
        var apiKey = _config["Bot:Eval:ApiKey"] ?? "";
        var model = _config["Bot:Eval:Model"] ?? "";
        if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = (_config["Bot:Llm:BaseUrl"] ?? "").TrimEnd('/');
        if (string.IsNullOrWhiteSpace(apiKey)) apiKey = _config["Bot:Llm:ApiKey"] ?? "";
        if (string.IsNullOrWhiteSpace(model)) model = _config["Bot:Llm:Model"] ?? "";

        var judge = _config["Bot:Eval:JudgePrompt"] ?? "";
        if (string.IsNullOrWhiteSpace(judge)) judge = DefaultJudgePrompt;
        var prompt = judge.Replace("{Requirement}", requirement).Replace("{Reply}", reply);

        var messages = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = prompt } };
        var result = await CallLlmAsync(baseUrl, apiKey, model, messages, ct);
        var raw = result.Content;

        // 容错解析 {"score":N,"comment":"..."}
        var m = System.Text.RegularExpressions.Regex.Match(raw, "\"score\"\\s*:\\s*(\\d+)");
        var score = m.Success ? int.Parse(m.Groups[1].Value) : (int?)null;
        var c = System.Text.RegularExpressions.Regex.Match(raw, "\"comment\"\\s*:\\s*\"([^\"]*)\"");
        return (score, c.Success ? c.Groups[1].Value : null, result.RequestJson, result.ResponseJson);
    }

    /// <summary>通用 LLM 调用（chat/completions，非流式），返回正文 + 请求/响应原文</summary>
    private async Task<LlmCallResult> CallLlmAsync(string baseUrl, string apiKey, string model, JsonArray messages,
                                                   CancellationToken ct, JsonArray? tools = null)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["temperature"] = 0.7,
            ["max_tokens"] = 2000,
            ["stream"] = false,
        };
        // tools 必须克隆：循环内复用同一 tools 定义，直接赋值会因"节点已有父"抛异常
        if (tools is not null && tools.Count > 0) body["tools"] = tools.DeepClone();
        // 关闭思维链（与生产 ChatEngine.ResolveExtraBody 一致）：DisableReasoning=true 时注入 Payload 到请求体顶层
        if (string.Equals(_config["Bot:Llm:DisableReasoning"], "true", StringComparison.OrdinalIgnoreCase))
        {
            var payload = _config["Bot:Llm:DisableReasoningPayload"];
            if (!string.IsNullOrWhiteSpace(payload))
            {
                try
                {
                    if (JsonNode.Parse(payload) is JsonObject extra)
                        foreach (var kv in extra) body[kv.Key] = kv.Value?.DeepClone();
                }
                catch { /* payload 格式错误则忽略 */ }
            }
        }
        var requestJson = body.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var reqPayload = body.ToJsonString();

        // 发送（网络抖动/连接复用被掐时重试 2 次；HttpRequestMessage 每次重建，StringContent 不可复用）
        HttpResponseMessage? resp = null;
        for (int attempt = 0; attempt < 3 && resp is null; attempt++)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions")
            {
                Content = new StringContent(reqPayload, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(apiKey))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            try
            {
                resp = await _evalHttp.SendAsync(req, ct);
            }
            catch (Exception) when (attempt < 2)
            {
                await Task.Delay(1000 * (attempt + 1), ct);
            }
        }
        if (resp is null) throw new HttpRequestException("LLM 请求发送失败（网络异常，重试后仍失败）");
        var responseJson = await resp.Content.ReadAsStringAsync(ct);
        using var resp2 = resp;
        resp.EnsureSuccessStatusCode();
        var node = JsonNode.Parse(responseJson);
        var msg = node?["choices"]?[0]?["message"];
        var content = msg?["content"]?.GetValue<string>() ?? "";

        // 工具调用摘要：LLM 选择调用工具时展示（评测环境不真正执行，仅看选择是否正确）
        string? toolSummary = null;
        var rawCalls = new List<ToolCall>();
        if (msg?["tool_calls"] is JsonArray tcs && tcs.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var tc in tcs.OfType<JsonObject>())
            {
                var fn = tc["function"] as JsonObject;
                var name = fn?["name"]?.GetValue<string>() ?? "?";
                // arguments 兼容：字符串或对象（LLM 不规范输出）
                var argsNode = fn?["arguments"];
                string args;
                if (argsNode is JsonValue av && av.TryGetValue<string>(out var asStr)) args = asStr;
                else if (argsNode is JsonObject aobj) args = aobj.ToJsonString();
                else args = "";
                sb.AppendLine($"{name}({args})");
                if (!string.IsNullOrWhiteSpace(name))
                    rawCalls.Add(new ToolCall(tc["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"), name, args));
            }
            toolSummary = sb.ToString().TrimEnd();
        }
        return new LlmCallResult(content, requestJson, responseJson, toolSummary, rawCalls.Count > 0 ? rawCalls : null);
    }

    /// <summary>从 LLM 原始输出提取回复文本（新数组格式：拼接所有项的 reply + 工具调用摘要）+ 格式是否合规</summary>
    private static (string Text, bool FormatOk) ExtractEvalReply(string raw)
    {
        var s = raw.Trim();
        // 分离 cot：若含 END_REASONING 标记，只取标记之后的部分（避免思考过程中的 [ ] 干扰数组定位）
        const string mark = "```END_REASONING```";
        var markIdx = s.LastIndexOf(mark, StringComparison.Ordinal);
        if (markIdx >= 0) s = s[(markIdx + mark.Length)..].Trim();
        // 优先解析数组（多轮回复新格式）
        var arrStart = s.IndexOf('[');
        var arrEnd = s.LastIndexOf(']');
        if (arrStart >= 0 && arrEnd > arrStart)
        {
            try
            {
                var arr = JsonNode.Parse(s[arrStart..(arrEnd + 1)]) as JsonArray;
                if (arr is { Count: > 0 })
                {
                    var parts = new List<string>();
                    var formatOk = true;
                    foreach (var node in arr)
                    {
                        if (node is not JsonObject obj) continue;
                        var reply = obj["reply"]?.GetValue<string>()?.Trim();
                        var calls = obj["tool_calls"] as JsonArray;
                        if (calls is { Count: > 0 })
                        {
                            foreach (var tc in calls.OfType<JsonObject>())
                            {
                                var fn = tc["function"] as JsonObject;
                                var nm = fn?["name"]?.GetValue<string>();
                                var ag = fn?["arguments"]?.GetValue<string>();
                                parts.Add($"[工具调用] {nm}({ag})");
                            }
                            if (!string.IsNullOrWhiteSpace(reply)) parts.Add(reply);
                        }
                        else if (!string.IsNullOrWhiteSpace(reply)) parts.Add(reply);
                        else formatOk = false;
                    }
                    if (parts.Count > 0) return (string.Join("\n", parts), formatOk);
                }
            }
            catch { /* 数组解析失败走兜底 */ }
        }
        // 兜底：旧对象格式 / 纯文本
        var jsonStart = s.IndexOf('{');
        var jsonEnd = s.LastIndexOf('}');
        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            try
            {
                var obj = JsonNode.Parse(s[jsonStart..(jsonEnd + 1)]) as JsonObject;
                if (obj?["reply"]?.GetValue<string>() is { } reply)
                    return (reply.Trim(), true);
            }
            catch { /* 解析失败走兜底 */ }
        }
        return (s.Length > 500 ? s[..500] + "…" : s, false);
    }
}
