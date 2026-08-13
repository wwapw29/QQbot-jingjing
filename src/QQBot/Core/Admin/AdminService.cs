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
        ]),
        new("planning", "规划轮", "回复前先做一次规划（手动 cot）", ["Planning", "Enabled"],
        [
            new CfgItem(["Planning", "Visible"], "规划可见", "把规划内容也发给用户看（调试用）", "bool"),
            new CfgItem(["Planning", "MaxChars"], "规划长度上限", "超过截断", "int"),
        ]),
        new("vision", "识图", "图片下载压缩后用识图模型识别并注入描述", ["Vision", "Enabled"],
        [
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
        (["Prompt", "ContinuePrompt"], "续说提示词", "more=true 自发补充时的系统提示（留空=内置默认；改后即时生效）", "text"),
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
            if (path == "/api/llm/models") { await HandleModelListAsync(ctx); return; }
            if (path == "/api/memories" || path.StartsWith("/api/memories/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleMemoriesAsync(ctx, path);
                return;
            }
            if (path == "/api/entities") { await ServeJsonAsync(ctx, BuildEntities()); return; }

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
}
