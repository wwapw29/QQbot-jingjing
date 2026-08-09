using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using QQBot.Core.Options;

namespace QQBot.Core.ComfyUI;

/// <summary>
/// ComfyUI 客户端：提交 workflow（/prompt）→ 轮询历史（/history）→ 下载图片（/view）。
/// </summary>
public sealed class ComfyClient
{
    private readonly ComfyUIOptions _options;
    private readonly ILogger<ComfyClient> _logger;
    private readonly HttpClient _http;

    public ComfyClient(ComfyUIOptions options, ILogger<ComfyClient> logger)
    {
        _options = options;
        _logger = logger;
        // BaseUrl 每次请求动态解析（支持面板热更新），不使用 BaseAddress
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(6) };
    }

    /// <summary>动态拼完整 URL（BaseUrl 热更新后立即生效）</summary>
    private Uri ApiUrl(string path) => new Uri(_options.BaseUrl.TrimEnd('/') + "/" + path.TrimStart('/'));

    /// <summary>提交 workflow，返回 prompt_id；失败返回 null</summary>
    public async Task<string?> SubmitAsync(JsonObject workflow, CancellationToken ct = default)
    {
        try
        {
            var body = new JsonObject
            {
                ["prompt"] = workflow,
                ["client_id"] = Guid.NewGuid().ToString("N")
            };
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(ApiUrl("/prompt"), content, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("ComfyUI 提交失败 [{Code}] {Reason}: {Body}",
                    (int)resp.StatusCode, resp.ReasonPhrase, json[..Math.Min(json.Length, 500)]);
                return null;
            }
            var node = JsonNode.Parse(json);
            var promptId = node?["prompt_id"]?.GetValue<string>();
            if (promptId is null)
            {
                _logger.LogError("ComfyUI 提交响应缺少 prompt_id: {Json}", json[..Math.Min(json.Length, 300)]);
                return null;
            }
            _logger.LogInformation("ComfyUI 已提交，prompt_id={Id}", promptId);
            return promptId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ComfyUI 提交异常（服务未启动？）");
            return null;
        }
    }

    /// <summary>
    /// 轮询等待生成完成，返回输出图片信息列表（SaveImage 节点的 images）。
    /// 超时返回 null。
    /// </summary>
    public async Task<List<ComfyImage>?> WaitForResultAsync(string promptId, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_options.TimeoutSeconds);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                var resp = await _http.GetAsync(ApiUrl($"/history/{promptId}"), ct);
                var json = await resp.Content.ReadAsStringAsync(ct);
                var node = JsonNode.Parse(json)?[promptId];
                if (node is JsonObject entry)
                {
                    var status = entry["status"]?["status_str"]?.GetValue<string>();
                    if (status == "success")
                    {
                        var images = new List<ComfyImage>();
                        var outputs = entry["outputs"] as JsonObject;
                        if (outputs is not null && !string.IsNullOrEmpty(_options.SaveImageNodeId))
                        {
                            var arr = outputs[_options.SaveImageNodeId]?["images"] as JsonArray;
                            if (arr is not null)
                            {
                                foreach (var img in arr.OfType<JsonObject>())
                                {
                                    images.Add(new ComfyImage(
                                        img["filename"]?.GetValue<string>() ?? "",
                                        img["subfolder"]?.GetValue<string>() ?? "",
                                        img["type"]?.GetValue<string>() ?? "output"));
                                }
                            }
                        }
                        if (images.Count == 0)
                        {
                            _logger.LogWarning("ComfyUI 成功但未找到节点 {Node} 的输出图片", _options.SaveImageNodeId);
                        }
                        return images;
                    }
                    if (status == "error")
                    {
                        _logger.LogError("ComfyUI 执行出错: {Json}", json[..Math.Min(json.Length, 500)]);
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ComfyUI 轮询异常，继续重试");
            }
            try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
        }
        _logger.LogWarning("ComfyUI 生图超时（{T}s）", _options.TimeoutSeconds);
        return null;
    }

    /// <summary>下载图片字节流</summary>
    public async Task<byte[]?> DownloadImageAsync(ComfyImage image, CancellationToken ct = default)
    {
        try
        {
            var url = $"/view?filename={Uri.EscapeDataString(image.Filename)}" +
                      $"&subfolder={Uri.EscapeDataString(image.Subfolder)}&type={image.Type}";
            var bytes = await _http.GetByteArrayAsync(ApiUrl(url), ct);
            _logger.LogInformation("图片已下载：{Name}（{Size} KB）", image.Filename, bytes.Length / 1024);
            return bytes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "下载图片失败：{Name}", image.Filename);
            return null;
        }
    }
}

/// <summary>ComfyUI 输出的图片信息</summary>
public sealed record ComfyImage(string Filename, string Subfolder, string Type);
