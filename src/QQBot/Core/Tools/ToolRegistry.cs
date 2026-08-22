using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using QQBot.Core.Options;

namespace QQBot.Core.Tools;

/// <summary>
/// 工具注册表：收集所有 ITool，生成 OpenAI tools 定义，执行工具调用。
/// 描述外部化：appsettings 的 Tools.Descriptions 可覆盖每个工具的 Description（留空/缺失用代码默认）。
/// 启停：appsettings 的 Tools.Disabled 列出的工具不生成定义（LLM 无法调用）。
/// 注意：Descriptions/Disabled 每次调用时从 ToolsOptions 动态读取，后台面板热更新即时生效。
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools;
    private readonly ToolsOptions _options;
    private readonly ILogger<ToolRegistry> _logger;

    public ToolRegistry(IEnumerable<ITool> tools, ToolsOptions options, ILogger<ToolRegistry> logger)
    {
        _logger = logger;
        _options = options;
        _tools = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> ToolNames => _tools.Keys.ToList();

    /// <summary>全部已注册工具（后台面板清单用）</summary>
    public IReadOnlyList<ITool> AllTools => _tools.Values.ToList();

    /// <summary>工具是否被禁用（动态读配置）</summary>
    public bool IsDisabled(string name) => _options.Disabled?.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>工具是否对客人开放（GuestAllowed 白名单；空名单=全部开放；动态读配置）</summary>
    public bool IsGuestAllowed(string name) =>
        _options.GuestAllowed is null || _options.GuestAllowed.Count == 0
            || _options.GuestAllowed.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 生成 OpenAI tools 参数（function calling 定义；描述可用配置覆盖；禁用的跳过）。
    /// forGuest=true 时仅包含对客人开放的工具（主人调用传 false/不传 = 全部）。
    /// </summary>
    public JsonArray BuildToolDefinitions(bool forGuest = false)
    {
        var arr = new JsonArray();
        foreach (var tool in _tools.Values)
        {
            if (IsDisabled(tool.Name)) continue;
            if (forGuest && !IsGuestAllowed(tool.Name)) continue;
            var desc = tool.Description;
            if (_options.Descriptions?.TryGetValue(tool.Name, out var custom) == true && !string.IsNullOrWhiteSpace(custom))
            {
                desc = custom;
            }
            arr.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = desc,
                    ["parameters"] = tool.ParametersSchema
                }
            });
        }
        return arr;
    }

    /// <summary>执行工具；未知工具、已禁用或执行失败返回 null</summary>
    public async Task<string?> ExecuteAsync(string name, string argsJson, ToolContext ctx, CancellationToken ct)
    {
        if (!_tools.TryGetValue(name, out var tool))
        {
            _logger.LogWarning("未知工具调用：{Name}", name);
            return null;
        }
        if (IsDisabled(name))
        {
            _logger.LogWarning("工具 {Name} 已被禁用，拒绝执行", name);
            return null;
        }
        // 防御：客人会话禁止调用未对其开放的工具（即使 LLM 幻觉/被注入硬调用）
        if (!ctx.Message.IsOwner && !IsGuestAllowed(name))
        {
            _logger.LogWarning("工具 {Name} 未对客人开放，拒绝执行（uid={Uid}）", name, ctx.Message.UserId);
            return $"工具 {name} 当前不可用。";
        }
        try
        {
            var result = await tool.ExecuteAsync(argsJson, ctx, ct);
            _logger.LogInformation("工具 {Name} 执行完成：{Result}", name, result[..Math.Min(result.Length, 200)]);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "工具 {Name} 执行异常", name);
            return $"工具执行出错：{ex.Message}";
        }
    }
}
