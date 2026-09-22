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
    /// <summary>单张图的识图结果：描述文本 + 压缩图路径 + 元数据 txt 路径（均为相对她空间根目录的路径）</summary>
    public sealed record VisionImageResult(string Description, string? ImagePath, string? MetadataPath);

    /// <summary>已归档的一份图片：压缩图（.jpg）与她空间里的元数据文本（.txt，可能为 null）</summary>
    public sealed record ArchivedImage(string? ImagePath, string? MetadataPath);

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

    /// <summary>读配置；读不到（未配置 **或配置被热重载搞坏**）时用绑定选项里的最后好值兜底</summary>
    private string Cfg(string key, string fallback)
    {
        var v = _config[key];
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }

    /// <summary>是否使用主模型识图（运行时读配置，热更新；读不到时用绑定值兜底）</summary>
    private bool UseMainModel
    {
        get
        {
            var raw = _config["Bot:Vision:UseMainModel"];
            if (string.IsNullOrWhiteSpace(raw)) return _options.UseMainModel;
            return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>动态解析识图模型（UseMainModel 或 Vision.Model 留空时用主 LLM 的）</summary>
    private string ResolveModel()
    {
        if (UseMainModel) return Cfg("Bot:Llm:Model", _bot.Llm.Model);
        var m = _config["Bot:Vision:Model"];
        return string.IsNullOrWhiteSpace(m) ? Cfg("Bot:Llm:Model", _bot.Llm.Model) : m;
    }

    /// <summary>动态解析识图 API 地址（UseMainModel 或 Vision.BaseUrl 留空=复用主 LLM 的）</summary>
    private string ResolveBaseUrl()
    {
        var b = UseMainModel ? "" : _config["Bot:Vision:BaseUrl"];
        if (string.IsNullOrWhiteSpace(b)) b = Cfg("Bot:Llm:BaseUrl", _bot.Llm.BaseUrl);
        var baseUrl = (b ?? "").TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
        {
            // 走到这里说明 BaseUrl 是空的（多半是配置被热重载读坏了）——
            // 直接返回空串让调用方跳过，别把相对地址丢给 HttpClient 抛那句看不懂的异常
            _logger.LogError("识图 BaseUrl 为空或不是绝对地址（\"{Url}\"）：请检查 appsettings.json 的 Bot.Llm.BaseUrl，或重启 QQBot", baseUrl);
            return "";
        }
        return baseUrl + "/chat/completions";
    }

    /// <summary>动态解析识图 API 密钥（UseMainModel 或 Vision.ApiKey 留空=复用主 LLM 的）</summary>
    private string ResolveApiKey()
    {
        var k = UseMainModel ? "" : _config["Bot:Vision:ApiKey"];
        if (string.IsNullOrWhiteSpace(k)) k = Cfg("Bot:Llm:ApiKey", _bot.Llm.ApiKey);
        return k ?? "";
    }

    /// <summary>
    /// 识别一组图片，返回每张图的文本描述（数量受 MaxImagesPerMessage 限制）。
    /// 下载/压缩/识别失败的图自动跳过；全部失败返回空列表。
    /// </summary>
    /// <summary>识图总开关（运行时读配置，热更新生效）</summary>
    public bool Enabled => string.Equals(_config["Bot:Vision:Enabled"], "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 识别一份**本地图片字节**（如静静自己刚生成的图）：压缩后交识图模型描述，返回描述文本。
    /// 供生图工具复用——生图完成后让静静"看到"自己画出来的图。失败返回 null（不影响主流程）。
    /// </summary>
    public async Task<string?> DescribeImageBytesAsync(byte[] imageBytes, string? userText, CancellationToken ct = default)
    {
        if (imageBytes is null || imageBytes.Length == 0) return null;
        try
        {
            // 静静自己画的图不落盘（避免与她空间里的聊天图混在一起），只要描述
            var (dataUrl, _) = CompressToJpegDataUrl(imageBytes, null, _options.JpegQuality);
            if (dataUrl is null) return null;

            var desc = await DescribeOneAsync(dataUrl, userText, ct);
            if (!string.IsNullOrWhiteSpace(desc))
                _logger.LogInformation("生图结果识图完成（{Src}模型 {Model}）", UseMainModel ? "主" : "专用", ResolveModel());
            return string.IsNullOrWhiteSpace(desc) ? null : desc.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "生图结果识图失败（跳过）");
            return null;
        }
    }

    /// <summary>
    /// 识别一组图片，返回每张图的文本描述（数量受 MaxImagesPerMessage 限制）。
    /// userText：本次消息的文字内容（用户的关注点/问题），会附带给识图模型，让它知道重点看什么。
    /// 下载/压缩/识别失败的图自动跳过；全部失败返回空列表。
    /// </summary>
    public async Task<List<VisionImageResult>> DescribeImagesAsync(List<string> imageUrls, string? userText,
        CancellationToken ct = default, string? nameHint = null)
    {
        var results = new List<VisionImageResult>();
        if (imageUrls.Count == 0) return results;

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
                var jpeg = CompressToJpegBytes(data, _options.JpegQuality);
                if (jpeg is null) continue;

                var arch = ArchiveImage(data, jpeg, nameHint);          // 压缩图 + 元数据 txt（用原图抽）
                var b64 = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg);
                var desc = await DescribeOneAsync(b64, userText, ct);
                if (!string.IsNullOrWhiteSpace(desc))
                    results.Add(new VisionImageResult(desc.Trim(), arch.ImagePath, arch.MetadataPath));
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

    /// <summary>把落盘文件换算成"相对她空间根目录"的路径（她 run_shell 时直接用得到）；不在空间内则回文件名</summary>
    private string? ToSpaceRelative(string? savedFile)
    {
        if (string.IsNullOrEmpty(savedFile)) return null;
        try
        {
            var space = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _bot.Shell.SandboxPath));
            var full = Path.GetFullPath(savedFile);
            if (full.StartsWith(space, StringComparison.OrdinalIgnoreCase))
                return Path.GetRelativePath(space, full).Replace(Path.DirectorySeparatorChar, '/');
        }
        catch { /* 换算失败就用文件名 */ }
        return Path.GetFileName(savedFile);
    }

    /// <summary>调识图模型（OpenAI 兼容多模态）获取单张图的文字描述；userText 附带给模型指明关注点</summary>
    /// <summary>
    /// 把一张 JPEG 存进她的空间（Vision.CacheDir，按 来源_时间_随机4位.jpg 命名），
    /// 返回相对她空间根目录的路径——供截图这类"非聊天来源"的图片归档用。
    /// </summary>
    public string? SaveJpegToSpace(byte[] jpegBytes, string nameHint)
        => SaveImageToSpace(jpegBytes, BuildImageBaseName(nameHint));

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
            // 主模型（如 deepseek-flash）带思维链时 reasoning 会吃掉输出配额 → 描述可能为空，故给足上限
            maxTokens = 2048;
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
            var url = ResolveBaseUrl();
            if (string.IsNullOrWhiteSpace(url)) return null;   // 地址解析不出来（配置异常）——已在里面打过日志
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
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
    public async Task<(List<string> DataUrls, List<ArchivedImage> Archived)> DownloadImagesDataUrlAsync(
        List<string> urls, CancellationToken ct, string? nameHint = null)
    {
        var result = new List<string>();
        var archived = new List<ArchivedImage>();
        if (urls.Count == 0) return (result, archived);
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
                var jpeg = CompressToJpegBytes(data, _options.JpegQuality);
                if (jpeg is null) continue;
                archived.Add(ArchiveImage(data, jpeg, nameHint));
                result.Add("data:image/jpeg;base64," + Convert.ToBase64String(jpeg));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "识图图片处理失败（跳过）：{Url}", url);
            }
        }
        if (result.Count > 0)
            _logger.LogInformation("主模型嵌入式识图：已准备 {N} 张图片（嵌入对话请求，模型 {Model}）；已归档 {S} 张（含元数据 {M} 份）",
                result.Count, ResolveModel(),
                archived.Count(a => a.ImagePath is not null), archived.Count(a => a.MetadataPath is not null));
        return (result, archived);
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
    public async Task<(List<string> FileIds, List<string> FailedUrls, List<ArchivedImage> Archived, List<string> DataUrls)> UploadImagesToFilesAsync(
        List<string> urls, CancellationToken ct, string? nameHint = null)
    {
        var fileIds = new List<string>();
        var failed = new List<string>();
        var archived = new List<ArchivedImage>();
        // 上传成功那些图的 base64（与 fileIds 同序）：**保底备份**——服务端要是拒了 file 块，
        // ChatEngine 会直接拿它当内嵌图片重发，不用重新下载/上传
        var dataUrls = new List<string>();
        if (urls.Count == 0) return (fileIds, failed, archived, dataUrls);
        if (!IsDeepSeekMainModel())
        {
            _logger.LogInformation("主模型非 DeepSeek，Files API 不可用，全部回退 base64 嵌入");
            return (fileIds, [.. urls], archived, dataUrls);
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
                // 无论 file_id 是否命中缓存，都把压缩图 + 元数据存进她的空间（她之后能自己读）
                archived.Add(ArchiveImage(data, compressed, nameHint));
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(compressed));
                lock (_fileLock)
                {
                    if (_fileCache.TryGetValue(hash, out var cached) && cached.Expires > DateTime.Now)
                    {
                        fileIds.Add(cached.FileId);
                        dataUrls.Add("data:image/jpeg;base64," + Convert.ToBase64String(compressed));
                        continue;
                    }
                }
                var fileId = await UploadOneFileAsync(compressed, ttl, ct);
                if (fileId is null) { failed.Add(url); continue; }
                lock (_fileLock) _fileCache[hash] = (fileId, DateTime.Now.AddSeconds(ttl));
                fileIds.Add(fileId);
                dataUrls.Add("data:image/jpeg;base64," + Convert.ToBase64String(compressed));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Files API 处理失败（回退 base64）：{Url}", url);
                failed.Add(url);
            }
        }
        _logger.LogInformation("Files API：上传/复用 {N} 张图片（有效期 {Ttl}s，模型 {Model}）；已归档 {S} 张到她的空间（含元数据 {M} 份）；base64 保底备份 {B} 份",
            fileIds.Count, ttl, ResolveModel(),
            archived.Count(a => a.ImagePath is not null), archived.Count(a => a.MetadataPath is not null),
            dataUrls.Count);
        return (fileIds, failed, archived, dataUrls);
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

    /// <summary>
    /// 把压缩后的图片写进她的个人空间（Vision.CacheDir，按 {来源_时间_随机4位}.jpg 命名），
    /// 返回"相对她空间根目录"的路径（她 run_shell 直接用得到）；失败返回 null。
    /// </summary>
    private string? SaveImageToSpace(byte[] jpegBytes, string baseName)
    {
        if (jpegBytes is null || jpegBytes.Length == 0) return null;
        try
        {
            var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _options.CacheDir));
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, baseName + ".jpg");
            File.WriteAllBytes(file, jpegBytes);
            return ToSpaceRelative(file);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "图片存档失败（不影响识图）");
            return null;
        }
    }

    /// <summary>把文本写进她的空间（元数据 sidecar），返回相对她空间的路径</summary>
    private string? SaveTextToSpace(string fileName, string text)
    {
        try
        {
            var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _options.CacheDir));
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, fileName);
            File.WriteAllText(file, text, new System.Text.UTF8Encoding(false));
            return ToSpaceRelative(file);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "元数据落盘失败（不影响识图）");
            return null;
        }
    }

    /// <summary>
    /// 归档一张图：压缩图落盘（供她读/留档）+ 从**原图字节**里抽出文本元数据写成同名 .txt
    /// （压缩会丢掉元数据，所以必须用原图抽；PNG 的 tEXt 参数、JPEG 的 EXIF 注释都在这）。
    /// </summary>
    private ArchivedImage ArchiveImage(byte[] originalBytes, byte[]? jpegBytes, string? nameHint)
    {
        var baseName = BuildImageBaseName(nameHint);
        var imagePath = SaveImageToSpace(jpegBytes ?? CompressToJpegBytes(originalBytes, _options.JpegQuality) ?? [], baseName);
        string? metaPath = null;
        if (_options.SaveMetadata)
        {
            var meta = ImageMetadataExtractor.Extract(originalBytes);
            if (!string.IsNullOrWhiteSpace(meta))
            {
                metaPath = SaveTextToSpace(baseName + ".txt", meta);
                if (metaPath is not null)
                    _logger.LogInformation("图片元数据已转存：{Path}（{N} 字符）", metaPath, meta.Length);
            }
        }
        return new ArchivedImage(imagePath, metaPath);
    }

    /// <summary>
    /// System.Drawing 压缩：保持原尺寸，JPEG 重编码减小体积；压缩图落到她的空间目录并返回 base64 data URL。
    /// 文件名带上来源与时间（如 群123456_20260912-021530_ab12.jpg），方便静静自己按名字读取。
    /// </summary>
    private static (string? DataUrl, string? SavedFile) CompressToJpegDataUrl(byte[] data, string? cacheDir, int quality, string? nameHint = null)
    {
        var bytes = CompressToJpegBytes(data, quality);
        if (bytes is null) return (null, null);
        string? saved = null;
        if (cacheDir is not null)
        {
            try
            {
                saved = Path.Combine(cacheDir, BuildImageFileName(nameHint));
                File.WriteAllBytes(saved, bytes);
            }
            catch { saved = null; /* 落盘失败不影响识别/发送 */ }
        }
        return ($"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}", saved);
    }

    /// <summary>压缩图文件名：{来源}_{时间}_{随机4位}.jpg</summary>
    private static string BuildImageFileName(string? nameHint) => BuildImageBaseName(nameHint) + ".jpg";

    /// <summary>归档文件基名（不带扩展名）：{来源}_{时间}_{随机4位}——压缩图与元数据 txt 共用同一个基名</summary>
    private static string BuildImageBaseName(string? nameHint)
    {
        var h = string.IsNullOrWhiteSpace(nameHint) ? "img" : nameHint.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) h = h.Replace(c, '_');
        if (h.Length > 40) h = h[..40];
        var rnd = Guid.NewGuid().ToString("N")[..4];
        return $"{h}_{DateTime.Now:yyyyMMdd-HHmmss}_{rnd}";
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
