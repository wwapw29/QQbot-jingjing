using QQBot.Core.OneBot;

namespace QQBot.Core.Commands;

/// <summary>
/// 机器人命令（斜杠命令，默认仅主人可用）。
/// 新增命令：实现本接口 + 在 Program.cs 注册一行即可。
/// </summary>
public interface IBotCommand
{
    /// <summary>命令名（不含斜杠，小写）</summary>
    string Name { get; }

    /// <summary>一句话说明</summary>
    string Description { get; }

    /// <summary>用法示例</summary>
    string Usage { get; }

    /// <summary>是否允许非主人（客人）调用；默认 false=仅主人可用。置 true 开放给所有人。</summary>
    bool GuestAllowed => false;

    /// <summary>执行命令，返回回复文本</summary>
    Task<string> ExecuteAsync(IncomingMessage msg, string[] args, CancellationToken ct);
}
