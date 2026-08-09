using System.Text.Json.Nodes;
using QQBot.Core.OneBot;

namespace QQBot.Core.Tools;

/// <summary>
/// 机器人工具（LLM 可自主调用的函数）。
/// 新增工具：实现本接口 + 在 Program.cs 注册一行即可。
/// </summary>
public interface ITool
{
    /// <summary>工具名（小写，LLM 用这个名字调用）</summary>
    string Name { get; }

    /// <summary>工具描述（LLM 判断何时调用的依据，写清楚触发场景）</summary>
    string Description { get; }

    /// <summary>参数 JSON Schema（OpenAI tools 格式）</summary>
    JsonObject ParametersSchema { get; }

    /// <summary>执行工具，返回给 LLM 的结果文本</summary>
    Task<string> ExecuteAsync(string argsJson, ToolContext ctx, CancellationToken ct);
}

/// <summary>工具执行上下文（当前消息信息 + 会话）</summary>
public sealed record ToolContext(IncomingMessage Message);
