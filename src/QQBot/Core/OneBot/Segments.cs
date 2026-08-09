using System.Text.Json.Nodes;

namespace QQBot.Core.OneBot;

/// <summary>
/// OneBot 11 消息段（segment）。
/// 文档：https://github.com/botuniverse/onebot-11
/// </summary>
public static class Segments
{
    /// <summary>纯文本段</summary>
    public static JsonObject Text(string text) => new()
    {
        ["type"] = "text",
        ["data"] = new JsonObject { ["text"] = text }
    };

    /// <summary>@ 某人段（qq=0 表示 @全体成员）</summary>
    public static JsonObject At(long qq) => new()
    {
        ["type"] = "at",
        ["data"] = new JsonObject { ["qq"] = qq.ToString() }
    };

    /// <summary>图片段。file 支持：本地绝对路径 / http(s) URL / base64:// 前缀</summary>
    public static JsonObject Image(string file) => new()
    {
        ["type"] = "image",
        ["data"] = new JsonObject { ["file"] = file }
    };

    /// <summary>回复引用段（被回复的消息 id）</summary>
    public static JsonObject Reply(int messageId) => new()
    {
        ["type"] = "reply",
        ["data"] = new JsonObject { ["id"] = messageId.ToString() }
    };
}
