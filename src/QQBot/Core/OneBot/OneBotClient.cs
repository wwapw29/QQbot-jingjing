using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using QQBot.Core.Options;

namespace QQBot.Core.OneBot;

/// <summary>
/// OneBot 11 客户端：正向 WebSocket 接收事件 + HTTP API 发送消息。
/// 协议细节全部封装在这里，业务层不感知。
/// 含消息去重（NapCat 可能对同一条消息推送多次事件）。
/// </summary>
public sealed class OneBotClient
{
    private readonly BotOptions _options;
    private readonly ILogger<OneBotClient> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>消息去重：key = 会话+messageId → 上次处理时间</summary>
    private readonly ConcurrentDictionary<string, DateTime> _recentMessages = new();
    private static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(30);

    /// <summary>收到一条协议事件（业务层订阅）</summary>
    public event Func<OneBotEvent, Task>? OnEvent;

    private ClientWebSocket? _activeWs;   // 当前 WS 连接（供面板触发重连）

    public OneBotClient(BotOptions options, ILogger<OneBotClient> logger)
    {
        _options = options;
        _logger = logger;
        // HttpBase/AccessToken 每次请求动态解析（支持面板热更新），不使用 BaseAddress/共享头
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>动态拼 OneBot HTTP API 完整 URL（HttpBase 热更新后立即生效）</summary>
    private Uri ApiUrl(string action) => new Uri(_options.HttpBase.TrimEnd('/') + "/" + action.TrimStart('/'));

    /// <summary>创建带动态 Authorization 的请求（每请求头，避免共享 DefaultRequestHeaders 并发竞态）</summary>
    private HttpRequestMessage BuildRequest(HttpMethod method, string action, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, ApiUrl(action)) { Content = content };
        if (!string.IsNullOrEmpty(_options.AccessToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
        return req;
    }

    /// <summary>带动态 URL/Token 的 JSON 请求（统一入口）</summary>
    private async Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string action, JsonNode? body, CancellationToken ct)
    {
        using var req = BuildRequest(method, action, body is null ? null : JsonContent.Create(body));
        return await _http.SendAsync(req, ct);
    }

    /// <summary>
    /// 连接正向 WebSocket 并循环接收事件；断线自动重连（指数退避）。
    /// 阻塞直到被取消。
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                if (!string.IsNullOrEmpty(_options.AccessToken))
                {
                    ws.Options.SetRequestHeader("Authorization", $"Bearer {_options.AccessToken}");
                }
                _logger.LogInformation("正在连接 NapCat WebSocket: {Url}", _options.WsUrl);
                await ws.ConnectAsync(new Uri(_options.WsUrl), ct);
                _logger.LogInformation("WebSocket 已连接 ✓");
                backoff = TimeSpan.FromSeconds(1);
                _activeWs = ws;   // 每次连接都读最新 _options.WsUrl（面板改地址后 Reconnect 即生效）

                await ReceiveLoopAsync(ws, ct);

                if (ReferenceEquals(_activeWs, ws)) _activeWs = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation("WebSocket 已停止（程序退出）");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WebSocket 连接异常，{Sec}s 后重连", backoff.TotalSeconds);
                try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
            }
        }
    }

    /// <summary>主动断开当前 WS（面板热更新 WsUrl 后调用；重连循环会自动用新地址）</summary>
    public void Reconnect()
    {
        try { _activeWs?.Abort(); } catch { }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                    return;
                }
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var json = Encoding.UTF8.GetString(ms.ToArray());
            HandlePayload(json);
        }
    }

    private void HandlePayload(string json)
    {
        try
        {
            var evt = OneBotEventParser.Parse(JsonNode.Parse(json));
            if (evt is null) return;

            // 只关心消息事件（后续阶段再扩展 notice/request）
            if (evt.PostType != "message") return;

            // 消息去重：NapCat 可能对同一条消息推送多次（如 30 秒内重复 message_id 只处理一次）
            var key = $"{evt.MessageType}:{evt.GroupId}:{evt.UserId}:{evt.MessageId}";
            var now = DateTime.UtcNow;
            if (_recentMessages.TryGetValue(key, out var last) && now - last < DedupWindow)
            {
                _logger.LogDebug("丢弃重复消息事件：{Key}", key);
                return;
            }
            _recentMessages[key] = now;
            if (_recentMessages.Count > 2000)
            {
                foreach (var (k, t) in _recentMessages.Where(p => now - p.Value > DedupWindow))
                {
                    _recentMessages.TryRemove(k, out _);
                }
            }

            // 异步分发，不阻塞接收循环；必须捕获异常——fire-and-forget 的未观察异常会静默丢失（无日志无回复）
            _ = Task.Run(async () =>
            {
                try { await (OnEvent?.Invoke(evt) ?? Task.CompletedTask); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "消息处理异常（type={Type}, uid={Uid}）", evt.MessageType, evt.UserId);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "解析事件失败: {Json}", json[..Math.Min(json.Length, 500)]);
        }
    }

    // ---------------- HTTP 发送 ----------------

    /// <summary>发私聊消息（支持多段：文本/图片/引用）</summary>
    public async Task<bool> SendPrivateMessageAsync(long userId, IEnumerable<JsonNode> segments, CancellationToken ct = default)
        => await PostAsync("send_private_msg", new JsonObject
        {
            ["user_id"] = userId,
            ["message"] = new JsonArray(segments.Select(s => (JsonNode)s.DeepClone()).ToArray())
        }, ct);

    /// <summary>发群消息</summary>
    public async Task<bool> SendGroupMessageAsync(long groupId, IEnumerable<JsonNode> segments, CancellationToken ct = default)
        => await PostAsync("send_group_msg", new JsonObject
        {
            ["group_id"] = groupId,
            ["message"] = new JsonArray(segments.Select(s => (JsonNode)s.DeepClone()).ToArray())
        }, ct);

    /// <summary>获取登录信息（验证连通性用）</summary>
    public async Task<JsonNode?> GetLoginInfoAsync(CancellationToken ct = default)
        => await GetAsync("get_login_info", ct);

    /// <summary>获取静静的好友列表（get_friend_list；发起私聊前的好友检查用）</summary>
    public async Task<List<(long UserId, string? Nickname)>> GetFriendListAsync(CancellationToken ct = default)
    {
        try
        {
            await _sendLock.WaitAsync(ct);
            try
            {
                var resp = await SendJsonAsync(HttpMethod.Post, "get_friend_list", new JsonObject(), ct);
                resp.EnsureSuccessStatusCode();
                var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (node?["retcode"]?.GetValue<int>() != 0) return [];
                var arr = node?["data"] as JsonArray;
                var list = new List<(long, string?)>();
                if (arr is not null)
                {
                    foreach (var f in arr.OfType<JsonObject>())
                    {
                        var uid = f["user_id"]?.GetValue<long>() ?? 0;
                        if (uid > 0) list.Add((uid, f["nickname"]?.GetValue<string>()));
                    }
                }
                return list;
            }
            finally { _sendLock.Release(); }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取好友列表失败");
            return [];
        }
    }

    /// <summary>
    /// 按消息 id 查询消息内容（get_msg）：返回被引用消息的文本/发送者/图片直链/完整消息段。
    /// 失败返回 null。Segments 含 forward（聊天记录分享）段时可供上层递归解析。
    /// </summary>
    public async Task<(string Text, string? Nickname, long UserId, List<string>? ImageUrls, JsonArray? Segments)?> GetMessageByIdAsync(long messageId, CancellationToken ct = default)
    {
        try
        {
            await _sendLock.WaitAsync(ct);
            try
            {
                var resp = await SendJsonAsync(HttpMethod.Post, "get_msg", new JsonObject { ["message_id"] = messageId }, ct);
                resp.EnsureSuccessStatusCode();
                var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (node?["retcode"]?.GetValue<int>() != 0) return null;
                var data = node?["data"] as JsonObject;
                if (data is null) return null;

                var sb = new System.Text.StringBuilder();
                var imageUrls = new List<string>();
                foreach (var seg in (data["message"] as JsonArray)?.OfType<JsonObject>() ?? [])
                {
                    if (seg["type"]?.GetValue<string>() == "text")
                        sb.Append(seg["data"]?["text"]?.GetValue<string>());
                    else if (seg["type"]?.GetValue<string>() == "image")
                    {
                        var url = seg["data"]?["url"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            imageUrls.Add(url);
                    }
                }
                var sender = data["sender"] as JsonObject;
                return (sb.ToString().Trim(), sender?["nickname"]?.GetValue<string>(),
                        data["user_id"]?.GetValue<long>() ?? 0,
                        imageUrls.Count > 0 ? imageUrls : null,
                        data["message"] as JsonArray);
            }
            finally { _sendLock.Release(); }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查询消息内容失败（id={Id}）", messageId);
            return null;
        }
    }

    /// <summary>
    /// 获取合并转发（聊天记录分享）内容（get_forward_msg）。
    /// 返回按顺序的 (发送者昵称, QQ, 消息段数组)；每条消息段里可能再含 forward 段（嵌套转发），由上层递归处理。
    /// 失败返回 null。
    /// </summary>
    public async Task<List<(string? Nickname, long UserId, JsonArray? Segments)>?> GetForwardMsgAsync(string id, CancellationToken ct = default)
    {
        try
        {
            await _sendLock.WaitAsync(ct);
            try
            {
                var resp = await SendJsonAsync(HttpMethod.Post, "get_forward_msg", new JsonObject { ["id"] = id }, ct);
                resp.EnsureSuccessStatusCode();
                var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (node?["retcode"]?.GetValue<int>() != 0) return null;
                var msgs = node?["data"]?["messages"] as JsonArray;
                if (msgs is null) return null;
                var list = new List<(string?, long, JsonArray?)>();
                foreach (var m in msgs.OfType<JsonObject>())
                {
                    var sender = m["sender"] as JsonObject;
                    // sender.user_id 优先，回退顶层 user_id（不同协议端字段位置略有差异）
                    var uid = sender?["user_id"]?.GetValue<long>()
                              ?? m["user_id"]?.GetValue<long>()
                              ?? 0;
                    list.Add((sender?["nickname"]?.GetValue<string>(), uid, m["message"] as JsonArray));
                }
                return list;
            }
            finally { _sendLock.Release(); }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取合并转发内容失败（id={Id}）", id);
            return null;
        }
    }

    /// <summary>
    /// 把指定消息原样转发给某个好友（NapCat forward_friend_single_msg，按 message_id，图片/表情等所有类型都保真）。
    /// 返回是否成功。
    /// </summary>
    public async Task<bool> ForwardFriendSingleMessageAsync(long userId, long messageId, CancellationToken ct = default)
    {
        try
        {
            await _sendLock.WaitAsync(ct);
            try
            {
                var resp = await SendJsonAsync(HttpMethod.Post, "forward_friend_single_msg", new JsonObject { ["user_id"] = userId, ["message_id"] = messageId }, ct);
                resp.EnsureSuccessStatusCode();
                var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
                var retcode = node?["retcode"]?.GetValue<int>();
                if (retcode != 0)
                {
                    _logger.LogWarning("forward_friend_single_msg 返回 retcode={Ret}: {Msg}", retcode, node?["message"]?.GetValue<string>());
                    return false;
                }
                return true;
            }
            finally { _sendLock.Release(); }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "转发消息失败（to={User}, id={Id}）", userId, messageId);
            return false;
        }
    }

    /// <summary>获取机器人加入的群列表（get_group_list，NapCat 支持）</summary>
    public async Task<List<(long GroupId, string Name)>> GetGroupListAsync(CancellationToken ct = default)
    {
        try
        {
            var node = await GetAsync("get_group_list", ct);
            var arr = node?["data"] as JsonArray;
            if (arr is null) return [];
            var list = new List<(long, string)>();
            foreach (var g in arr.OfType<JsonObject>())
            {
                var id = g["group_id"]?.GetValue<long>() ?? 0;
                var name = g["group_name"]?.GetValue<string>() ?? "";
                if (id > 0) list.Add((id, name));
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取群列表失败");
            return [];
        }
    }

    /// <summary>获取某群最近 count 条消息（get_group_msg_history，NapCat 支持；返回旧→新）</summary>
    public async Task<List<JsonObject>> GetGroupMessagesAsync(long groupId, int count, CancellationToken ct = default)
    {
        try
        {
            var body = new JsonObject { ["group_id"] = groupId, ["message_seq"] = 0, ["count"] = Math.Clamp(count, 1, 20) };
            var node = await PostForDataAsync("get_group_msg_history", body, ct);
            // NapCat 返回结构：data: { "messages": [...] }——消息列表在 data.messages（旧→新）
            var arr = (node?["data"] as JsonObject)?["messages"] as JsonArray;
            if (arr is null) return [];
            return arr.OfType<JsonObject>().ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取群消息历史失败：{Gid}", groupId);
            return [];
        }
    }

    /// <summary>POST 并返回响应 data 节点</summary>
    private async Task<JsonNode?> PostForDataAsync(string action, JsonNode body, CancellationToken ct)
    {
        try
        {
            await _sendLock.WaitAsync(ct);
            var resp = await SendJsonAsync(HttpMethod.Post, action, body, ct);
            resp.EnsureSuccessStatusCode();
            return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HTTP POST {Action} 失败", action);
            return null;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<JsonNode?> GetAsync(string action, CancellationToken ct)
    {
        try
        {
            var resp = await SendJsonAsync(HttpMethod.Get, action, null, ct);
            resp.EnsureSuccessStatusCode();
            return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HTTP GET {Action} 失败", action);
            return null;
        }
    }

    private async Task<bool> PostAsync(string action, JsonNode body, CancellationToken ct)
    {
        try
        {
            await _sendLock.WaitAsync(ct);
            var resp = await SendJsonAsync(HttpMethod.Post, action, body, ct);
            resp.EnsureSuccessStatusCode();
            var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            var retcode = node?["retcode"]?.GetValue<int>();
            if (retcode != 0)
            {
                _logger.LogWarning("{Action} 返回 retcode={Ret}: {Msg}", action, retcode, node?["message"]?.GetValue<string>());
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HTTP POST {Action} 失败", action);
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// 拉取群聊历史消息（NapCat get_group_msg_history，含群里所有人消息，与静静是否参与无关）。
    /// 返回 messages 数组节点（每条含 sender.nickname / user_id / message 段 / time）；失败返回 null。
    /// </summary>
    public async Task<JsonArray?> GetGroupMsgHistoryAsync(long groupId, int count, CancellationToken ct)
    {
        try
        {
            var body = new JsonObject { ["group_id"] = groupId, ["count"] = count };
            await _sendLock.WaitAsync(ct);
            try
            {
                var resp = await SendJsonAsync(HttpMethod.Post, "get_group_msg_history", body, ct);
                resp.EnsureSuccessStatusCode();
                var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
                var retcode = node?["retcode"]?.GetValue<int>();
                if (retcode != 0)
                {
                    _logger.LogWarning("get_group_msg_history 返回 retcode={Ret}: {Msg}", retcode, node?["message"]?.GetValue<string>());
                    return null;
                }
                return node?["data"]?["messages"] as JsonArray;
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "拉取群历史消息失败（group={Group}）", groupId);
            return null;
        }
    }
}
