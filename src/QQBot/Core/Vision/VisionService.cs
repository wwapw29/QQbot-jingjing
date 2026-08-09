using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using QQBot.Core.Options;

namespace QQBot.Core.Vision;

/// <summary>
/// 双模型识图服务（Vision）：
/// 收到图片时用**专用识图模型**（VisionOptions.Model）把图片描述成文本，
/// 描述文本交给主模型生成回复——主模型不需要支持视觉，也不依赖主模型调用工具。
/// 图片处理：下载 → System.Drawing 压缩（保持原尺寸、减小体积）→ base64 发给识图模型；
/// 原图不落盘，压缩图写入缓存目录（只保留压缩后的）。
/// </summary>
public sealed class VisionService
{
    private readonly BotOptions _bot;
    private readonly VisionOptions _options;
    private readonly ILogger<VisionService> _logger;
    private readonly HttpClient _http;

    public VisionService(BotOptions bot, ILogger<VisionService> logger)
    {
        // 子配置节点（VisionOptions）未单独注册进 DI，从 BotOptions.Vision 取
        _bot = bot;
        _options = bot.Vision;
        _logger = logger;

        // BaseUrl/ApiKey 每次请求动态解析（支持面板热更新）；留空复用主 LLM 的
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    }

    /// <summary>动态解析识图 API 地址（Vision.BaseUrl 留空=复用主 LLM 的）</summary>
    private string ResolveBaseUrl()
    {
        var b = !string.IsNullOrWhiteSpace(_options.BaseUrl) ? _options.BaseUrl : _bot.Llm.BaseUrl;
        return b.TrimEnd('/') + "/chat/completions";
    }

    /// <summary>动态解析识图 API 密钥（Vision.ApiKey 留空=复用主 LLM 的）</summary>
    private string ResolveApiKey()
    {
        return !string.IsNullOrWhiteSpace(_options.ApiKey) ? _options.ApiKey : _bot.Llm.ApiKey ?? "";
    }

    /// <summary>
    /// 识别一组图片，返回每张图的文本描述（数量受 MaxImagesPerMessage 限制）。
    /// 下载/压缩/识别失败的图自动跳过；全部失败返回空列表。
    /// </summary>
    /// <summary>
    /// 识别一组图片，返回每张图的文本描述（数量受 MaxImagesPerMessage 限制）。
    /// userText：本次消息的文字内容（用户的关注点/问题），会附带给识图模型，让它知道重点看什么。
    /// 下载/压缩/识别失败的图自动跳过；全部失败返回空列表。
    /// </summary>
    public async Task<List<string>> DescribeImagesAsync(List<string> imageUrls, string? userText, CancellationToken ct = default)
    {
        var results = new List<string>();
        if (imageUrls.Count == 0) return results;

        string? cacheDir = null;
        try
        {
            cacheDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _options.CacheDir));
            Directory.CreateDirectory(cacheDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "创建识图缓存目录失败（不影响识别）");
        }

        var take = Math.Min(imageUrls.Count, Math.Max(1, _options.MaxImagesPerMessage));
        for (int i = 0; i < take; i++)
        {
            try
            {
                var data = await DownloadImageAsync(imageUrls[i], ct);
                if (data is null || data.Length == 0)
                {
                    _logger.LogWarning("识图下载失败（跳过）：{Url}", imageUrls[i]);
                    continue;
                }
                var b64 = CompressToJpegDataUrl(data, cacheDir, _options.JpegQuality);
                if (b64 is null) continue;

                var desc = await DescribeOneAsync(b64, userText, ct);
                if (!string.IsNullOrWhiteSpace(desc)) results.Add(desc.Trim());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "识图图片处理失败：{Url}", imageUrls[i]);
            }
        }
        if (results.Count > 0)
            _logger.LogInformation("识图模型返回 {N} 张图片的描述", results.Count);
        return results;
    }

    /// <summary>调识图模型（OpenAI 兼容多模态）获取单张图的文字描述；userText 附带给模型指明关注点</summary>
    private async Task<string?> DescribeOneAsync(string dataUrl, string? userText, CancellationToken ct)
    {
        // 描述指令 + 用户本次消息文字（告诉识图模型重点看什么）
        var describeInstruction = _options.DescribePrompt;
        if (!string.IsNullOrWhiteSpace(userText))
            describeInstruction += $"\n用户对这张图的关注点/问题：{userText}";

        var body = new
        {
            model = _options.Model,
            messages = new object[]
            {
                new { role = "system", content = "你是图片描述器，只负责准确描述图片内容。" },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = describeInstruction },
                        new { type = "image_url", image_url = new { url = dataUrl } }
                    }
                }
            },
            max_tokens = 300,
            temperature = 0.3,
            stream = false
        };

        try
        {
            var json = JsonSerializer.Serialize(body);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.Model.Contains("vision") ? 60 : 90));
            using var req = new HttpRequestMessage(HttpMethod.Post, ResolveBaseUrl()) { Content = content };
            var key = ResolveApiKey();
            if (!string.IsNullOrWhiteSpace(key))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            var resp = await _http.SendAsync(req, cts.Token);
            var bodyText = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("识图模型调用失败 [{Code}]: {Msg}", (int)resp.StatusCode,
                    bodyText[..Math.Min(bodyText.Length, 300)]);
                return null;
            }
            var node = JsonNode.Parse(bodyText);
            return node?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()?.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "识图模型调用异常");
            return null;
        }
    }

    /// <summary>下载图片到内存（带浏览器 UA）</summary>
    private static async Task<byte[]?> DownloadImageAsync(string url, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        using var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>System.Drawing 压缩：保持原尺寸，JPEG 重编码减小体积；落盘缓存目录并返回 base64 data URL</summary>
    private static string? CompressToJpegDataUrl(byte[] data, string? cacheDir, int quality)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var bmp = new Bitmap(ms);
            using var outMs = new MemoryStream();
            var enc = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
            using var ep = new System.Drawing.Imaging.EncoderParameters(1);
            ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, Math.Clamp(quality, 1, 100));
            bmp.Save(outMs, enc, ep);
            var bytes = outMs.ToArray();
            // 只保留压缩后的图片：原图从未落盘，压缩图写入缓存目录
            if (cacheDir is not null)
            {
                try { File.WriteAllBytes(Path.Combine(cacheDir, $"{Guid.NewGuid():N}.jpg"), bytes); }
                catch { /* 缓存写入失败不影响发送 */ }
            }
            return $"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}";
        }
        catch (Exception ex)
        {
            // 图片解码失败（损坏/格式不支持）等
            return null;
        }
    }
}
