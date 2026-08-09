using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QQBot.Core.Tools;

/// <summary>
/// browse_web —— 浏览指定网页并返回文字内容。
/// LLM 在用户需要网页上的实时信息（新闻、文档、价格、公告等）时自主调用，
/// 抓取结果回填后由 LLM 总结成自然语言回复。
/// </summary>
public sealed class BrowseWebTool : ITool
{
    private const int MaxLength = 6000;          // 返回给 LLM 的最大字符数（控制 token 开销）
    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        return client;
    }

    public string Name => "browse_web";
    public string Description =>
        "浏览指定网页并返回其文字内容。当用户要求查看某个网址/网页/新闻/文章，或需要网页上的实时信息（如今日新闻、公告、价格、文档说明）时调用。参数 url 为目标网页完整地址。" +
        "自主执行：判断出需要网页信息时立即抓取，无需先询问用户确认；抓取失败也要如实告知用户原因。";

    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["url"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "要浏览的网页完整 URL（必须 http:// 或 https:// 开头）"
            }
        },
        ["required"] = new JsonArray("url"),
        ["additionalProperties"] = false
    };

    public async Task<string> ExecuteAsync(string argsJson, ToolContext ctx, CancellationToken ct)
    {
        var args = JsonNode.Parse(argsJson) as JsonObject;
        var url = args?["url"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(url)) return "url 参数为空";

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "仅支持 http/https 开头的网址";
        }

        try
        {
            var html = await _http.GetStringAsync(url, ct);
            if (string.IsNullOrWhiteSpace(html)) return "网页内容为空";

            var text = HtmlToText(html);
            if (text.Length == 0) return "网页未提取到可读文字（可能是图片/视频页或需要登录）";

            if (text.Length > MaxLength)
            {
                text = text[..MaxLength] + "\n…(内容过长已截断)";
            }
            return $"网页内容（{url}）：\n{text}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "网页抓取超时（15 秒），请稍后重试或换一个网址";
        }
        catch (Exception ex)
        {
            return $"网页抓取失败：{ex.Message}";
        }
    }

    /// <summary>HTML → 纯文本：去 script/style/注释/tag、解码实体、压缩空白</summary>
    private static string HtmlToText(string html)
    {
        var s = Regex.Replace(html, "<(script|style|noscript|svg)[^>]*>.*?</\\1>", " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, "<!--.*?-->", " ", RegexOptions.Singleline);
        s = Regex.Replace(s, "<[^>]+>", " ");
        s = WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"[ \t\u00a0]+", " ");
        s = Regex.Replace(s, @"\n\s*\n+", "\n");
        return s.Trim();
    }
}
