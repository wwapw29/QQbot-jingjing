using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using QQBot.Core.Chat;

namespace QQBot.Core.Memory;

/// <summary>
/// SQLite 访问层（WAL 模式）。本项目唯一的数据落盘出口。
/// 表：Messages（消息历史）、Users（用户档案）。
/// </summary>
public sealed class Database
{
    private readonly string _connString;
    private readonly ILogger<Database> _logger;

    public Database(string dbPath, ILogger<Database> logger)
    {
        _logger = logger;
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        Init();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();
        // 并发写保护：多线程聊天时多个连接可能同时写，等待最多 5s 而不是立刻报锁错误
        using var busy = conn.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout = 5000;";
        busy.ExecuteNonQuery();
        return conn;
    }

    private void Init()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Messages (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                session_key TEXT    NOT NULL,
                role        TEXT    NOT NULL,
                content     TEXT    NOT NULL,
                user_id     INTEGER,
                msg_key     TEXT,                          -- 消息唯一键（如 group:{gid}:{message_id}），QQ 拉取去重用
                created_at  TEXT    NOT NULL DEFAULT (datetime('now','localtime'))
            );
            CREATE INDEX IF NOT EXISTS idx_messages_session ON Messages(session_key, id);
            -- 注意：idx_messages_key 唯一索引必须在下面 ALTER 补 msg_key 列之后再建（旧表提前建会报 no such column）

            CREATE TABLE IF NOT EXISTS Users (
                qq_id       INTEGER PRIMARY KEY,
                nickname    TEXT,
                affinity    INTEGER NOT NULL DEFAULT 0,
                tags        TEXT    NOT NULL DEFAULT '[]',
                profile     TEXT,
                first_seen  TEXT NOT NULL DEFAULT (datetime('now','localtime')),
                last_seen   TEXT NOT NULL DEFAULT (datetime('now','localtime'))
            );

            CREATE TABLE IF NOT EXISTS Memories (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                scope       TEXT    NOT NULL,           -- global | user | group
                qq_id       INTEGER,                    -- scope=user 时的归属用户
                group_id    INTEGER,                    -- scope=group 时的归属群
                content     TEXT    NOT NULL,
                trigger     TEXT    NOT NULL DEFAULT '',
                importance  INTEGER NOT NULL DEFAULT 1, -- 1~5
                category    TEXT,
                source_session TEXT,
                use_count   INTEGER NOT NULL DEFAULT 0,
                last_used_at TEXT,
                decay_at    TEXT,
                created_at  TEXT NOT NULL DEFAULT (datetime('now','localtime')),
                updated_at  TEXT NOT NULL DEFAULT (datetime('now','localtime'))
            );
            CREATE INDEX IF NOT EXISTS idx_memories_scope ON Memories(scope, qq_id);

            CREATE TABLE IF NOT EXISTS MemoryLinks (
                from_id INTEGER NOT NULL,
                to_id   INTEGER NOT NULL,
                weight  REAL    NOT NULL DEFAULT 1.0,
                PRIMARY KEY (from_id, to_id)
            );
            """;
        cmd.ExecuteNonQuery();

        // 兼容旧库：Messages 表缺 user_id 列时补上（SQLite 不支持 IF NOT EXISTS，需先检查）
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Messages') WHERE name='user_id';";
            var has = Convert.ToInt64(check.ExecuteScalar()) > 0;
            if (!has)
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = "ALTER TABLE Messages ADD COLUMN user_id INTEGER;";
                alter.ExecuteNonQuery();
            }
        }

        // 兼容旧库：Messages 表补 msg_key 列 + 唯一索引（QQ 拉取群消息去重用）
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Messages') WHERE name='msg_key';";
            if (Convert.ToInt64(check.ExecuteScalar()) == 0)
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = "ALTER TABLE Messages ADD COLUMN msg_key TEXT;";
                alter.ExecuteNonQuery();
            }
        }
        using (var keyIdx = conn.CreateCommand())
        {
            keyIdx.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_messages_key ON Messages(msg_key);";
            keyIdx.ExecuteNonQuery();
        }

        // 兼容旧库：Memories 表补新列（group_id / use_count / last_used_at / decay_at）
        foreach (var (col, ddl) in new[]
                 {
                     ("group_id", "INTEGER"),
                     ("use_count", "INTEGER NOT NULL DEFAULT 0"),
                     ("last_used_at", "TEXT"),
                     ("decay_at", "TEXT")
                 })
        {
            using var check = conn.CreateCommand();
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('Memories') WHERE name='{col}';";
            if (Convert.ToInt64(check.ExecuteScalar()) == 0)
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = $"ALTER TABLE Memories ADD COLUMN {col} {ddl};";
                alter.ExecuteNonQuery();
            }
        }

        // 旧表补列完成后才建 group 索引（否则旧表在列存在前建索引会报 no such column）
        using (var grpIdx = conn.CreateCommand())
        {
            grpIdx.CommandText = "CREATE INDEX IF NOT EXISTS idx_memories_group ON Memories(scope, group_id);";
            grpIdx.ExecuteNonQuery();
        }

        // 清理历史幽灵数据：旧版群聊总结写入的 user 记忆 qq_id 为 NULL，任何场景都检索不到（写了白写）
        using (var cleanup = conn.CreateCommand())
        {
            cleanup.CommandText = "DELETE FROM Memories WHERE scope='user' AND qq_id IS NULL;";
            var removed = cleanup.ExecuteNonQuery();
            if (removed > 0) _logger.LogInformation("已清理 {N} 条群聊幽灵记忆（旧版 qq_id 为 NULL 的 user 记忆）", removed);
        }

        _logger.LogInformation("数据库就绪: {Db}", _connString);
    }

    // ---------------- Messages ----------------

    public void InsertMessage(string sessionKey, string role, string content, long? userId = null)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO Messages(session_key, role, content, user_id) VALUES($s, $r, $c, $u);";
            cmd.Parameters.AddWithValue("$s", sessionKey);
            cmd.Parameters.AddWithValue("$r", role);
            cmd.Parameters.AddWithValue("$c", content);
            cmd.Parameters.AddWithValue("$u", (object?)userId ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入消息失败");
        }
    }

    /// <summary>
    /// 按消息唯一键插入（INSERT OR IGNORE）：QQ 拉取的群历史入库时去重——已存在（msg_key 相同）则跳过，
    /// 不存在则插入。返回是否新插入。
    /// </summary>
    public bool InsertMessageIfAbsent(string sessionKey, string msgKey, string role, string content, long? userId)
    {
        if (string.IsNullOrWhiteSpace(msgKey)) return false;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO Messages(session_key, role, content, user_id, msg_key)
                VALUES($s, $r, $c, $u, $k);
                """;
            cmd.Parameters.AddWithValue("$s", sessionKey);
            cmd.Parameters.AddWithValue("$r", role);
            cmd.Parameters.AddWithValue("$c", content);
            cmd.Parameters.AddWithValue("$u", (object?)userId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$k", msgKey);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "按唯一键写入消息失败");
            return false;
        }
    }

    /// <summary>加载某会话最近 count 条消息（按时间升序）</summary>
    public List<ChatMessage> LoadRecentMessages(string sessionKey, int count)
    {
        var result = new List<ChatMessage>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT role, content, user_id FROM (
                    SELECT id, role, content, user_id FROM Messages
                    WHERE session_key = $s
                    ORDER BY id DESC LIMIT $n
                ) ORDER BY id ASC;
                """;
            cmd.Parameters.AddWithValue("$s", sessionKey);
            cmd.Parameters.AddWithValue("$n", count);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new ChatMessage(reader.GetString(0), reader.GetString(1))
                {
                    UserId = reader.IsDBNull(2) ? null : reader.GetInt64(2)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载消息历史失败");
        }
        return result;
    }

    /// <summary>加载某会话最近 count 条消息（**新→旧**，最新一条在最前；get_chat_history 工具用）</summary>
    public List<ChatMessage> LoadRecentMessagesDesc(string sessionKey, int count)
    {
        var result = new List<ChatMessage>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT role, content, user_id FROM Messages
                WHERE session_key = $s
                ORDER BY id DESC LIMIT $n;
                """;
            cmd.Parameters.AddWithValue("$s", sessionKey);
            cmd.Parameters.AddWithValue("$n", count);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new ChatMessage(reader.GetString(0), reader.GetString(1))
                {
                    UserId = reader.IsDBNull(2) ? null : reader.GetInt64(2)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载消息历史失败");
        }
        return result;
    }

    /// <summary>加载某会话最近 count 条消息（**新→旧**），带消息 id（重新生成命令用）</summary>
    public List<(long Id, string Role, string Content, long? UserId)> LoadRecentMessagesWithId(string sessionKey, int count)
    {
        var result = new List<(long, string, string, long?)>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, role, content, user_id FROM Messages
                WHERE session_key = $s
                ORDER BY id DESC LIMIT $n;
                """;
            cmd.Parameters.AddWithValue("$s", sessionKey);
            cmd.Parameters.AddWithValue("$n", count);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                            reader.IsDBNull(3) ? null : reader.GetInt64(3)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载消息历史失败");
        }
        return result;
    }

    /// <summary>按 id 删除一条消息（重新生成命令用）</summary>
    public void DeleteMessageById(long id)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Messages WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除消息失败（id={Id}）", id);
        }
    }

    public void DeleteSession(string sessionKey)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Messages WHERE session_key = $s;";
            cmd.Parameters.AddWithValue("$s", sessionKey);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除会话失败");
        }
    }

    /// <summary>清理超过保留天数的历史消息（0 = 跳过）</summary>
    public void CleanupOldMessages(int retentionDays)
    {
        if (retentionDays <= 0) return;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Messages WHERE created_at < datetime('now','localtime', $d);";
            cmd.Parameters.AddWithValue("$d", $"-{retentionDays} days");
            var n = cmd.ExecuteNonQuery();
            if (n > 0) _logger.LogInformation("已清理 {N} 条过期消息", n);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清理旧消息失败");
        }
    }

    // ---------------- Memories（神经链记忆） ----------------

    /// <summary>写入一条记忆，返回新 id；若 content 已存在（同 scope+qq/group）则更新时间戳并返回旧 id</summary>
    public long UpsertMemory(string scope, long? qqId, long? groupId, string content, string trigger,
                             int importance, string? category, string? sourceSession = null)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Memories(scope, qq_id, group_id, content, trigger, importance, category, source_session)
                VALUES($scope, $qq, $grp, $content, $trigger, $imp, $cat, $src)
                ON CONFLICT(id) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("$scope", scope);
            cmd.Parameters.AddWithValue("$qq", (object?)qqId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$grp", (object?)groupId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$content", content);
            cmd.Parameters.AddWithValue("$trigger", trigger);
            cmd.Parameters.AddWithValue("$imp", importance);
            cmd.Parameters.AddWithValue("$cat", (object?)category ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$src", (object?)sourceSession ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            using (var idCmd = conn.CreateCommand())
            {
                idCmd.CommandText = "SELECT last_insert_rowid();";
                return Convert.ToInt64(idCmd.ExecuteScalar());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入记忆失败");
            return 0;
        }
    }

    /// <summary>记忆列清单（所有记忆查询统一使用，保证 ReadMemory 读列索引一致）</summary>
    private const string MemCols =
        "id, scope, qq_id, group_id, content, trigger, importance, category, use_count, last_used_at, decay_at, updated_at";

    /// <summary>按触发词检索种子记忆（scope 过滤 + trigger 关键词包含匹配）</summary>
    public List<MemoryRecord> SearchMemoriesByTrigger(long? qqId, string userText, int limit)
    {
        var result = new List<MemoryRecord>();
        try
        {
            var keywords = userText.Split([' ', '，', '。', '！', '？', '、', ',', '.', '!', '?'], StringSplitOptions.RemoveEmptyEntries);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT {MemCols}
                FROM Memories WHERE importance > 0
                ORDER BY importance DESC, updated_at DESC
                LIMIT 200;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read() && result.Count < limit)
            {
                var rec = ReadMemory(reader);
                if (!ScopeMatches(rec, qqId, groupId: null)) continue;
                if (TriggerHits(rec.Trigger, userText, keywords))
                {
                    result.Add(rec);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检索记忆失败");
        }
        return result;
    }

    /// <summary>取重要度 ≥ minImportance 的记忆（保底常驻注入）</summary>
    public List<MemoryRecord> LoadImportantMemories(long? qqId, int minImportance, int limit)
    {
        var result = new List<MemoryRecord>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT {MemCols}
                FROM Memories WHERE importance >= $min
                ORDER BY importance DESC, updated_at DESC
                LIMIT $lim;
                """;
            cmd.Parameters.AddWithValue("$min", minImportance);
            cmd.Parameters.AddWithValue("$lim", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var rec = ReadMemory(reader);
                if (ScopeMatches(rec, qqId, groupId: null)) result.Add(rec);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载重要记忆失败");
        }
        return result;
    }

    /// <summary>从种子记忆出发，沿 MemoryLinks 做链式扩展（BFS，最多 maxHops 跳）</summary>
    public List<MemoryRecord> ExpandMemoryLinks(List<long> seedIds, long? qqId, int maxHops, int limit)
    {
        var result = new List<MemoryRecord>();
        if (seedIds.Count == 0) return result;
        var seen = new HashSet<long>(seedIds);
        var frontier = new HashSet<long>(seedIds);
        try
        {
            using var conn = Open();
            for (int hop = 0; hop < maxHops && result.Count < limit && frontier.Count > 0; hop++)
            {
                var ids = frontier.ToList();
                var seenList = seen.ToList();
                var idParams = string.Join(",", ids.Select((_, i) => $"$f{i}"));
                var seenParams = string.Join(",", seenList.Select((_, i) => $"$s{i}"));
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    SELECT m.id, m.scope, m.qq_id, m.group_id, m.content, m.trigger, m.importance,
                           m.category, m.use_count, m.last_used_at, m.decay_at, m.updated_at
                    FROM Memories m
                    JOIN MemoryLinks l ON l.to_id = m.id
                    WHERE l.from_id IN ({idParams}) AND m.id NOT IN ({seenParams})
                    ORDER BY l.weight DESC, m.importance DESC;
                    """;
                for (int i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue($"$f{i}", ids[i]);
                for (int i = 0; i < seenList.Count; i++) cmd.Parameters.AddWithValue($"$s{i}", seenList[i]);
                var next = new HashSet<long>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read() && result.Count < limit)
                {
                    var rec = ReadMemory(reader);
                    if (!ScopeMatches(rec, qqId, groupId: null)) continue;
                    result.Add(rec);
                    next.Add(rec.Id);
                    seen.Add(rec.Id);
                }
                frontier = next;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "链式扩展记忆失败");
        }
        return result;
    }

    /// <summary>
    /// 两步定位第一步：按上下文标识精确取候选记忆（SQL 走索引）。
    /// 私聊：user(对方QQ) ∪ global；群聊：group(本群) ∪ user(说话人) ∪ 提及他人的 user 记忆 ∪ global。
    /// 返回按重要度/时间排序的候选集（上限 limit），语义筛选在内存中做。
    /// </summary>
    public List<MemoryRecord> GetContextMemories(long? qqId, long? groupId, long[]? mentionedQqIds, int limit)
    {
        var result = new List<MemoryRecord>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            var users = new List<long>();
            if (qqId.HasValue) users.Add(qqId.Value);
            if (mentionedQqIds is not null) users.AddRange(mentionedQqIds.Where(u => u > 0 && u != qqId));
            users = users.Distinct().ToList();

            var conds = new List<string> { "scope = 'global'" };
            if (groupId.HasValue)
            {
                // 群聊：群层面记忆（qq_id 为空）对全群生效；带 qq_id 的群记忆仅对该群的该用户生效
                conds.Add("(scope = 'group' AND group_id = $grp AND qq_id IS NULL)");
                if (users.Count > 0)
                    conds.Add($"(scope = 'group' AND group_id = $grp AND qq_id IN ({string.Join(",", users.Select((_, i) => $"$u{i}"))}))");
            }
            else if (users.Count > 0)
            {
                // 私聊（无群）：可想起该用户在其参与过的任意群里的专属记忆（qq_id 隔离人，
                // 不注入群层面记忆——那是全群的，不该进私聊；群与群之间按人隔离）
                conds.Add($"(scope = 'group' AND qq_id IN ({string.Join(",", users.Select((_, i) => $"$u{i}"))}))");
            }
            if (users.Count > 0) conds.Add($"(scope = 'user' AND qq_id IN ({string.Join(",", users.Select((_, i) => $"$u{i}"))}))");
            cmd.CommandText = $"""
                SELECT {MemCols}
                FROM Memories
                WHERE ({string.Join(" OR ", conds)}) AND importance > 0
                ORDER BY importance DESC, updated_at DESC
                LIMIT $lim;
                """;
            if (groupId.HasValue) cmd.Parameters.AddWithValue("$grp", groupId.Value);
            for (int i = 0; i < users.Count; i++) cmd.Parameters.AddWithValue($"$u{i}", users[i]);
            cmd.Parameters.AddWithValue("$lim", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result.Add(ReadMemory(reader));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "上下文记忆定位失败");
        }
        return result;
    }

    /// <summary>在指定归属域内找与内容最相似的记忆（2-gram Jaccard）；相似度 ≥ threshold 返回该记忆，否则 null</summary>
    public MemoryRecord? FindSimilarMemory(string scope, long? qqId, long? groupId, string content, double threshold)
    {
        try
        {
            var grams = Grams(content);
            if (grams.Count == 0) return null;
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            var sql = $"SELECT {MemCols} FROM Memories WHERE scope = $scope";
            if (scope == "user") sql += " AND qq_id = $qq";
            else if (scope == "group")
            {
                sql += " AND group_id = $grp";
                // 群记忆带 qq_id（群内某人）时去重域限定该人；不带则群层面
                if (qqId.HasValue) sql += " AND qq_id = $gqq";
                else sql += " AND qq_id IS NULL";
            }
            sql += " ORDER BY updated_at DESC LIMIT 500;";
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$scope", scope);
            if (scope == "user") cmd.Parameters.AddWithValue("$qq", qqId ?? -1);
            else if (scope == "group")
            {
                cmd.Parameters.AddWithValue("$grp", groupId ?? -1);
                if (qqId.HasValue) cmd.Parameters.AddWithValue("$gqq", qqId.Value);
            }
            using var reader = cmd.ExecuteReader();
            MemoryRecord? best = null;
            double bestScore = 0;
            while (reader.Read())
            {
                var rec = ReadMemory(reader);
                var other = Grams(rec.Content);
                if (other.Count == 0) continue;
                var inter = grams.Intersect(other).Count();
                var score = (double)inter / (grams.Count + other.Count - inter);
                if (score > bestScore) { bestScore = score; best = rec; }
            }
            return bestScore >= threshold ? best : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "记忆相似度匹配失败");
            return null;
        }
    }

    /// <summary>记忆被唤起命中：升温（use_count++、刷新 last_used_at）</summary>
    public void TouchMemory(long id)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Memories SET use_count = use_count + 1, last_used_at = datetime('now','localtime') WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "记忆升温失败");
        }
    }

    /// <summary>合并更新记忆内容（去重合并用：保留旧 id，刷新内容/触发词/重要度/时间）</summary>
    public void UpdateMemoryContent(long id, string content, string trigger, int importance)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Memories SET content=$c, trigger=$t, importance=$i, updated_at=datetime('now','localtime') WHERE id=$id;";
            cmd.Parameters.AddWithValue("$c", content);
            cmd.Parameters.AddWithValue("$t", trigger);
            cmd.Parameters.AddWithValue("$i", importance);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "合并更新记忆失败");
        }
    }

    /// <summary>文本拆成相邻两字组合（2-gram），中文语义相似度计算用</summary>
    internal static HashSet<string> Grams(string text)
    {
        var set = new HashSet<string>();
        var chars = text.Where(c => !char.IsWhiteSpace(c) && c != '，' && c != '。' && c != '、' && c != '!' && c != '?').ToList();
        for (int i = 0; i < chars.Count - 1; i++)
        {
            set.Add($"{chars[i]}{chars[i + 1]}");
        }
        return set;
    }

    /// <summary>为两条记忆建立关联边（双向）</summary>
    /// <summary>图谱默认根：取重要度最高的记忆 id</summary>
    public long TopImportanceMemoryId()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM Memories ORDER BY importance DESC, updated_at DESC LIMIT 1;";
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
        catch (Exception ex) { _logger.LogError(ex, "查询最高重要度记忆失败"); return 0; }
    }

    /// <summary>全量记忆图谱：所有记忆节点 + 全部关联边（去重双向，3D 球状图用）</summary>
    public (List<MemoryRecord> Nodes, List<(long From, long To, double Weight)> Links) LoadFullGraph(int limit = 300)
    {
        var nodes = new List<MemoryRecord>();
        var links = new List<(long, long, double)>();
        try
        {
            using var conn = Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT {MemCols} FROM Memories ORDER BY importance DESC, updated_at DESC LIMIT $lim;";
                cmd.Parameters.AddWithValue("$lim", limit);
                using var r = cmd.ExecuteReader();
                while (r.Read()) nodes.Add(ReadMemory(r));
            }
            using (var cmd = conn.CreateCommand())
            {
                // 双向成对边取小→大一条（无向图视角），避免 3D 图重复连线
                cmd.CommandText = "SELECT from_id, to_id, weight FROM MemoryLinks WHERE from_id < to_id;";
                using var r = cmd.ExecuteReader();
                while (r.Read()) links.Add((r.GetInt64(0), r.GetInt64(1), r.GetDouble(2)));
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "全量记忆图谱查询失败"); }
        return (nodes, links);
    }

    /// <summary>
    /// 记忆图谱查询：从根记忆 BFS 收集多层关联（返回节点列表 + 走过的边）。
    /// 管理视图不过滤归属（主人要看全部关联）。
    /// </summary>
    public (List<MemoryRecord> Nodes, List<(long From, long To, double Weight)> Links) LoadMemoryGraph(long rootId, int maxHops, int limit)
    {
        var nodes = new List<MemoryRecord>();
        var links = new List<(long, long, double)>();
        try
        {
            var root = GetMemoryById(rootId);
            if (root is null) return (nodes, links);
            nodes.Add(root);
            var seen = new HashSet<long> { rootId };
            var frontier = new List<long> { rootId };
            using var conn = Open();
            for (int hop = 0; hop < maxHops && frontier.Count > 0 && nodes.Count < limit; hop++)
            {
                var ids = frontier;
                frontier = new List<long>();
                var idParams = string.Join(",", ids.Select((_, i) => $"$f{i}"));
                var seenParams = string.Join(",", seen.Select((_, i) => $"$s{i}"));
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    SELECT m.id, m.scope, m.qq_id, m.group_id, m.content, m.trigger, m.importance,
                           m.category, m.use_count, m.last_used_at, m.decay_at, m.updated_at,
                           l.from_id, l.weight
                    FROM Memories m
                    JOIN MemoryLinks l ON (l.to_id = m.id AND l.from_id IN ({idParams}))
                    WHERE m.id NOT IN ({seenParams})
                    ORDER BY l.weight DESC, m.importance DESC;
                    """;
                for (int i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue($"$f{i}", ids[i]);
                for (int i = 0; i < seen.Count; i++) cmd.Parameters.AddWithValue($"$s{i}", seen.ToList()[i]);
                using var reader = cmd.ExecuteReader();
                while (reader.Read() && nodes.Count < limit)
                {
                    var rec = ReadMemory(reader);
                    var from = reader.GetInt64(12);
                    var w = reader.GetDouble(13);
                    nodes.Add(rec);
                    links.Add((from, rec.Id, w));
                    frontier.Add(rec.Id);
                    seen.Add(rec.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "记忆图谱查询失败（root={Root}）", rootId);
        }
        return (nodes, links);
    }

    /// <summary>
    /// 一键自动建边：对现有记忆两两计算关联度（触发词重叠 + 同分类 + 内容相似度），
    /// 超过阈值且无现存边则写入 MemoryLinks（双向）。返回新建边数。
    /// </summary>
    public int AutoLinkMemories(double threshold = 0.25)
    {
        int created = 0;
        try
        {
            // 清理孤儿边（指向已删除/迁移前记忆的残留）
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM MemoryLinks WHERE from_id NOT IN (SELECT id FROM Memories) OR to_id NOT IN (SELECT id FROM Memories);";
                cmd.ExecuteNonQuery();
            }

            var all = new List<MemoryRecord>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT {MemCols} FROM Memories;";
                using var r = cmd.ExecuteReader();
                while (r.Read()) all.Add(ReadMemory(r));
            }
            if (all.Count < 2) return 0;

            var existing = new HashSet<(long, long)>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT from_id, to_id FROM MemoryLinks;";
                using var r = cmd.ExecuteReader();
                while (r.Read()) existing.Add((r.GetInt64(0), r.GetInt64(1)));
            }

            for (int i = 0; i < all.Count; i++)
            {
                for (int j = i + 1; j < all.Count; j++)
                {
                    var score = LinkScore(all[i], all[j]);
                    if (score < threshold) continue;
                    var a = all[i].Id; var b = all[j].Id;
                    // 双向写边（图谱按 from_id 展开，两个方向都要有，与 AI 总结建的成对边一致）；
                    // 只补缺失方向，已有的方向不动
                    var needA = !existing.Contains((a, b));
                    var needB = !existing.Contains((b, a));
                    if (needA || needB)
                    {
                        var w = Math.Min(1.0, score);
                        var ok = (!needA || LinkMemoriesCore(a, b, w)) & (!needB || LinkMemoriesCore(b, a, w));
                        if (ok)
                        {
                            created++;
                            existing.Add((a, b)); existing.Add((b, a));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "记忆自动建边失败");
        }
        return created;
    }

    /// <summary>记忆关联度打分：触发词重叠 + 同分类 + 内容 2-gram Jaccard（与图谱 demo 一致）</summary>
    private static double LinkScore(MemoryRecord a, MemoryRecord b)
    {
        double s = 0;
        var ta = SplitTrigger(a.Trigger);
        var tb = SplitTrigger(b.Trigger);
        foreach (var t in ta) if (tb.Contains(t)) s += 0.4;
        if (!string.IsNullOrWhiteSpace(a.Category) && a.Category == b.Category) s += 0.15;
        var ga = Grams(a.Content); var gb = Grams(b.Content);
        if (ga.Count > 0 && gb.Count > 0)
        {
            var inter = ga.Intersect(gb).Count();
            s += (double)inter / (ga.Count + gb.Count - inter) * 0.8;
        }
        return s;
    }

    private static HashSet<string> SplitTrigger(string trigger)
        => trigger.Split([',', '，', ';', '；', '、', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length >= 2).ToHashSet();

    /// <summary>写边（不查重，供批量建边用）</summary>
    private bool LinkMemoriesCore(long fromId, long toId, double weight)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO MemoryLinks(from_id, to_id, weight) VALUES($f, $t, $w);";
            cmd.Parameters.AddWithValue("$f", fromId);
            cmd.Parameters.AddWithValue("$t", toId);
            cmd.Parameters.AddWithValue("$w", weight);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入记忆关联边失败");
            return false;
        }
    }

    public void LinkMemories(long fromId, long toId, double weight = 1.0)
    {
        if (fromId <= 0 || toId <= 0 || fromId == toId) return;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO MemoryLinks(from_id, to_id, weight) VALUES($a, $b, $w);
                INSERT OR IGNORE INTO MemoryLinks(from_id, to_id, weight) VALUES($b, $a, $w);
                """;
            cmd.Parameters.AddWithValue("$a", fromId);
            cmd.Parameters.AddWithValue("$b", toId);
            cmd.Parameters.AddWithValue("$w", weight);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "建立记忆关联失败");
        }
    }

    /// <summary>列出记忆（供查看命令使用）：includeGlobal=true 时同时列出通用记忆</summary>
    public List<MemoryRecord> LoadMemories(long? qqId, int limit, bool includeGlobal)
    {
        var result = new List<MemoryRecord>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = includeGlobal
                ? $"SELECT {MemCols} FROM Memories ORDER BY importance DESC, updated_at DESC LIMIT $lim;"
                : $"SELECT {MemCols} FROM Memories WHERE qq_id = $id ORDER BY importance DESC, updated_at DESC LIMIT $lim;";
            if (!includeGlobal) cmd.Parameters.AddWithValue("$id", qqId ?? -1);
            cmd.Parameters.AddWithValue("$lim", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(ReadMemory(reader));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载记忆列表失败");
        }
        return result;
    }

    /// <summary>按 scope/归属查询记忆（供查看命令用）：scope=null 全部；qqId=null 不限制归属</summary>
    public List<MemoryRecord> LoadMemoriesByScope(string? scope, long? qqId, int limit)
    {
        var result = new List<MemoryRecord>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            var sql = $"SELECT {MemCols} FROM Memories WHERE 1=1";
            if (scope != null) sql += " AND scope = $scope";
            if (qqId != null) sql += " AND qq_id = $qq";
            sql += " ORDER BY importance DESC, updated_at DESC LIMIT $lim;";
            cmd.CommandText = sql;
            if (scope != null) cmd.Parameters.AddWithValue("$scope", scope);
            if (qqId != null) cmd.Parameters.AddWithValue("$qq", qqId);
            cmd.Parameters.AddWithValue("$lim", limit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(ReadMemory(reader));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载记忆列表失败");
        }
        return result;
    }

    /// <summary>按 id 查单条记忆（精细操作用）</summary>
    public MemoryRecord? GetMemoryById(long id)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT {MemCols}
                FROM Memories WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadMemory(reader) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查询记忆失败");
            return null;
        }
    }

    /// <summary>取某会话最近写入的一条记忆（记忆纠错"不用记/记错了"用）</summary>
    public MemoryRecord? GetLatestMemoryBySession(string sessionKey)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT {MemCols}
                FROM Memories WHERE source_session = $s
                ORDER BY id DESC LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$s", sessionKey);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadMemory(reader) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查询会话最近记忆失败");
            return null;
        }
    }

    /// <summary>按 id 删除单条记忆（连带清理关联边）</summary>
    public bool DeleteMemoryById(long id)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Memories WHERE id=$id; DELETE FROM MemoryLinks WHERE from_id=$id OR to_id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除记忆失败");
            return false;
        }
    }

    /// <summary>后台面板：已知用户列表（Users 表 + 记忆里出现过的 qq_id，去重）</summary>
    public List<(long Qq, string? Nick)> ListKnownUsers()
    {
        var result = new List<(long, string?)>();
        var seen = new HashSet<long>();
        try
        {
            using var conn = Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT qq_id, nickname FROM Users ORDER BY last_seen DESC;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var qq = r.GetInt64(0);
                    if (seen.Add(qq)) result.Add((qq, r.IsDBNull(1) ? null : r.GetString(1)));
                }
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT qq_id FROM Memories WHERE qq_id IS NOT NULL;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var qq = r.GetInt64(0);
                    if (seen.Add(qq)) result.Add((qq, null));
                }
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "查询已知用户列表失败"); }
        return result;
    }

    /// <summary>后台面板：已知群列表（记忆里出现过的群号 + 消息会话里的群号，去重）</summary>
    public List<long> ListKnownGroups()
    {
        var result = new List<long>();
        var seen = new HashSet<long>();
        try
        {
            using var conn = Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT group_id FROM Memories WHERE group_id IS NOT NULL;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var g = r.GetInt64(0);
                    if (seen.Add(g)) result.Add(g);
                }
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT session_key FROM Messages WHERE session_key LIKE 'group:%';";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var key = r.GetString(0);
                    if (key.Length > 6 && long.TryParse(key[6..], out var g) && seen.Add(g)) result.Add(g);
                }
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "查询已知群列表失败"); }
        return result;
    }

    /// <summary>后台面板：记忆分页列表（scope/归属/关键词筛选，按更新时间倒序）</summary>
    public (List<MemoryRecord> Records, long Total) ListMemories(string? scope, long? qqId, long? groupId, string? keyword, int page, int size)
    {
        var records = new List<MemoryRecord>();
        long total = 0;
        try
        {
            var where = new System.Text.StringBuilder("WHERE 1=1");
            var args = new List<(string, object?)>();
            // param=参数名（$xxx），expr=SQL 片段；两者必须分开，否则参数名会带上表达式导致匹配失败
            void Add(string param, string expr, object? v) { where.Append(" AND ").Append(expr); args.Add((param, v)); }
            if (!string.IsNullOrWhiteSpace(scope)) Add("$scope", "scope=$scope", scope.Trim());
            if (qqId.HasValue && qqId > 0) Add("$qq", "qq_id=$qq", qqId.Value);
            if (groupId.HasValue && groupId > 0) Add("$grp", "group_id=$grp", groupId.Value);
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                where.Append(" AND (content LIKE $kw OR trigger LIKE $kw OR category LIKE $kw)");
                args.Add(("$kw", "%" + keyword.Trim() + "%"));
            }

            using var conn = Open();
            using (var cnt = conn.CreateCommand())
            {
                cnt.CommandText = "SELECT COUNT(*) FROM Memories " + where + ";";
                foreach (var (k, v) in args) cnt.Parameters.AddWithValue(k, v ?? DBNull.Value);
                total = Convert.ToInt64(cnt.ExecuteScalar());
            }
            using var cmd = conn.CreateCommand();
            var page0 = Math.Max(0, page);
            var size0 = Math.Clamp(size, 1, 200);
            cmd.CommandText = $"""
                SELECT {MemCols} FROM Memories {where}
                ORDER BY updated_at DESC, id DESC
                LIMIT $size OFFSET $off;
                """;
            foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$size", size0);
            cmd.Parameters.AddWithValue("$off", page0 * size0);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) records.Add(ReadMemory(reader));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "记忆分页列表查询失败");
        }
        return (records, total);
    }

    /// <summary>后台面板：全字段更新记忆（内容/触发词/星级/归属；只更新非 null 字段）</summary>
    public bool UpdateMemoryFull(long id, string? content, string? trigger, int? importance, string? scope, long? qqId, long? groupId)
    {
        try
        {
            var sets = new System.Text.StringBuilder("updated_at=datetime('now','localtime')");
            var args = new List<(string, object?)>();
            void Add(string k, object? v, string expr) { sets.Append(", ").Append(expr); args.Add((k, v)); }
            if (content is not null) Add("$c", content, "content=$c");
            if (trigger is not null) Add("$t", trigger, "trigger=$t");
            if (importance.HasValue) Add("$i", importance.Value, "importance=$i");
            if (scope is not null)
            {
                Add("$s", scope, "scope=$s");
                // 切换归属时同步清理归属字段
                Add("$q", qqId, "qq_id=$q");
                Add("$g", groupId, "group_id=$g");
            }

            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE Memories SET {sets} WHERE id=$id;";
            foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新记忆失败（id={Id}）", id);
            return false;
        }
    }

    /// <summary>修改记忆归属（user ↔ global）</summary>
    public bool UpdateMemoryScope(long id, string scope, long? qqId)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Memories SET scope=$s, qq_id=$q, updated_at=datetime('now','localtime') WHERE id=$id;";
            cmd.Parameters.AddWithValue("$s", scope);
            cmd.Parameters.AddWithValue("$q", (object?)qqId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "修改记忆归属失败");
            return false;
        }
    }

    /// <summary>修改记忆重要度</summary>
    public bool UpdateMemoryImportance(long id, int importance)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Memories SET importance=$i, updated_at=datetime('now','localtime') WHERE id=$id;";
            cmd.Parameters.AddWithValue("$i", Math.Clamp(importance, 1, 5));
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "修改记忆重要度失败");
            return false;
        }
    }

    /// <summary>按内容模糊查记忆 id（用于 AI 总结的 related_to 建链）</summary>
    public long FindMemoryIdByContent(long? qqId, string contentFragment)    {
        try
        {
            var frag = contentFragment.Length > 60 ? contentFragment[..60] : contentFragment;
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id FROM Memories
                WHERE content LIKE $frag
                ORDER BY (scope = 'global') DESC, importance DESC, updated_at DESC
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$frag", $"%{frag}%");
            var val = cmd.ExecuteScalar();
            return val is long l ? l : (val is int i ? i : 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "按内容查记忆失败");
            return 0;
        }
    }

    private static MemoryRecord ReadMemory(System.Data.Common.DbDataReader r) => new(
        Id: r.GetInt64(0),
        Scope: r.GetString(1),
        QqId: r.IsDBNull(2) ? null : r.GetInt64(2),
        GroupId: r.IsDBNull(3) ? null : r.GetInt64(3),
        Content: r.GetString(4),
        Trigger: r.IsDBNull(5) ? "" : r.GetString(5),
        Importance: r.GetInt32(6),
        Category: r.IsDBNull(7) ? null : r.GetString(7),
        UseCount: r.IsDBNull(8) ? 0 : r.GetInt32(8),
        LastUsedAt: r.IsDBNull(9) ? null : DateTime.TryParse(r.GetString(9), out var lu) ? lu : null,
        DecayAt: r.IsDBNull(10) ? null : DateTime.TryParse(r.GetString(10), out var dc) ? dc : null,
        UpdatedAt: DateTime.TryParse(r.GetString(11), out var up) ? up : DateTime.MinValue);

    /// <summary>
    /// scope 匹配：global 通用对所有人生效；user 只对归属用户生效；
    /// group 只对归属群生效——群记忆若带 qq_id（群内某人），则仅对该群的该用户命中。
    /// </summary>
    private static bool ScopeMatches(MemoryRecord rec, long? qqId, long? groupId)
        => rec.Scope == "global"
           || (rec.Scope == "user" && rec.QqId.HasValue && rec.QqId == qqId)
           || (rec.Scope == "group" && rec.GroupId.HasValue && rec.GroupId == groupId
               && (!rec.QqId.HasValue || rec.QqId == qqId));

    /// <summary>触发词命中：trigger 中的任一词出现在用户消息里（子串匹配）</summary>
    private static bool TriggerHits(string trigger, string userText, string[] tokens)
    {
        if (string.IsNullOrWhiteSpace(trigger)) return false;
        var kws = trigger.Split([',', '，', ';', '；', '、', ' '], StringSplitOptions.RemoveEmptyEntries);
        foreach (var kw in kws)
        {
            if (userText.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>记录用户出现（昵称/活跃时间），新用户则建档</summary>
    public void TouchUser(long qqId, string? nickname)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Users(qq_id, nickname) VALUES($id, $nick)
                ON CONFLICT(qq_id) DO UPDATE SET
                    nickname = CASE WHEN $nick IS NOT NULL AND $nick <> '' THEN $nick ELSE nickname END,
                    last_seen = datetime('now','localtime');
                """;
            cmd.Parameters.AddWithValue("$id", qqId);
            cmd.Parameters.AddWithValue("$nick", (object?)nickname ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新用户档案失败");
        }
    }

    // ---------------- 统计与清理（供主人命令使用） ----------------

    /// <summary>删除某用户的记忆（scope=user）；globalToo=true 时连同通用记忆一起删</summary>
    public int DeleteMemories(long? qqId, bool globalToo)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = globalToo
                ? "DELETE FROM Memories;"
                : "DELETE FROM Memories WHERE qq_id = $id;";
            if (!globalToo) cmd.Parameters.AddWithValue("$id", qqId ?? -1);
            return cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除记忆失败");
            return 0;
        }
    }

    /// <summary>删除某用户的聊天记录（Messages）</summary>
    public int DeleteMessagesByUser(long qqId)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Messages WHERE session_key LIKE $s;";
            cmd.Parameters.AddWithValue("$s", $"private:{qqId}");
            return cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除聊天记录失败");
            return 0;
        }
    }

    public long CountMemories() => Scalar("SELECT COUNT(*) FROM Memories;");
    public long CountMessages() => Scalar("SELECT COUNT(*) FROM Messages;");
    public long CountSessions() => Scalar("SELECT COUNT(DISTINCT session_key) FROM Messages;");

    /// <summary>后台面板统计：已见过的人数（Users 表）</summary>
    public long CountUsers() => Scalar("SELECT COUNT(*) FROM Users;");

    /// <summary>后台面板统计：近 days 天每天消息数（旧→新，缺的天由调用方补 0）</summary>
    public List<(string Day, long Count)> MessageTrendByDay(int days)
    {
        var result = new List<(string, long)>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT date(created_at) AS d, COUNT(*) FROM Messages
                WHERE date(created_at) >= $start
                GROUP BY d ORDER BY d ASC;
                """;
            cmd.Parameters.AddWithValue("$start",
                DateTime.Now.Date.AddDays(-(Math.Max(1, days) - 1)).ToString("yyyy-MM-dd"));
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result.Add((reader.GetString(0), reader.GetInt64(1)));
        }
        catch (Exception ex) { _logger.LogError(ex, "统计消息趋势失败"); }
        return result;
    }

    /// <summary>后台面板统计：记忆按归属分布（scope → 数量）</summary>
    public List<(string Scope, long Count)> MemoryByScope()
    {
        var result = new List<(string, long)>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT scope, COUNT(*) FROM Memories GROUP BY scope ORDER BY COUNT(*) DESC;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result.Add((reader.GetString(0), reader.GetInt64(1)));
        }
        catch (Exception ex) { _logger.LogError(ex, "统计记忆分布失败"); }
        return result;
    }

    /// <summary>某会话的历史消息条数（群聊上下文外置时提示 LLM 用）</summary>
    public long CountSessionMessages(string sessionKey)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Messages WHERE session_key = $s;";
            cmd.Parameters.AddWithValue("$s", sessionKey);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "统计会话消息数失败");
            return 0;
        }
    }

    /// <summary>查用户昵称（用于聊天记录显示）</summary>
    public string? GetUserNickname(long qqId)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT nickname FROM Users WHERE qq_id = $id;";
            cmd.Parameters.AddWithValue("$id", qqId);
            var val = cmd.ExecuteScalar();
            return val as string;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查询用户昵称失败");
            return null;
        }
    }

    private long Scalar(string sql)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "统计查询失败");
            return 0;
        }
    }
}
