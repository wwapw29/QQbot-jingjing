using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using QQBot.Core.Options;

namespace QQBot.Core.Vision;

/// <summary>
/// 双模型识图服务（Vision）：
/// 收到图片时用**专用识图模型**（VisionOptions.Model）把图片描述成文本，
/// 描述文本交给主模型生成回复——主模型不需要支持视觉，也不依赖主模型调用工具。
/// UseMainModel=true 时：直接用主模型看图（不注入描述指令，直接把图发过去），用于测试主模型视觉能力。
/// 图片处理：下载 → System.Drawing 压缩（保持原尺寸、减小体积）→ base64 发给识图模型；
/// 原图不落盘，压缩图写入缓存目录（只保留压缩后的）。
/// </summary>
public sealed class VisionService
{
    private readonly BotOptions _bot;
    private readonly VisionOptions _options;
    private readonly IConfiguration _config;
    private readonly ILogger<VisionService> _logger;
    private readonly HttpClient _http;
    // Files API file_id 缓存（同一图片内容 24h 内不重复上传；key=图片 SHA256）
    private readonly Dictionary<string, (string FileId, DateTime Expires)> _fileCache = new();
    private readonly object _fileLock = new();

    public VisionService(BotOptions bot, IConfiguration config, ILogger<VisionService> logger)
    {
        // 子配置节点（VisionOptions）未单独注册进 DI，从 BotOptions.Vision 取
        _bot = bot;
        _options = bot.Vision;
        _config = config;
        _logger = logger;

        // BaseUrl/ApiKey/UseMainModel 每次请求动态解析（面板热更新立即生效）
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    }

    /// <summary>是否使用主模型识图（运行时读配置，热更新）</summary>
    private bool UseMainModel => string.Equals(_config["Bot:Vision:UseMainModel"], "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>动态解析识图模型（UseMainModel 或 Vision.Model 留空时用主 LLM 的）</summary>
    private string ResolveModel()
    {
        if (UseMainModel) return _config["Bot:Llm:Model"] ?? "";
        var m = _config["Bot:Vision:Model"];
        return string.IsNullOrWhiteSpace(m) ? (_config["Bot:Llm:Model"] ?? "") : m;
    }

    /// <summary>动态解析识图 API 地址（UseMainModel 或 Vision.BaseUrl 留空=复用主 LLM 的）</summary>
    private string ResolveBaseUrl()
    {
        var b = UseMainModel ? "" : _config["Bot:Vision:BaseUrl"];
        if (string.IsNullOrWhiteSpace(b)) b = _config["Bot:Llm:BaseUrl"];
        return (b ?? "").TrimEnd('/') + "/chat/completions";
    }

    /// <summary>动态解析识图 API 密钥（UseMainModel 或 Vision.ApiKey 留空=复用主 LLM 的）</summary>
    private string ResolveApiKey()
    {
        var k = UseMainModel ? "" : _config["Bot:Vision:ApiKey"];
        if (string.IsNullOrWhiteSpace(k)) k = _config["Bot:Llm:ApiKey"];
        return k ?? "";
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
            _logger.LogInformation("识图完成：{N} 张图片的描述（{Src}模型 {Model}）",
                results.Count, UseMainModel ? "主" : "专用", ResolveModel());
        return results;
    }

    /// <summary>调识图模型（OpenAI 兼容多模态）获取单张图的文字描述；userText 附带给模型指明关注点</summary>
    private async Task<string?> DescribeOneAsync(string dataUrl, string? userText, CancellationToken ct)
    {
        var model = ResolveModel();
        var useMain = UseMainModel;
        object[] messages;
        int maxTokens;
        if (useMain)
        {
            // 主模型识图：不带"图片描述器"system / DescribePrompt，直接把图发过去
            //（附用户关注点文字，若有；没有就只发图，让主模型自己理解语境）
            var userContent = new List<object>
            {
                new { type = "image_url", image_url = new { url = dataUrl } }
            };
            if (!string.IsNullOrWhiteSpace(userText))
                userContent.Add(new { type = "text", text = userText });
            messages = [new { role = "user", content = (object)userContent }];
            maxTokens = 1000;
        }
        else
        {
            // 专用识图模型：描述指令 + 用户本次消息文字（告诉识图模型重点看什么）
            var describeInstruction = _options.DescribePrompt;
            if (!string.IsNullOrWhiteSpace(userText))
                describeInstruction += $"\n用户对这张图的关注点/问题：{userText}";
            messages =
            [
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
            ];
            maxTokens = 300;
        }

        var body = new
        {
            model,
            messages,
            max_tokens = maxTokens,
            temperature = 0.3,
            stream = false
        };

        try
        {
            var json = JsonSerializer.Serialize(body);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(model.Contains("vision") ? 60 : 90));
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

    /// <summary>
    /// 主模型嵌入式识图用：下载 + 压缩 → base64 data URL 列表（失败/非图片自动跳过）。
    /// 返回的 data URL 可直接作为 ChatMessage.ImageDataUrls 嵌入对话请求（image_url 内容块）。
    /// </summary>
    public async Task<List<string>> DownloadImagesDataUrlAsync(List<string> urls, CancellationToken ct)
    {
        var result = new List<string>();
        if (urls.Count == 0) return result;
        string? cacheDir = null;
        try
        {
            cacheDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _options.CacheDir));
            Directory.CreateDirectory(cacheDir);
        }
        catch { /* 缓存目录失败不影响嵌入 */ }
        foreach (var url in urls)
        {
            try
            {
                var data = await DownloadImageAsync(url, ct);
                if (data is null || data.Length == 0)
                {
                    _logger.LogWarning("识图下载失败（跳过）：{Url}", url);
                    continue;
                }
                var b64 = CompressToJpegDataUrl(data, cacheDir, _options.JpegQuality);
                if (b64 is not null) result.Add(b64);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "识图图片处理失败（跳过）：{Url}", url);
            }
        }
        if (result.Count > 0)
            _logger.LogInformation("主模型嵌入式识图：已准备 {N} 张图片（嵌入对话请求，模型 {Model}）", result.Count, ResolveModel());
        return result;
    }

    /// <summary>主模型是否为 DeepSeek（模型名或 BaseUrl 含 deepseek）——Files API 只在 DeepSeek 官方可用</summary>
    public bool IsDeepSeekMainModel()
    {
        var baseUrl = _config["Bot:Llm:BaseUrl"] ?? "";
        var model = _config["Bot:Llm:Model"] ?? "";
        return baseUrl.Contains("deepseek.com", StringComparison.OrdinalIgnoreCase)
               || model.Contains("deepseek", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// DeepSeek Files API 上传图片（multipart，purpose=user_data，有效期 FileTtlSeconds 默认 24h）。
    /// 同一图片内容 24h 内缓存 file_id 不重复上传；返回 (fileIds, 上传失败的 urls——调用方回退 base64)。
    /// </summary>
    public async Task<(List<string> FileIds, List<string> FailedUrls)> UploadImagesToFilesAsync(List<string> urls, CancellationToken ct)
    {
        var fileIds = new List<string>();
        var failed = new List<string>();
        if (urls.Count == 0) return (fileIds, failed);
        if (!IsDeepSeekMainModel())
        {
            _logger.LogInformation("主模型非 DeepSeek，Files API 不可用，全部回退 base64 嵌入");
            return (fileIds, [.. urls]);
        }
        var ttl = _options.FileTtlSeconds is >= 3600 and <= 2592000 ? _options.FileTtlSeconds : 86400;
        foreach (var url in urls)
        {
            try
            {
                var data = await DownloadImageAsync(url, ct);
                if (data is null || data.Length == 0) { failed.Add(url); continue; }
                // 上传前先压缩（与 base64 路径一致，省流量/存储）：hash 基于压缩后内容，同一图压缩结果相同可复用 file_id
                var compressed = CompressToJpegBytes(data, _options.JpegQuality);
                if (compressed is null) { failed.Add(url); continue; }
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(compressed));
                lock (_fileLock)
                {
                    if (_fileCache.TryGetValue(hash, out var cached) && cached.Expires > DateTime.Now)
                    {
                        fileIds.Add(cached.FileId);
                        continue;
                    }
                }
                var fileId = await UploadOneFileAsync(compressed, ttl, ct);
                if (fileId is null) { failed.Add(url); continue; }
                lock (_fileLock) _fileCache[hash] = (fileId, DateTime.Now.AddSeconds(ttl));
                fileIds.Add(fileId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Files API 处理失败（回退 base64）：{Url}", url);
                failed.Add(url);
            }
        }
        _logger.LogInformation("Files API：上传/复用 {N} 张图片（有效期 {Ttl}s，模型 {Model}）", fileIds.Count, ttl, ResolveModel());
        return (fileIds, failed);
    }

    /// <summary>上传单张图片到 DeepSeek Files API，返回 file_id（失败 null）</summary>
    private async Task<string?> UploadOneFileAsync(byte[] imageBytes, int ttlSeconds, CancellationToken ct)
    {
        try
        {
            var baseUrl = (_config["Bot:Llm:BaseUrl"] ?? "").TrimEnd('/');
            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(imageBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            form.Add(fileContent, "file", "image.jpg");
            form.Add(new StringContent("user_data"), "purpose");
            form.Add(new StringContent("created_at"), "expires_after[anchor]");
            form.Add(new StringContent(ttlSeconds.ToString()), "expires_after[seconds]");

            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/files") { Content = form };
            var key = ResolveApiKey();
            if (!string.IsNullOrWhiteSpace(key))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Files API 上传失败 [{Code}]: {Msg}", (int)resp.StatusCode, err[..Math.Min(err.Length, 300)]);
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            return JsonNode.Parse(json)?["id"]?.GetValue<string>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Files API 上传异常");
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
        var bytes = CompressToJpegBytes(data, quality);
        if (bytes is null) return null;
        // 只保留压缩后的图片：原图从未落盘，压缩图写入缓存目录
        if (cacheDir is not null)
        {
            try { File.WriteAllBytes(Path.Combine(cacheDir, $"{Guid.NewGuid():N}.jpg"), bytes); }
            catch { /* 缓存写入失败不影响发送 */ }
        }
        return $"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}";
    }

    /// <summary>压缩核心：System.Drawing 解码 → JPEG 重编码（保持原尺寸），返回压缩后字节；解码失败返回 null</summary>
    private static byte[]? CompressToJpegBytes(byte[] data, int quality)
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
            return outMs.ToArray();
        }
        catch (Exception ex)
        {
            // 图片解码失败（损坏/格式不支持）等
            return null;
        }
    }
}
