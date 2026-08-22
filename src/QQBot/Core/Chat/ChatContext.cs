using System.Collections.Concurrent;
using QQBot.Core.Memory;

namespace QQBot.Core.Chat;

/// <summary>
/// 会话上下文管理器（SQLite 持久化版）：
///  - 内存缓存加速 + 每次追加同步落盘
///  - 会话无缓存时从数据库恢复最近消息（自定义聊天记录长度）
///  - 重启后静静依然记得历史聊天
/// </summary>
public sealed class ChatContext
{
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _cache = new();
    private readonly Database _db;
    private readonly int _maxMessages;
    private readonly object _lock = new();

    /// <summary>会话最近一次规划文本（内存级）：规划轮输出，供下次对话延续互动方向（如"欲擒故纵"）；
    /// 非持久化——重启后丢失，属可接受的短期张力</summary>
    private readonly ConcurrentDictionary<string, string> _lastPlanning = new();

    public ChatContext(Database db, int maxMessages)
    {
        _db = db;
        _maxMessages = maxMessages;
    }

    public void AppendUser(string sessionKey, string text, long? userId = null)
        => Append(sessionKey, "user", text, userId);

    public void AppendAssistant(string sessionKey, string text)
        => Append(sessionKey, "assistant", text, null);

    /// <summary>
    /// 按消息唯一键插入用户消息（INSERT OR IGNORE）：返回 false=该 msg_key 已存在（重放消息）→ 调用方跳过处理。
    /// 同时承担"落库 + 持久去重"双重职责：NapCat 重连重放的消息超过内存去重窗口后不会重复处理/重复回复。
    /// </summary>
    public bool InsertUserIfAbsent(string sessionKey, string msgKey, string text, long? userId)
    {
        var inserted = _db.InsertMessageIfAbsent(sessionKey, msgKey, "user", text, userId);
        if (inserted)
        {
            var list = GetOrLoad(sessionKey);
            lock (_lock)
            {
                list.Add(new ChatMessage("user", text) { UserId = userId });
                Trim(list);
            }
        }
        return inserted;
    }

    private void Append(string sessionKey, string role, string content, long? userId)
    {
        var list = GetOrLoad(sessionKey);
        lock (_lock)
        {
            list.Add(new ChatMessage(role, content) { UserId = userId });
            Trim(list);
        }
        _db.InsertMessage(sessionKey, role, content, userId);   // 同步落盘（含说话人）
    }

    /// <summary>
    /// 组装发给 LLM 的 messages：
    /// prepend（全局前置/系统提示词等，置于最前）+ 上下文历史 + append（全局后置提示词，置于最底部，离生成位置最近约束力最强）。
    /// </summary>
    public List<ChatMessage> BuildMessages(string sessionKey, IReadOnlyList<ChatMessage> prepend, IReadOnlyList<ChatMessage>? append = null)
    {
        var messages = new List<ChatMessage>(prepend);
        lock (_lock)
        {
            messages.AddRange(GetOrLoad(sessionKey));
        }
        if (append is not null && append.Count > 0)
        {
            messages.AddRange(append);
        }
        return messages;
    }

    /// <summary>保存会话最近一次规划（空文本=清除）</summary>
    public void SavePlanning(string sessionKey, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) _lastPlanning.TryRemove(sessionKey, out _);
        else _lastPlanning[sessionKey] = text;
    }

    /// <summary>取会话最近一次规划；无则 null</summary>
    public string? GetLastPlanning(string sessionKey)
        => _lastPlanning.TryGetValue(sessionKey, out var t) ? t : null;

    /// <summary>清空某会话（内存 + 数据库）</summary>
    public void Clear(string sessionKey)
    {
        _cache.TryRemove(sessionKey, out _);
        _lastPlanning.TryRemove(sessionKey, out _);
        _db.DeleteSession(sessionKey);
    }

    /// <summary>取会话列表：内存有则用，没有则从库恢复最近 N 条</summary>
    private List<ChatMessage> GetOrLoad(string sessionKey)
    {
        if (_cache.TryGetValue(sessionKey, out var list)) return list;

        var fromDb = _db.LoadRecentMessages(sessionKey, _maxMessages);
        _cache[sessionKey] = fromDb;
        return fromDb;
    }

    /// <summary>按自定义长度截取（保留最近 MaxContextMessages 条，保持 user/assistant 完整配对）</summary>
    private void Trim(List<ChatMessage> list)
    {
        if (list.Count <= _maxMessages) return;
        var keep = list.Skip(list.Count - _maxMessages).ToList();
        while (keep.Count > 1 && keep[0].Role == "assistant")
        {
            keep.RemoveAt(0);
        }
        list.Clear();
        list.AddRange(keep);
    }
}
