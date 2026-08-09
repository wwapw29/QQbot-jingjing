namespace QQBot.Core;

/// <summary>
/// 活动时钟（共享状态）：记录"最后一次用户消息处理时间"。
/// 自主活动（AutoActivity）用它做空闲检测——只有完全没人理静静超过设定时长，才触发自主行动。
/// </summary>
public static class ActivityClock
{
    /// <summary>最后一次处理用户消息的时间（UTC）；初始为进程启动时间</summary>
    public static DateTime LastUserMessageUtc { get; set; } = DateTime.UtcNow;

    /// <summary>记录一次消息响应（在消息真正进入处理流程时调用）</summary>
    public static void Touch() => LastUserMessageUtc = DateTime.UtcNow;
}
