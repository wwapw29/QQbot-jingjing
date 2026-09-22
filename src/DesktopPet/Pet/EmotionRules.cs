using System.Text.RegularExpressions;

namespace DesktopPet.Pet;

/// <summary>
/// 从她说的话里挑一个"表情"动作。
///
/// 两步走，都不需要她去配合（她本来就会用括号描述动作，比如"（开心地晃了晃辫子）"，关键词能直接命中）：
///   ① <b>显式标记 <c>[动作名]</c></b>：只在"当前皮肤真的加载了这个动作"时才认——所以不会误判，
///      别的方括号（比如 [1]）原样留着，而且显示前会把标记从句子里剔掉。
///   ② <b>关键词规则</b>：<see cref="PetConfig.Emotions"/>（动作名 → 关键词），留空用 <see cref="Defaults"/>。
///
/// 皮肤里没有对应动作时，一切都无声无息地不发生——不报错、不硬凑。
/// </summary>
public static class EmotionRules
{
    /// <summary>内置默认关键词（动作名 → 关键词）。故意保守：宁可不播，也别乱播。</summary>
    public static readonly Dictionary<string, string[]> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["happy"] = new[] { "开心", "高兴", "嘿嘿", "哈哈", "笑", "太好了", "喜欢", "得意", "点头" },
        ["sad"] = new[] { "难过", "委屈", "呜", "唉", "失落", "沮丧", "眼泪", "叹气" },
        ["angry"] = new[] { "生气", "讨厌", "气鼓鼓", "不满", "瞪", "哼" },
        ["surprise"] = new[] { "惊讶", "吃惊", "咦", "诶", "瞪大" },
        ["sleepy"] = new[] { "困", "打哈欠", "想睡", "揉眼睛" },
    };

    /// <summary>显式标记：<c>[动作名]</c>（英文名，和 actions 的键呼应）</summary>
    private static readonly Regex Marker = new(@"\[([A-Za-z_][A-Za-z0-9_]*)\]", RegexOptions.Compiled);

    /// <summary>把"已知动作"的显式标记从句子里剔掉（不认识的方括号原样保留）</summary>
    public static string StripMarkers(string text, IEnumerable<string> actions)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('[')) return text;
        var known = new HashSet<string>(actions, StringComparer.OrdinalIgnoreCase);
        var cleaned = Marker.Replace(text, m => known.Contains(m.Groups[1].Value) ? "" : m.Value);
        cleaned = cleaned.Replace("  ", " ");
        // 标记删掉后可能留下孤零零的标点（"。[happy]" → "。"），这里只用兜掉首尾空白
        return cleaned.Trim();
    }

    /// <summary>
    /// 这句话该播哪个动作；没有合适的就返回 null。
    ///
    /// 顺序：① 显式标记 <c>[动作名]</c> 最高优先；
    ///      ② 触发词匹配——**命中多个时权重高的播；权重相同则「在句子里出现得更晚」的那个播**
    ///         （主人定的规则：同权重者，后触发的播放）。
    /// 系统动作（idle/talk/thinking）不参与触发词匹配。
    /// </summary>
    public static string? Detect(string text, IEnumerable<string> actions, PetConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var known = new HashSet<string>(actions, StringComparer.OrdinalIgnoreCase);

        // ① 显式标记：取最后一个（她一般把情绪收在句尾）；返回的是皮肤里的**规范动作名**（大小写按皮肤来）
        string? last = null;
        foreach (Match m in Marker.Matches(text))
        {
            var name = m.Groups[1].Value;
            var canonical = known.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (canonical is not null) last = canonical;
        }
        if (last is not null) return last;

        // ② 触发词 + 权重
        string? best = null;
        var bestWeight = double.NegativeInfinity;
        var bestAt = -1;
        foreach (var action in known)
        {
            if (SystemActions.Is(action)) continue;                  // 系统动作不参与
            var (words, weight) = RuleFor(action, cfg);
            if (words.Count == 0) continue;

            var at = LastHit(text, words);
            if (at < 0) continue;                                    // 没命中（或被否定掉）

            // 权重高者优先；同权重取"在句子里出现更晚"的
            if (weight > bestWeight + 1e-9 ||
                (Math.Abs(weight - bestWeight) <= 1e-9 && at > bestAt))
            {
                best = action;
                bestWeight = weight;
                bestAt = at;
            }
        }
        return best;
    }

    /// <summary>哪个动作绑定了这个工具（没有就返回 null）——她在正式回复前调工具时播它</summary>
    public static string? ActionForTool(string? toolName, IEnumerable<string> actions, PetConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return null;
        var known = new HashSet<string>(actions, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in cfg.Actions)
        {
            if (kv.Value?.Tools is not { Count: > 0 } tools) continue;
            if (!tools.Any(t => t.Equals(toolName, StringComparison.OrdinalIgnoreCase))) continue;
            var canonical = known.FirstOrDefault(k => k.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
            if (canonical is not null) return canonical;
        }
        return null;
    }

    /// <summary>取某个动作的关键词与权重：动作自己配了触发词就用它（带权重），否则回落 旧 emotions → 内置默认表</summary>
    private static (List<string> Words, double Weight) RuleFor(string action, PetConfig cfg)
    {
        if (cfg.Actions.TryGetValue(action, out var a) && a?.Triggers is { Count: > 0 })
            return (a.Triggers.Where(w => !string.IsNullOrWhiteSpace(w)).ToList(), a.Weight);

        if (cfg.Emotions.TryGetValue(action, out var legacy) && legacy is { Length: > 0 })
            return (legacy.Where(w => !string.IsNullOrWhiteSpace(w)).ToList(), 1);

        if (Defaults.TryGetValue(action, out var dflt))
            return (dflt.Where(w => !string.IsNullOrWhiteSpace(w)).ToList(), 1);

        return (new List<string>(), 1);
    }

    /// <summary>一组关键词里**最后一个**命中的位置（没命中返回 -1；前面紧跟"不/没/别"的算否定、跳过）</summary>
    private static int LastHit(string text, List<string> words)
    {
        var at = -1;
        foreach (var w in words)
        {
            var from = 0;
            while (true)
            {
                var i = text.IndexOf(w, from, StringComparison.OrdinalIgnoreCase);
                if (i < 0) break;
                if (!IsNegated(text, i) && i > at) at = i;
                from = i + w.Length;
            }
        }
        return at;
    }

    /// <summary>关键词前面紧跟"不 / 没 / 别"就当成否定，跳过</summary>
    private static bool IsNegated(string text, int at)
        => at > 0 && (text[at - 1] == '不' || text[at - 1] == '没' || text[at - 1] == '别');
}
