namespace QQBot.Core.Pet;

/// <summary>
/// 桌面精灵的"她此刻在干什么"提示（给桌面端播动作帧用）。
///
/// 为什么需要它：主人在桌面上跟她说话时，桌面端是**阻塞**在 <c>POST /api/pet/chat</c> 上的，
/// 中间她调了哪些工具、查了多久，桌面端一无所知；而"工具绑定动作帧"要求工具被调用的**当时**就播对应动作。
/// 所以这里记下"最近一次工具调用 + 递增序号"，桌面端在等待回复期间轮询 <c>GET /api/pet/status</c>，
/// 序号一变就播绑定的那个动作，播完回到 thinking。
///
/// ⚠️ 只记**宠物会话**（sessionKey 以 <c>pet:</c> 开头）——QQ 聊天里她调工具跟桌面上的动作无关。
/// </summary>
public static class PetActionHint
{
    private static long _seq;
    private static string _tool = "";
    private static long _atTicks;

    /// <summary>工具执行成功时调用一次（由 <c>ToolRegistry</c> 统一埋点）</summary>
    public static void NoteToolCall(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return;
        _tool = toolName;
        Interlocked.Increment(ref _seq);
        Interlocked.Exchange(ref _atTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>调用序号（只会增；桌面端靠"序号变了"判断有没有新的工具调用）</summary>
    public static long Seq => Interlocked.Read(ref _seq);

    /// <summary>最近一次被调用的工具名</summary>
    public static string Tool => Volatile.Read(ref _tool);

    /// <summary>最近一次工具调用的时间（UTC；没调用过则是 MinValue）</summary>
    public static DateTime At
    {
        get
        {
            var t = Interlocked.Read(ref _atTicks);
            return t == 0 ? DateTime.MinValue : new DateTime(t, DateTimeKind.Utc);
        }
    }
}
