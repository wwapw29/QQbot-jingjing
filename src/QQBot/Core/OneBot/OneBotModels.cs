using System.Text.Json.Nodes;

namespace QQBot.Core.OneBot;

/// <summary>
/// 经过解析、归一化后的入站消息（业务层只认这个模型，不直接碰协议 JSON）。
/// </summary>
public sealed record IncomingMessage(
    long MessageId,
    long SelfId,
    long UserId,
    string UserName,
    long GroupId,          // 群聊才有；私聊为 0
    bool IsPrivate,
    string PlainText,      // 纯文本内容（已去掉 @ 机器人 段）
    JsonArray Segments,    // 原始消息段（图片/表情等后续扩展用）
    string SessionKey,     // private:{uid} 或 group:{gid}
    bool IsOwner,          // 是否主人（OwnerId）
    long QuoteId = 0,      // 引用的消息 id（reply 段；0=无引用）
    List<string>? ImageUrls = null);   // 消息里的图片直链（image 段 url；识图模式用；无图为 null）

/// <summary>
/// OneBot 11 事件模型（只取本项目用到的字段，其余忽略）。
/// </summary>
public sealed record OneBotEvent(
    string PostType,       // message | notice | request | meta_event
    string? MessageType,   // private | group
    long SelfId,
    long UserId,
    string? UserName,      // 群名片/昵称（NapCat 扩展）
    long GroupId,
    int MessageId,
    JsonArray? Message,    // 消息段数组
    string? RawMessage);

public static class OneBotEventParser
{
    public static OneBotEvent? Parse(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        var postType = obj["post_type"]?.GetValue<string>();
        if (postType is null) return null;

        return new OneBotEvent(
            PostType: postType,
            MessageType: obj["message_type"]?.GetValue<string>(),
            SelfId: obj["self_id"]?.GetValue<long>() ?? 0,
            UserId: obj["user_id"]?.GetValue<long>() ?? 0,
            UserName: obj["sender"]?["card"]?.GetValue<string>()
                   ?? obj["sender"]?["nickname"]?.GetValue<string>(),
            GroupId: obj["group_id"]?.GetValue<long>() ?? 0,
            MessageId: obj["message_id"]?.GetValue<int>() ?? 0,
            Message: obj["message"] as JsonArray,
            RawMessage: obj["raw_message"]?.GetValue<string>());
    }
}
