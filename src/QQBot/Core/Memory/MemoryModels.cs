namespace QQBot.Core.Memory;

/// <summary>
/// 记忆神经元：一条长期记忆（含唤起条件）。
/// scope: "global" = 通用记忆（所有用户共享）；"user" = 针对单个用户的独特记忆；"group" = 群聊记忆（绑定群号）。
/// trigger: 唤起条件（关键词列表，逗号分隔）；命中任一词即被唤起。
/// importance: 1~5，重要度越高越优先注入；≥ AlwaysInjectImportance 时总是注入（5 星常驻）。
/// use_count/last_used_at/decay_at：用进废退——被唤起升温、随时间衰减（惰性计算）。
/// </summary>
public sealed record MemoryRecord(
    long Id,
    string Scope,        // global | user | group
    long? QqId,          // scope=user 时的归属用户
    long? GroupId,       // scope=group 时的归属群
    string Content,      // 记忆内容
    string Trigger,      // 唤起条件（逗号分隔关键词，可空）
    int Importance,      // 1~5
    string? Category,    // 分类：偏好/事件/承诺/规则...
    int UseCount,        // 被唤起命中次数（用进废退）
    DateTime? LastUsedAt, // 最近一次被唤起时间
    DateTime? DecayAt,   // 上次衰减基准时间（惰性衰减用）
    DateTime UpdatedAt);

/// <summary>AI 总结输出的记忆条目（写入前）</summary>
public sealed record NewMemory(
    string Content,
    string Trigger,
    string Scope,        // global | user
    int Importance,
    string? Category,
    string[] RelatedTo,  // 关联的旧记忆内容（用于建神经链）
    long? UserQq = null);// 群聊中信息明确属于某人时填其 QQ（额外记一条该用户的记忆）
