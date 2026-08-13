using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using QQBot.Core.Options;

namespace QQBot.Core.Chat;

/// <summary>
/// 对话引擎：以标准 OpenAI 兼容格式调用 LLM（chat/completions）。
/// 特性：单次请求超时控制 + 失败自动重试（指数退避），超时/网络错误/5xx/429 可重试，4xx 不重试。
/// </summary>
public sealed class ChatEngine
{
    private readonly LlmOptions _llm;
    private readonly BotOptions _options;
    private readonly Microsoft.Extensions.Configuration.IConfiguration _config;
    private readonly ILogger<ChatEngine> _logger;
    private readonly HttpClient _http;

    public ChatEngine(LlmOptions llm, ILogger<ChatEngine> logger, BotOptions options,
                      Microsoft.Extensions.Configuration.IConfiguration config)
    {
        _llm = llm;
        _logger = logger;
        _options = options;
        _config = config;

        if (string.IsNullOrWhiteSpace(ResolveApiKey()))
        {
            _logger.LogWarning("未配置 LLM API Key（appsettings 的 Llm:ApiKey 或环境变量 LLM_API_KEY）");
        }

        // BaseUrl/ApiKey 每次请求动态解析（支持面板热更新）：
        // 不能依赖 HttpClient.BaseAddress 拼接 "/chat/completions"——以 "/" 开头的路径会替换掉 BaseAddress
        // 的整个路径（如 /api/v3 会丢失），DeepSeek 无路径碰巧没事，豆包这类带路径的会 404。
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };  // 兜底大超时；实际每次请求用更细的超时控制
    }

    /// <summary>调试模式开关：运行时读配置（面板热更新立即生效，无需重启）</summary>
    private bool Debug => string.Equals(_config["Bot:Debug"], "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>动态解析 API Key（优先配置，回退环境变量；热更新后立即生效）</summary>
    private string ResolveApiKey() =>
        !string.IsNullOrWhiteSpace(_llm.ApiKey)
            ? _llm.ApiKey!
            : Environment.GetEnvironmentVariable("LLM_API_KEY")
              ?? Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")
              ?? "";

    /// <summary>动态解析完整请求 URL（BaseUrl 热更新后立即生效）</summary>
    private string ResolveEndpoint() => _llm.BaseUrl.TrimEnd('/') + "/chat/completions";

    /// <summary>创建带动态 Authorization 的请求（HttpRequestMessage 头，避免共享 DefaultRequestHeaders 的并发竞态）</summary>
    private HttpRequestMessage BuildRequest(HttpMethod method, string url, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, url) { Content = content };
        var key = ResolveApiKey();
        if (!string.IsNullOrWhiteSpace(key))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        return req;
    }

    /// <summary>
    /// 带工具能力的对话（OpenAI tools 协议）：LLM 可返回 tool_calls，供 Agent 循环执行后回填。
    /// forceTool 非空时强制 LLM 调用该工具（tool_choice 指定函数），用于记忆总结等必须出结构化结果的场景。
    /// </summary>
    public async Task<ChatToolResult> CompleteWithToolsAsync(
        IReadOnlyList<ChatMessage> messages,
        JsonArray toolDefinitions,
        CancellationToken ct = default,
        JsonObject? extraBody = null,
        string? forceTool = null)
    {
        var body = new ChatCompletionRequest
        {
            Model = _llm.Model,
            Messages = [.. messages],
            Temperature = _llm.Temperature,
            MaxTokens = _llm.MaxTokens,
            Stream = false,
            Tools = JsonSerializer.SerializeToElement(toolDefinitions),
            ToolChoice = forceTool is null
                ? JsonValue.Create("auto")
                : JsonNode.Parse($"{{\"type\":\"function\",\"function\":{{\"name\":\"{forceTool}\"}}}}")
        };

        var json = MergeExtra(JsonSerializer.Serialize(body), ResolveExtraBody(extraBody));
        for (int attempt = 0; attempt <= _llm.MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                var backoff = TimeSpan.FromSeconds(1 << Math.Min(attempt - 1, 4));
                _logger.LogWarning("LLM 调用失败，{A}/{Max} 次重试，{S}s 后重试", attempt, _llm.MaxRetries, backoff.TotalSeconds);
                try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { return new ChatToolResult(null, null, []); }
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(_llm.TimeoutSeconds));

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                if (Debug) _logger.LogInformation("[DEBUG] LLM 请求（完整 Prompt）:\n{Json}", FormatJson(json));
                using var req = BuildRequest(HttpMethod.Post, ResolveEndpoint(), content);
                var resp = await _http.SendAsync(req, cts.Token);
                var bodyText = await resp.Content.ReadAsStringAsync(cts.Token);
                if (Debug) _logger.LogInformation("[DEBUG] LLM 响应:\n{Json}", Truncate(bodyText, 8000));

                if (resp.IsSuccessStatusCode)
                {
                    var parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(bodyText);
                    var msg = parsed?.Choices.FirstOrDefault()?.Message;
                    if (msg is null)
                    {
                        _logger.LogError("LLM 响应缺少 choices.message: {Body}", bodyText[..Math.Min(bodyText.Length, 300)]);
                        return new ChatToolResult(null, null, []);
                    }
                    var calls = msg.ToolCalls?
                        .Where(t => t.Function?.Name is not null)
                        .Select(t => new ToolCall(t.Id ?? Guid.NewGuid().ToString("N"), t.Function!.Name!, t.Function.Arguments ?? "{}"))
                        .ToList() ?? [];
                    // 稳定信息日志（Debug 无关）：LLM 回复内容摘要 + 工具调用列表，方便日常观察
                    var toolNote = calls.Count > 0 ? $" [调用工具: {string.Join(", ", calls.Select(c => c.Name))}]" : "";
                    _logger.LogInformation("LLM 回复：{Content}{ToolNote}", Truncate(msg.Content ?? "(无正文)", 300), toolNote);
                    return new ChatToolResult(msg.Content, msg.ReasoningContent, calls);
                }

                var code = (int)resp.StatusCode;
                if (code is 429 or >= 500)
                {
                    _logger.LogWarning("LLM 返回 {Code}，{A}/{Max} 次重试", code, attempt, _llm.MaxRetries);
                    continue;
                }
                _logger.LogError("LLM 调用失败 [{Code}] {Reason}: {Body}",
                    code, resp.ReasonPhrase, bodyText[..Math.Min(bodyText.Length, 500)]);
                return new ChatToolResult(null, null, []);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("LLM 请求超时（{T}s），{A}/{Max} 次重试", _llm.TimeoutSeconds, attempt, _llm.MaxRetries);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "LLM 网络错误，{A}/{Max} 次重试", attempt, _llm.MaxRetries);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "LLM 响应 JSON 解析失败");
                return new ChatToolResult(null, null, []);
            }
            catch (OperationCanceledException)
            {
                return new ChatToolResult(null, null, []);
            }
        }
        return new ChatToolResult(null, null, []);
    }

    /// <summary>
    /// 完整对话一次（非流式），带超时 + 自动重试。
    /// extraBody：附加到请求体的顶层字段（如关闭思维链的 chat_template_kwargs）。
    /// 失败返回 ChatResult(null, null)。
    /// </summary>
    public async Task<ChatResult> CompleteAsync(IReadOnlyList<ChatMessage> messages,
                                                CancellationToken ct = default,
                                                JsonObject? extraBody = null)
    {
        var request = new ChatCompletionRequest
        {
            Model = _llm.Model,
            Messages = [.. messages],
            Temperature = _llm.Temperature,
            MaxTokens = _llm.MaxTokens,
            Stream = false
        };
        var json = MergeExtra(JsonSerializer.Serialize(request), ResolveExtraBody(extraBody));

        for (int attempt = 0; attempt <= _llm.MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                var backoff = TimeSpan.FromSeconds(1 << Math.Min(attempt - 1, 4)); // 1s,2s,4s,8s,16s
                _logger.LogWarning("LLM 调用失败，{A}/{Max} 次重试，{S}s 后重试", attempt, _llm.MaxRetries, backoff.TotalSeconds);
                try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { return new ChatResult(null, null); }
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(_llm.TimeoutSeconds));

                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                if (Debug) _logger.LogInformation("[DEBUG] LLM 请求（完整 Prompt）:\n{Json}", FormatJson(json));
                using var req = BuildRequest(HttpMethod.Post, ResolveEndpoint(), content);
                var resp = await _http.SendAsync(req, cts.Token);
                var body = await resp.Content.ReadAsStringAsync(cts.Token);
                if (Debug) _logger.LogInformation("[DEBUG] LLM 响应:\n{Json}", Truncate(body, 8000));

                if (resp.IsSuccessStatusCode)
                {
                    var parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(body);
                    var msg = parsed?.Choices.FirstOrDefault()?.Message;
                    if (msg is null)
                    {
                        _logger.LogError("LLM 响应缺少 choices.message: {Body}", body[..Math.Min(body.Length, 300)]);
                        return new ChatResult(null, null);  // 结构异常，重试无用
                    }
                    _logger.LogInformation("LLM 回复：{Content}", Truncate(msg.Content ?? "(无正文)", 300));
                    return new ChatResult(msg.Content, msg.ReasoningContent);
                }

                // 是否值得重试：429 限流 / 5xx 服务端错误 可重试；4xx 参数类不重试
                var code = (int)resp.StatusCode;
                if (code is 429 or >= 500)
                {
                    _logger.LogWarning("LLM 返回 {Code}，{A}/{Max} 次重试", code, attempt, _llm.MaxRetries);
                    continue;
                }

                _logger.LogError("LLM 调用失败 [{Code}] {Reason}: {Body}",
                    code, resp.ReasonPhrase, body[..Math.Min(body.Length, 500)]);
                return new ChatResult(null, null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 请求超时（CancelAfter 触发）→ 重试
                _logger.LogWarning("LLM 请求超时（{T}s），{A}/{Max} 次重试", _llm.TimeoutSeconds, attempt, _llm.MaxRetries);
            }
            catch (HttpRequestException ex)
            {
                // 网络层错误（连接失败/解析失败）→ 重试
                _logger.LogWarning(ex, "LLM 网络错误，{A}/{Max} 次重试", attempt, _llm.MaxRetries);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "LLM 响应 JSON 解析失败");
                return new ChatResult(null, null);
            }
            catch (OperationCanceledException)
            {
                return new ChatResult(null, null);  // 外部取消
            }
        }

        return new ChatResult(null, null);
    }

    /// <summary>
    /// 解析请求附加字段：调用方显式传入的优先；否则若配置了关闭思维链（DisableReasoning），
    /// 自动带上 DisableReasoningPayload——防止调用方漏传导致 LLM 进入 thinking 模式
    /// （豆包 thinking 模式不支持强制 tool_choice，记忆总结等场景会 400）。
    /// </summary>
    private JsonObject? ResolveExtraBody(JsonObject? extraBody)
    {
        if (extraBody is not null) return extraBody;
        if (_llm.DisableReasoning && !string.IsNullOrWhiteSpace(_llm.DisableReasoningPayload))
        {
            try { return JsonNode.Parse(_llm.DisableReasoningPayload) as JsonObject; }
            catch { /* payload 格式错误则忽略 */ }
        }
        return null;
    }

    /// <summary>把附加字段合并进请求体 JSON（顶层键覆盖/追加）</summary>
    private static string MergeExtra(string requestJson, JsonObject? extraBody)
    {
        if (extraBody is null) return requestJson;
        try
        {
            var node = JsonNode.Parse(requestJson) as JsonObject ?? new JsonObject();
            foreach (var kv in extraBody)
            {
                node[kv.Key] = kv.Value?.DeepClone();
            }
            return node.ToJsonString();
        }
        catch
        {
            return requestJson;
        }
    }

    /// <summary>格式化 JSON 便于阅读（中文不转义）；非法 JSON 原样返回</summary>
    private static string FormatJson(string json)    {
        try
        {
            return JsonSerializer.Serialize(JsonNode.Parse(json),
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
        }
        catch
        {
            return json;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "\n…(截断)";
}
