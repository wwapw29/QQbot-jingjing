using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopPet.Pet;

/// <summary>后端返回的一条回复：文字 或 图片（图片走 dataUrl）</summary>
public sealed class PetReplyItem
{
    [JsonPropertyName("type")] public string Type { get; set; } = "text";
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("dataUrl")] public string? DataUrl { get; set; }
    [JsonPropertyName("caption")] public string? Caption { get; set; }

    public bool IsImage => string.Equals(Type, "image", StringComparison.OrdinalIgnoreCase);

    /// <summary>气泡里要显示的文字（图片取 caption）</summary>
    public string DisplayText => IsImage ? (Caption ?? "") : (Text ?? "");
}

/// <summary>一次对话的结果</summary>
public sealed record ChatOutcome(bool Ok, List<PetReplyItem> Replies, string? Error, long ElapsedMs);

/// <summary>一次 inbox 轮询的结果：她主动发来的消息 + 去重用的序号 + 后台配置版本</summary>
public sealed record InboxOutcome(bool Ok, List<PetReplyItem> Pushes, long LastSeq, string? Error, int ConfigVersion = 0, string ActivityMode = "",
    // 她最近一次工具调用（后端 /api/pet/inbox 顺带带回来的）。
    // 以前只有"主人正在等她回复"时才会去轮询 /api/pet/status，所以她自主活动里干了什么
    // （游戏模式主动截屏、自己翻文件）桌面上一点反应都没有 → 现在跟着心跳一起回来。
    string? Tool = null, long ToolSeq = 0);

/// <summary>后台下发的精灵配置</summary>
public sealed record RemoteConfig(bool Ok, int Version, PetConfig? Config, string? Error);

/// <summary>她此刻在调什么工具（等待回复期间桌面端据此播"绑定动作"）</summary>
public sealed record PetToolHint(string Tool, long Seq);

/// <summary>
/// 桌面端 → QQBot 的通道。她的"大脑"全在后端（人设/记忆/工具都是同一套），
/// 这里只负责把一句话送过去、把她要说的话拿回来。
/// </summary>
public sealed class PetBackend
{
    private readonly BackendConfig _cfg;
    private readonly HttpClient _http = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public PetBackend(BackendConfig cfg)
    {
        _cfg = cfg;
    }

    public string BaseUrl => (_cfg.BaseUrl ?? "").TrimEnd('/');

    /// <summary>健康检查：后端在不在、宠物接口开没开（托盘/启动时用）</summary>
    public async Task<(bool Ok, string Info)> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/pet/ping");
            Authorize(req);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return (false, $"HTTP {(int)resp.StatusCode}");
            var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var enabled = json.RootElement.TryGetProperty("enabled", out var e) && e.GetBoolean();
            var session = json.RootElement.TryGetProperty("sessionKey", out var s) ? s.GetString() : "?";
            return (enabled, enabled ? $"已连接（会话 {session}）" : "后端在，但宠物接口被关了");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>说一句话给她，等她回话（可能要等几秒到几十秒：她要规划/查记忆/调工具）</summary>
    public async Task<ChatOutcome> ChatAsync(string text, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var body = JsonSerializer.Serialize(new { text }, JsonOpts);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/pet/chat")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            Authorize(req);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, _cfg.TimeoutSeconds)));

            using var resp = await _http.SendAsync(req, timeoutCts.Token);
            var raw = await resp.Content.ReadAsStringAsync(timeoutCts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                var reason = TryReadError(raw) ?? $"HTTP {(int)resp.StatusCode}";
                return new ChatOutcome(false, new(), reason, sw.ElapsedMilliseconds);
            }

            var dto = JsonSerializer.Deserialize<ChatResponse>(raw, JsonOpts);
            if (dto is null) return new ChatOutcome(false, new(), "返回内容无法解析", sw.ElapsedMilliseconds);

            var replies = dto.Replies ?? new List<PetReplyItem>();
            PetLog.Info($"后端回复 {replies.Count} 条（{sw.ElapsedMilliseconds}ms）");
            return new ChatOutcome(true, replies, null, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ChatOutcome(false, new(), $"等太久了（超过 {_cfg.TimeoutSeconds} 秒）", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            PetLog.Warn($"后端请求失败：{ex.Message}");
            return new ChatOutcome(false, new(), ex.Message, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// 轻量状态查询（等待回复期间高频调）：只问"她刚调了什么工具"。
    /// ⚠️ 与 <see cref="PollInboxAsync"/> 不同：它**不标记在线、不取消息**，所以能 700ms 一次地打。
    /// </summary>
    public async Task<PetToolHint?> PetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/pet/status");
            Authorize(req);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("hint", out var hint)) return null;
            var tool = hint.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
            var seq = hint.TryGetProperty("seq", out var s) ? s.GetInt64() : 0;
            return new PetToolHint(tool, seq);
        }
        catch { return null; }     // 状态查询失败无所谓，别影响主流程
    }

    /// <summary>
    /// 轮询收件箱：既是"她有没有主动找我"的取件口，也是**心跳**（告诉后端精灵还在不在）。
    /// online=false 时后端会判定"人不在电脑前"，她主动说话就会改走 QQ（托盘躲起来时用）。
    /// </summary>
    public async Task<InboxOutcome> PollInboxAsync(long since, bool online, CancellationToken ct = default)
    {
        try
        {
            var url = $"{BaseUrl}/api/pet/inbox?since={since}&online={(online ? 1 : 0)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            Authorize(req);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return new InboxOutcome(false, new(), since, $"HTTP {(int)resp.StatusCode}");

            var dto = JsonSerializer.Deserialize<InboxResponse>(await resp.Content.ReadAsStringAsync(ct), JsonOpts);
            if (dto is null) return new InboxOutcome(false, new(), since, "返回无法解析");
            return new InboxOutcome(true, dto.Pushes ?? new(), dto.LastSeq, null, dto.ConfigVersion, dto.ActivityMode ?? "",
                                     dto.Tool, dto.ToolSeq);
        }
        catch (OperationCanceledException) { return new InboxOutcome(false, new(), since, "已取消"); }
        catch (Exception ex)
        {
            return new InboxOutcome(false, new(), since, ex.Message);
        }
    }

    /// <summary>
    /// 拉取后台配置（面板「桌面精灵 → 精灵外观与行为」里改的那份）。
    /// 心跳发现版本变了才来拉，不用每次传整份配置。
    /// </summary>
    public async Task<RemoteConfig> GetConfigAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/pet/config");
            Authorize(req);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return new RemoteConfig(false, 0, null, $"HTTP {(int)resp.StatusCode}");

            var dto = JsonSerializer.Deserialize<ConfigResponse>(await resp.Content.ReadAsStringAsync(ct), JsonOpts);
            if (dto is null) return new RemoteConfig(false, 0, null, "返回无法解析");
            if (dto.Config is null) return new RemoteConfig(true, dto.Version, null, null);   // 没存过 → 用本地

            var cfg = PetConfig.Parse(dto.Config.Value.GetRawText());
            return cfg is null
                ? new RemoteConfig(false, dto.Version, null, "配置解析失败")
                : new RemoteConfig(true, dto.Version, cfg, null);
        }
        catch (Exception ex)
        {
            return new RemoteConfig(false, 0, null, ex.Message);
        }
    }

    /// <summary>明确告诉后端"精灵不在了"（托盘躲起来 / 退出时调；不调也会因心跳超时被判离线）</summary>
    public async Task MarkOfflineAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/pet/presence")
            {
                Content = new StringContent("{\"online\":false}", Encoding.UTF8, "application/json"),
            };
            Authorize(req);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _http.SendAsync(req, cts.Token);
        }
        catch { /* 退出/隐藏时尽力而为，失败无所谓（有超时兜底） */ }
    }

    private void Authorize(HttpRequestMessage req)
    {
        if (!string.IsNullOrWhiteSpace(_cfg.Token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.Token.Trim());
    }

    private static string? TryReadError(string raw)
    {
        try
        {
            var doc = JsonDocument.Parse(raw);
            return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch { return null; }
    }

    private sealed class ChatResponse
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("replies")] public List<PetReplyItem>? Replies { get; set; }
        [JsonPropertyName("elapsedMs")] public long ElapsedMs { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }

    private sealed class InboxResponse
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("online")] public bool Online { get; set; }
        [JsonPropertyName("lastSeq")] public long LastSeq { get; set; }
        [JsonPropertyName("pushes")] public List<PetReplyItem>? Pushes { get; set; }
        [JsonPropertyName("configVersion")] public int ConfigVersion { get; set; }
        [JsonPropertyName("activityMode")] public string? ActivityMode { get; set; }
        [JsonPropertyName("tool")] public string? Tool { get; set; }
        [JsonPropertyName("toolSeq")] public long ToolSeq { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }

    private sealed class ConfigResponse
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("hasConfig")] public bool HasConfig { get; set; }
        [JsonPropertyName("config")] public JsonElement? Config { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }
}
