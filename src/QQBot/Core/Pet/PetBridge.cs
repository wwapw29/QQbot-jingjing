using System.Collections.Concurrent;
using QQBot.Core.Options;

namespace QQBot.Core.Pet;

/// <summary>
/// 宠物端要展示的一条消息（文字或图片）。
/// Type: "text" | "image"；image 时 DataUrl 为 data:image/png;base64,...，Caption 为图片说明。
/// </summary>
public sealed record PetReply(string Type, string? Text = null, string? DataUrl = null, string? Caption = null)
{
    public static PetReply Text0(string text) => new("text", Text: text);
    public static PetReply Image0(string dataUrl, string? caption) => new("image", DataUrl: dataUrl, Caption: caption);
}

/// <summary>
/// 单次宠物请求的"回复收集器"：把本来要发到 QQ 的内容收在这里，请求结束后一次性交给桌面端。
/// </summary>
public sealed class PetSink
{
    private readonly List<PetReply> _replies = new();
    private readonly object _lock = new();

    public void Add(PetReply reply)
    {
        lock (_lock) _replies.Add(reply);
    }

    public List<PetReply> Snapshot()
    {
        lock (_lock) return _replies.ToList();
    }
}

/// <summary>
/// 桌面精灵桥。管三件事：
///  1) **回复改道**：登记中的会话，回复不发 QQ 而是进 PetSink（见 Begin/TryGet）；
///  2) **在线状态**：桌面端轮询 inbox 时上报心跳，超时就视为"精灵不在"（自主活动据此选渠道）；
///  3) **主动消息**：她主动找主人时（自主活动），若精灵在线就投递到队列，等桌面端轮询取走。
/// 精灵不在时，调用方回落到 QQ 私聊，行为与从前一致。
/// </summary>
public sealed class PetBridge
{
    private readonly PetOptions _options;
    private readonly ConcurrentDictionary<string, PetSink> _sinks = new(StringComparer.Ordinal);

    /// <summary>宠物会话键前缀（pet:{ownerId}）；用它和 QQ 会话区分</summary>
    public const string SessionPrefix = "pet:";

    public PetBridge(PetOptions options) => _options = options;

    public static bool IsPetSession(string? sessionKey)
        => sessionKey is not null && sessionKey.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase);

    // ---------------- 1. 回复改道 ----------------

    /// <summary>登记一个宠物会话；Dispose 时注销（务必放在会话锁内使用）</summary>
    public IDisposable Begin(string sessionKey, PetSink sink)
    {
        _sinks[sessionKey] = sink;
        return new Registration(this, sessionKey);
    }

    public bool TryGet(string sessionKey, out PetSink sink) => _sinks.TryGetValue(sessionKey, out sink!);

    private sealed class Registration(PetBridge owner, string key) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            owner._sinks.TryRemove(key, out _);
        }
    }

    // ---------------- 2. 在线状态 ----------------

    private long _lastSeenTicks = DateTime.MinValue.Ticks;
    private volatile bool _explicitOffline = true;   // 启动时未知 → 先当作不在（她主动消息会先走 QQ，比较稳妥）

    /// <summary>超时秒数（面板可改；桌面端默认 3 秒一次轮询，15 秒没动静就算人不在）</summary>
    public int OfflineAfterSeconds => Math.Max(5, _options.OfflineAfterSeconds);

    /// <summary>桌面精灵是否在线（在电脑前）</summary>
    public bool IsOnline
    {
        get
        {
            if (_explicitOffline) return false;
            var last = new DateTime(Interlocked.Read(ref _lastSeenTicks), DateTimeKind.Utc);
            return DateTime.UtcNow - last < TimeSpan.FromSeconds(OfflineAfterSeconds);
        }
    }

    /// <summary>桌面端报活（每次轮询 inbox 时）</summary>
    public void MarkOnline()
    {
        _explicitOffline = false;
        Interlocked.Exchange(ref _lastSeenTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>桌面端主动说"我不在了"（托盘躲起来 / 正常退出时）</summary>
    public void MarkOffline() => _explicitOffline = true;

    public string StatusText => IsOnline ? "在线（主人在电脑前）" : "离线（主人不在电脑前）";

    // ---------------- 3. 主动消息队列 ----------------

    private readonly object _pushLock = new();
    private readonly List<(long Seq, DateTime AtUtc, PetReply Reply)> _pushes = new();
    private long _pushSeq;

    /// <summary>队列上限（防桌面端长时间不取导致堆积）</summary>
    private const int MaxQueued = 50;

    /// <summary>超过这个时间的旧消息直接丢掉（隔了半小时才弹一句很怪）</summary>
    private static readonly TimeSpan PushTtl = TimeSpan.FromMinutes(5);

    /// <summary>给她排队一条"主动找主人"的消息（只在 IsOnline 时调用；离线应回落 QQ）</summary>
    public void EnqueuePush(PetReply reply)
    {
        lock (_pushLock)
        {
            _pushes.Add((++_pushSeq, DateTime.UtcNow, reply));
            while (_pushes.Count > MaxQueued) _pushes.RemoveAt(0);
        }
    }

    /// <summary>取 seq &gt; since 的消息（顺带清理过期项）；lastSeq 供桌面端下次带上做去重</summary>
    public List<PetReply> PushesSince(long since, out long lastSeq)
    {
        var cutoff = DateTime.UtcNow - PushTtl;
        lock (_pushLock)
        {
            _pushes.RemoveAll(p => p.AtUtc < cutoff);
            lastSeq = _pushes.Count > 0 ? _pushes[^1].Seq : since;
            return _pushes.Where(p => p.Seq > since).Select(p => p.Reply).ToList();
        }
    }
}
