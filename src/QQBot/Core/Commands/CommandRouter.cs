using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using QQBot.Core.Chat;
using QQBot.Core.Memory;
using QQBot.Core.OneBot;
using QQBot.Core.Options;

namespace QQBot.Core.Commands;

/// <summary>
/// 命令路由：识别前缀命令（如 !help），仅主人可执行，非主人一律吞掉不回复。
/// </summary>
public sealed class CommandRouter
{
    private readonly Dictionary<string, IBotCommand> _commands;
    private readonly OneBotClient _client;
    private readonly BotOptions _options;
    private readonly string _prefix;
    private readonly ILogger<CommandRouter> _logger;

    public CommandRouter(IEnumerable<IBotCommand> commands, OneBotClient client,
                         BotOptions options, ILogger<CommandRouter> logger)
    {
        _client = client;
        _options = options;
        _prefix = options.Command.Prefix;
        _logger = logger;
        _commands = commands.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>尝试作为命令处理。返回 true 表示已处理（不再走对话流程）。</summary>
    public async Task<bool> TryHandleAsync(IncomingMessage msg, CancellationToken ct)
    {
        var text = msg.PlainText.Trim();
        if (!text.StartsWith(_prefix)) return false;

        var parts = text[_prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var name = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var args = parts.Length > 1 ? parts[1..] : [];

        if (!_commands.TryGetValue(name, out var cmd))
        {
            // 未知命令：主人给提示；非主人直接吞掉不回复（防探测）
            if (!msg.IsOwner) return true;
            return await ReplyAsync(msg, $"未知命令 {_prefix}{name}。输入 {_prefix}help 查看可用命令。", ct);
        }

        // 权限：主人全通；非主人仅可调用 GuestAllowed=true 的命令（如 !draw），其余吞掉不回复
        if (!msg.IsOwner && !cmd.GuestAllowed) return true;

        string reply;
        try
        {
            reply = await cmd.ExecuteAsync(msg, args, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "命令 {Prefix}{Name} 执行失败", _prefix, name);
            reply = $"命令 {_prefix}{name} 执行出错：{ex.Message}";
        }

        // 空回复不发送（命令内部已自行回复，如 !draw 已发图）
        if (string.IsNullOrWhiteSpace(reply)) return true;
        return await ReplyAsync(msg, reply, ct);
    }

    /// <summary>
    /// 后台控制台：以主人身份执行命令，返回回复文本（不发送 QQ）。
    /// 命令内部自发的发送仍会执行（如 !draw 生图发图）。
    /// 返回 (Handled=false, null) 表示不是命令格式（未以前缀开头）。
    /// </summary>
    public async Task<(bool Handled, string? Reply)> ExecuteForConsoleAsync(string commandLine, CancellationToken ct)
    {
        var text = (commandLine ?? "").Trim();
        if (!text.StartsWith(_prefix)) return (false, null);

        var parts = text[_prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var name = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var args = parts.Length > 1 ? parts[1..] : [];

        if (!_commands.TryGetValue(name, out var cmd))
            return (true, $"未知命令 {_prefix}{name}。输入 {_prefix}help 查看可用命令。");

        // 构造主人身份的私聊消息（控制台即主人，跳过权限检查直接执行）
        var msg = new IncomingMessage(
            MessageId: 0,
            SelfId: _options.SelfId,
            UserId: _options.OwnerId,
            UserName: "控制台",
            GroupId: 0,
            IsPrivate: true,
            PlainText: text,
            Segments: new JsonArray(),
            SessionKey: $"private:{_options.OwnerId}",
            IsOwner: true);

        string reply;
        try
        {
            reply = await cmd.ExecuteAsync(msg, args, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "控制台命令 {Prefix}{Name} 执行失败", _prefix, name);
            reply = $"命令 {_prefix}{name} 执行出错：{ex.Message}";
        }
        return (true, string.IsNullOrWhiteSpace(reply) ? null : reply);
    }

    /// <summary>发送命令回复（私聊引用回复；群聊引用 + @ 发送者）</summary>
    private async Task<bool> ReplyAsync(IncomingMessage msg, string reply, CancellationToken ct)
    {
        if (msg.IsPrivate)
        {
            await _client.SendPrivateMessageAsync(msg.UserId,
                [Segments.Reply((int)msg.MessageId), Segments.Text(reply)], ct);
        }
        else
        {
            await _client.SendGroupMessageAsync(msg.GroupId,
                [Segments.Reply((int)msg.MessageId), Segments.At(msg.UserId), Segments.Text(" " + reply)], ct);
        }
        return true;
    }
}
