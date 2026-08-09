using System.Text.RegularExpressions;
using QQBot.Core.Options;

namespace QQBot.Core.Chat;

/// <summary>
/// AI 回复截取器（cot 处理）：从 LLM 原始输出中提取"最终回复"。
/// 三种策略（配置 Prompt:ReplyExtraction:Strategy）：
///  - reasoningContent：直接丢弃 reasoning_content 字段（DeepSeek 等自带思维链分离）
///  - delimiter：截取 content 中最后一个分隔符之后的部分
///  - regex：用正则提取最终回答段
/// 无论哪种策略，最终都会再做一次 delimiter 兜底，防止思维过程混进 content。
/// </summary>
public static class ReplyExtractor
{
    public static string Extract(ChatResult result, ReplyExtractionOptions options)
    {
        if (result.Content is null) return "";

        string text = options.Strategy switch
        {
            // 思维链在单独字段里，content 本身就是最终回复
            "reasoningContent" => result.Content,

            // 模型按约定在思考后输出分隔符 → 取分隔符之后
            "delimiter" => CutAfterDelimiter(result.Content, options.Delimiter),

            // 正则提取
            "regex" when !string.IsNullOrEmpty(options.Regex) => ExtractByRegex(result.Content, options.Regex!),

            _ => result.Content
        };

        // 兜底：content 里若还混着分隔符标记的思考段，再截一次
        if (!string.IsNullOrEmpty(options.Delimiter))
        {
            text = CutAfterDelimiter(text, options.Delimiter);
        }

        return text.Trim();
    }

    private static string CutAfterDelimiter(string content, string? delimiter)
    {
        if (string.IsNullOrEmpty(delimiter)) return content;

        // 1) 优先匹配完整标记（如 ```END_REASONING```）
        var idx = content.LastIndexOf(delimiter, StringComparison.Ordinal);
        if (idx >= 0)
        {
            return content[(idx + delimiter.Length)..];
        }

        // 2) 兜底：LLM 可能丢失 ``` 装饰，只要有标记核心词（如 END_REASONING）
        //    就把它所在行（含行首可能的 ```）之后的内容视为正文
        var keyword = delimiter.Trim('`', ' ', '\t', '\r', '\n');
        if (keyword.Length >= 3)
        {
            var kidx = content.LastIndexOf(keyword, StringComparison.Ordinal);
            if (kidx >= 0)
            {
                var nl = content.IndexOf('\n', kidx);
                return nl >= 0 ? content[(nl + 1)..] : "";
            }
        }
        return content;
    }

    private static string ExtractByRegex(string content, string pattern)
    {
        var m = Regex.Match(content, pattern, RegexOptions.Singleline);
        return m.Success && m.Groups.Count > 1 ? m.Groups[1].Value : content;
    }
}
