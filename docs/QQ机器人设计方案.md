# QQ 机器人完整设计方案（C# 版）

> 版本：v1.0 ｜ 日期：2026-08-05 ｜ 状态：待实施
> 一句话概括：**NapCat（协议层） + C#/.NET 10 主程序 + OpenAI 兼容 LLM + 本地 ComfyUI + SQLite 记忆库**

---

## 0. 需求对照总览

| # | 你的需求 | 实现方式 |
|---|---------|---------|
| 1 | 可加入群聊或加好友私聊 | NapCat 登录 QQ 号，OneBot 11 标准接口，天然支持群聊 + 私聊 |
| 2 | 读取自定义长度的聊天记录 | 会话上下文管理器：可配置按条数 / Token 预算 / 时间窗截取历史 |
| 3 | 私聊或被 @ 时回复，允许多条回复 | 事件分发器（触发规则过滤）+ 回复队列（多条回复按序发送） |
| 4 | 拥有调用函数功能 | Function Calling（OpenAI 兼容 tools 协议），工具注册表可插拔 |
| 5 | 本地运行，接入本地 ComfyUI 生图并发送图片 | ComfyUI REST API（/prompt → /history → /view），图片转 base64 发送 |
| 6 | AI 自行维护聊天关系和记忆数据库 | SQLite：用户档案、关系图谱（亲密度/标签）、长期记忆摘要、消息历史 |
| 7 | 自定义系统提示词，可自由截取 AI 回复内容 | 提示词模板放配置文件；cot 截取支持三种策略（字段分离/分隔符/正则） |
| 8 | C# 语言，可随时运行/关闭/重启 | 控制台宿主 + 一键启动/停止/重启脚本（.bat），可选 Windows 服务化 |
| 9 | 功能设计清晰，增减容易 | 分层架构 + 依赖注入 + 插件化模块注册，新增功能=新增一个模块类 |

---

## 1. 技术选型与理由

| 层次 | 选型 | 理由 |
|------|------|------|
| 协议端 | **NapCat**（备用：Lagrange.OneBot / LLOneBot） | 2026 年个人 QQ 机器人最主流方案；独立进程登录 QQ，无需主程序关心 QQ 风控细节；Windows 原生支持；OneBot 11 标准接口 |
| 开发语言 | **C# / .NET 10**（LTS） | 你指定的语言；与你的 Dungen29 技术栈统一；异步性能好；宿主程序自带 |
| 协议对接 | **自研轻量 OneBotClient 层**（正向 WebSocket + HTTP API） | OneBot 11 规范简单，自研约 300~400 行即可完全可控、无第三方依赖生命周期的坑；若想省事可换 NuGet 的 `HoshikawaKaguya.Sora`（备用方案） |
| LLM | **OpenAI 兼容 API**：DeepSeek / 通义千问 / Kimi / 智谱 / 本地 Ollama | 一套代码通吃所有供应商，切换只需改配置；DeepSeek 自带 `reasoning_content` 字段天然支持 cot 分离 |
| 数据库 | **SQLite**（`Microsoft.Data.Sqlite`） | 单文件、零部署、易备份、适合本地机器人；并发量对聊天机器人完全够用 |
| ComfyUI | **REST API 直连**（`System.Net.Http`） | ComfyUI 原生提供 /prompt、/history、/view 接口，无额外依赖 |
| 配置 | **appsettings.json + 分层 Options + 热重载** | 微软原生配置系统，改配置不用重启 |
| 日志 | **Serilog**（控制台 + 滚动文件） | 便于排障与复盘 |
| 宿主 | 控制台应用（`dotnet run`）+ 启停脚本 | 随时运行/关闭/重启；进阶可注册为 Windows 服务或 NSSM 托管 |

> **为什么不用 QQ 官方机器人 API？** 官方开放平台机器人需要企业/个人认证、接口能力受限（私聊、主动发图、消息频率都有严格限制），且不能"加好友"自由私聊。个人自用机器人的场景下，OneBot 方案（NapCat）功能最全、最自由。
>
> **为什么协议端要独立进程？** 协议端（登录 QQ、收发原始消息）与业务端（AI 对话、生图）解耦：QQ 被风控或协议升级时只影响协议端；业务升级、重启 AI 逻辑不影响 QQ 在线状态。这正是"可随时运行或关闭或重启"的关键设计。

---

## 2. 总体架构

```
┌─────────────────────────────────────────────────────────────┐
│  QQ 客户端（群聊 / 私聊）                                      │
└──────────────────────────┬──────────────────────────────────┘
                           │ OneBot 11（HTTP API + 正向 WebSocket）
┌──────────────────────────▼──────────────────────────────────┐
│  NapCat（独立进程）                                            │
│  登录 QQ · 消息收发 · 图片上传 · 事件上报                        │
└──────────────────────────┬──────────────────────────────────┘
                           │ 事件（JSON）+ API 调用
┌──────────────────────────▼──────────────────────────────────┐
│  C# 主程序（.NET 10 控制台宿主）                                │
│  ┌──────────────┐   ┌──────────────┐                        │
│  │ OneBot 客户端 │──▶│  事件分发器   │ 触发过滤·白名单·防刷      │
│  └──────────────┘   └──────┬───────┘                        │
│                            ▼                                 │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌──────────┐    │
│  │ 对话引擎   │  │ 函数调用   │  │ComfyUI   │  │ 记忆系统   │    │
│  │(LLM)     │  │(Tools)   │  │ 生图      │  │(SQLite)  │    │
│  └────┬─────┘  └────┬─────┘  └────┬─────┘  └────┬─────┘    │
│       ▼              │             │             │          │
│  ┌──────────┐        │             │             │          │
│  │ 回复队列   │◀───────┴─────────────┴─────────────┘          │
│  │(多条回复)  │                                                │
│  └──────────┘                                                │
│  配置系统（appsettings.json · 热重载）· 日志（Serilog）          │
└─────────────────────────────────────────────────────────────┘
           │                        │              │
           ▼                        ▼              ▼
   LLM API（OpenAI 兼容）    ComfyUI（本地 8188）   SQLite 记忆库
```

**消息流转主链路**：QQ 收到消息 → NapCat 上报事件 → OneBot 客户端 → 事件分发器（判断是否触发）→ 对话引擎（组装上下文 + 调 LLM）→ 回复队列 → OneBot 客户端调用发送 API → NapCat → QQ。

**回复发送链路**：回复队列把多条回复（文本/图片/引用）按序交给 OneBot 客户端，由它调用 `send_private_msg` / `send_group_msg` 发出。

---

## 3. 模块详细设计

### 3.1 OneBot 客户端（协议适配层）

职责：与 NapCat 建立正向 WebSocket 连接（事件订阅），并封装 HTTP API（发消息、取历史、取群列表等）。

```
OneBotClient
├── ConnectAsync()           // WS 连接 + 心跳 + 断线重连（指数退避）
├── EventReceived            // 事件流：message.private / message.group / notice 等
├── SendPrivateMessageAsync(userId, segments)
├── SendGroupMessageAsync(groupId, segments)
├── GetGroupHistoryAsync(groupId, count)   // 主动拉群历史
├── GetFriendHistoryAsync(userId, count)   // 主动拉私聊历史
└── BuildImageSegment(file 或 base64)      // 图片消息段（用于发图）
```

要点：
- 消息段（segment）格式：`{ "type": "text" | "image" | "reply" | "at", "data": {...} }`，支持引用回复与 @。
- 断线自动重连：WS 断开后指数退避重连（1s→2s→4s…→30s 封顶）。
- 所有发送动作收敛在这一个类里，协议细节不外泄，未来换协议端零改动。

### 3.2 事件分发器（触发规则）

职责：决定"这条消息要不要让 AI 回复"，以及"回复给谁、要不要引用原消息"。

| 场景 | 触发规则 |
|------|---------|
| 私聊 | 默认全部触发（可配白名单/黑名单：`AllowedUsers` / `BlockedUsers`） |
| 群聊 | 必须 @ 机器人 或 回复机器人 才触发（`GroupAtOnly: true`） |
| 冷却 | 同会话 3 秒内消息合并（防抖），防止连发刷屏 |
| 限流 | 每分钟回复条数上限，超限静默 |

输出一个统一的 `IncomingMessage` 模型：
```csharp
record IncomingMessage(
    long MessageId,
    long SenderId, string SenderName,
    long GroupId,          // 群聊才有
    bool IsPrivate,
    string PlainText,      // 纯文本（含@本体时去掉@段）
    List<MessageSegment> Segments,  // 原始段，含图片/表情等
    string SessionKey      // private:{uid} 或 group:{gid}
);
```

### 3.3 会话与上下文管理器（自定义聊天记录长度）

职责：维护每个会话的上下文，决定喂给 LLM 多少历史。

三种可组合的截取策略（`appsettings.json` 配置）：

```
"Context": {
  "MaxMessages": 30,        // 最近 N 条消息（默认）
  "MaxTokens": 4000,        // 或按 Token 预算截取（优先）
  "MaxMinutes": 60,         // 或只保留最近 X 分钟内的消息
  "IncludeSystemMemory": true  // 是否注入长期记忆
}
```

- 会话以 `SessionKey`（`private:{qq}` / `group:{群号}`）为键，存于内存 `ConcurrentDictionary` + SQLite 落盘。
- 群聊支持两种模式：`user`（每人独立上下文，互不干扰）/ `shared`（整个群共享上下文，群聊像共用一个大脑）。默认 `user`。
- 历史消息从 SQLite `Messages` 表按会话加载（自定义长度 = 取最近 N 条）。

### 3.4 对话引擎（LLM 接入 + 回复截取）

职责：组装提示词 → 调用 LLM → 截取回复 → 交给回复队列。

**提示词组装顺序**（最终发给 LLM 的 messages）：
1. `System` 提示词（来自配置 `Prompt:SystemPrompt`，可含记忆注入占位符）
2. 长期记忆注入（用户档案 + 关系标签 + 最近重要记忆，见 3.7）
3. 历史消息（按 3.3 策略截取）
4. 当前消息 + 函数定义（tools）

**回复截取（cot 处理）**——支持三种策略，满足"自由截取 AI 回复内容"：

```json
"ReplyExtraction": {
  "Strategy": "reasoningContent",   // 可选：reasoningContent | delimiter | regex
  "Delimiter": "```END_REASONING```",
  "Regex": "(?s)^.*?(?:最终回答|Final):\\s*(.*)$"
}
```

| 策略 | 原理 | 适用 |
|------|------|------|
| `reasoningContent`（推荐） | 直接读响应 JSON 里的 `content` 字段，`reasoning_content` 自动丢弃 | DeepSeek-R1 / 通义千问-QwQ 等带思维链字段的模型 |
| `delimiter` | 模型按约定在思考后输出分隔符，截取分隔符之后的部分 | 任意模型，靠提示词约束 |
| `regex` | 用正则匹配最终回答段 | 有固定输出格式的自定义场景 |

> 即使策略是 `reasoningContent`，仍然保留 `delimiter` 作为兜底：如果模型把思考写进了 content（某些模型不分离字段），先按分隔符截一次。

### 3.5 函数调用系统（Function Calling / Tools）

职责：让 LLM 能调用工具（查记忆、生图、发图片、查时间……），工具可插拔。

**实现方式**：OpenAI 兼容 `tools` 协议。
1. 对话引擎把工具注册表序列化为 `tools` 参数发给 LLM；
2. LLM 若决定调用，返回 `tool_calls`；
3. 主程序执行对应工具，把结果作为 `tool` 角色消息回填；
4. LLM 基于工具结果生成最终回复（可多轮工具调用循环，上限默认 5 轮）。

**工具注册表**（`ITool` 接口，新增工具 = 新增一个类）：

```csharp
public interface ITool
{
    string Name { get; }
    string Description { get; }
    object ParametersSchema { get; }          // JSON Schema
    Task<string> ExecuteAsync(string argsJson, ToolContext ctx);
}
```

内置工具（随项目提供）：
| 工具名 | 功能 |
|--------|------|
| `generate_image` | 调用 ComfyUI 生图，返回图片路径/占位，经回复队列发送图片 |
| `search_memory` | 检索记忆库（某人 / 某话题的相关记忆） |
| `remember` | 主动写入一条长期记忆 |
| `get_time` | 当前时间 |
| `send_text` | 触发第二条回复（实现"多条回复"的显式手段之一） |

> "多条回复"的两种实现：① 对话引擎一次回复里用 `\n---\n` 或专用指令拆成多条；② 工具 `send_text` 在对话中主动追加一条。回复队列统一按序发送。

### 3.6 ComfyUI 生图模块

职责：把用户的生图请求提交到本地 ComfyUI，等待完成，取图，转 base64 发给回复队列。

**调用链路**（ComfyUI 标准 API）：
```
POST http://127.0.0.1:8188/prompt          → 提交 workflow（返回 prompt_id）
GET  http://127.0.0.1:8188/history/{id}    → 轮询（500ms 间隔）直到输出就绪
GET  http://127.0.0.1:8188/view?filename=..&type=output  → 下载图片
→ 转 base64 → OneBot 图片消息段 → 发送
```

要点：
- **Workflow 模板**：主人在 ComfyUI 界面排好版后，用"导出 API 格式"导出 workflow JSON，存为 `ComfyUI/Workflows/standard.json`。生图模块在模板中自动替换 `CLIPTextEncode` 的正/负提示词文本节点（按节点 ID 配置），并随机 seed。
- 超时控制：默认 300s，超时回复"生图超时"。
- 支持参数：模型（ckpt）、宽高、步数、正/负提示词，均可由用户语句解析或走默认值。
- 同一时刻只跑一个生图任务（队列串行），防止爆显存。

### 3.7 记忆系统（AI 自行维护的关系与记忆）

核心设计：**三层记忆 + 关系图谱**，全部存 SQLite。

```
第一层：消息历史（Messages 表）          —— 近期对话的原文，供上下文截取
第二层：用户档案 + 关系图谱（Users/Relations 表）
        · 亲密度（affinity）：随互动频率/时长自动增减
        · 标签（tags）：AI 根据对话总结出的用户特征
        · 关系称呼（nickname）：用户要求或 AI 建议
第三层：长期记忆（Memories 表）          —— AI 自动沉淀的"重要的事"
        · 触发：对话结束后，用 LLM 总结本次对话是否有值得长期记住的信息
        · 内容：用户偏好、重要承诺、生活事件等
        · 过期/覆盖：按 importance 与时间衰减，超容量清理
```

**记忆维护流程**（AI 自行维护）：
1. 每轮对话结束后，后台任务把"用户消息 + AI 回复"交给 LLM，用固定提示词判断：
   - 有无值得记住的新信息 → 写入 `Memories`
   - 亲密度变化 → 更新 `Relations`
   - 话题结束 → 生成对话摘要（可选，防止长会话上下文爆掉）
2. 每次组装提示词时，按"当前用户 + 当前话题"召回相关记忆注入 System。

> 这个设计就是让 AI "记得主人是谁、我们聊过什么、主人喜欢什么"，且完全由 AI 自己维护，不用手动管理。

### 3.8 回复队列（多条回复）

职责：保证"一次触发可以发多条回复"，且顺序正确、不会刷屏。

```csharp
ReplyQueue.Enqueue(sessionKey, ReplyItem { Segments, Priority })
// 每会话一个发送协程：按序发送，条间间隔默认 800ms，
// 总量上限默认 5 条/次触发（可配），超长文本自动分片或合并转发
```

- 支持文本分片（QQ 单条上限约 2000 字，超长自动切段或转合并转发）。
- 生图任务通过队列发图：文字"正在生成…"先发，图片完成后追加发送。
- 发送失败自动重试 2 次。

### 3.9 配置系统

- 主配置 `appsettings.json`，按模块分层（见第 6 节完整示例）。
- 敏感信息（API Key、Token）单独放 `secrets.json`（不提交 git）。
- 使用 `Microsoft.Extensions.Options` + `ChangeToken` 实现热重载：改配置无需重启。

---

## 4. 数据库设计（SQLite）

```sql
-- 用户档案与关系图谱
CREATE TABLE Users (
    qq_id        INTEGER PRIMARY KEY,     -- 用户 QQ
    nickname     TEXT,                    -- 关系称呼
    tags         TEXT DEFAULT '[]',       -- JSON 数组：特征标签
    affinity     INTEGER DEFAULT 0,       -- 亲密度 -100~100
    profile      TEXT,                    -- AI 维护的用户画像摘要
    first_seen   TEXT, last_seen  TEXT
);

-- 会话（每个会话一个上下文槽）
CREATE TABLE Sessions (
    session_key  TEXT PRIMARY KEY,        -- private:{qq} / group:{gid}
    kind         TEXT,                    -- private | group
    context_json TEXT,                    -- 最近的上下文消息序列（截取后）
    updated_at   TEXT
);

-- 消息历史（全量落盘，供"自定义长度"截取与复盘）
CREATE TABLE Messages (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    session_key  TEXT,                    -- 索引 (session_key, created_at)
    role         TEXT,                    -- user | assistant | tool
    content      TEXT,
    msg_id       INTEGER,                 -- QQ 消息 ID（对应召回用）
    created_at   TEXT
);

-- 长期记忆（AI 自动维护）
CREATE TABLE Memories (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    qq_id        INTEGER,                 -- 归属用户（群聊可为 0 = 群级）
    content      TEXT,                    -- 记忆内容
    importance   INTEGER DEFAULT 1,       -- 1~5
    category     TEXT,                    -- 偏好/事件/承诺/习惯...
    source_msg   INTEGER,                 -- 来源消息 id
    created_at   TEXT, updated_at TEXT
);

-- 生图任务记录
CREATE TABLE ImageJobs (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    session_key  TEXT, qq_id INTEGER,
    prompt       TEXT, workflow  TEXT,
    status       TEXT,                    -- queued | running | done | failed
    prompt_id    TEXT,                    -- ComfyUI prompt_id
    image_path   TEXT,
    created_at   TEXT
);

-- 键值设置（机器人自身的可变状态）
CREATE TABLE Settings (
    key TEXT PRIMARY KEY, value TEXT
);
```

> 并发模型：`Microsoft.Data.Sqlite` 默认单写连接，配合 WAL 模式（`PRAGMA journal_mode=WAL`）即可满足机器人场景；数据量增长后按 `Messages.created_at` 定期清理（保留最近 N 天，可配）。

---

## 5. 关键流程设计

### 5.1 消息处理主流程

```
QQ 消息 → NapCat 上报 → OneBot 客户端
  → 事件分发器：是私聊？是群聊且被@/被回复？→ 通过
  → 上下文管理器：加载/截取历史（自定义长度）
  → 记忆系统：注入用户画像 + 相关记忆
  → 对话引擎：调 LLM（带 tools + 系统提示词）
      └─ LLM 返回 tool_calls？→ 执行工具 → 回填 → 再调 LLM（循环，≤5 轮）
  → 回复截取（reasoning_content / delimiter / regex）
  → 回复队列：拆条 → 顺序发送 → OneBot API → NapCat → QQ
  → 记忆系统：后台总结写入（新记忆/亲密度/画像）
```

### 5.2 生图流程

```
用户："画一只戴帽子的猫"
→ 对话引擎判定需要工具 → 函数调用 execute("generate_image", {prompt:"戴帽子的猫"})
→ 回复队列先发"好的，正在生成~"
→ ComfyUI 模块：替换模板正提示词 → POST /prompt → 轮询 /history → 下载图片
→ 转 base64 → 回复队列追加图片消息 → 发出
→ 可选：LLM 收到工具结果后再补一句文案（"画好了，请查收~"）
```

### 5.3 cot 截取流程

```
LLM 原始响应：
  { "content": "最终回复……", "reasoning_content": "一大堆思考……" }
→ Strategy=reasoningContent：直接取 content，丢掉 reasoning_content
→ 兜底：若 content 中仍含 "```END_REASONING```" → 截取其后的部分
→ 输出干净的最终回复 → 回复队列
```

---

## 6. 配置示例（appsettings.json）

```jsonc
{
  "OneBot": {
    "WsUrl": "ws://127.0.0.1:3001",   // NapCat 正向 WS 地址
    "AccessToken": "",                 // 鉴权令牌（可选）
    "HttpBase": "http://127.0.0.1:3000" // HTTP API 地址（发消息用）
  },
  "Trigger": {
    "PrivateEnabled": true,            // 私聊是否触发
    "GroupAtOnly": true,               // 群聊仅 @ 触发
    "AllowedUsers": [],                // 白名单（空 = 全部）
    "BlockedUsers": [],                // 黑名单
    "DebounceMs": 3000,                // 同会话消息合并窗口
    "RateLimitPerMinute": 20
  },
  "Context": {
    "MaxMessages": 30,                 // 历史条数上限
    "MaxTokens": 4000,                 // Token 预算（优先级高于条数）
    "GroupMode": "user"                // user | shared
  },
  "LLM": {
    "BaseUrl": "https://api.deepseek.com/v1",
    "ApiKey": "",                      // 放 secrets.json
    "Model": "deepseek-chat",
    "ReasoningModel": "deepseek-reasoner",
    "Temperature": 0.7,
    "MaxTokens": 1024,
    "MaxToolRounds": 5
  },
  "Prompt": {
    "SystemPrompt": "你是「小仆」……（自定）",
    "MemoryInjection": true,
    "ReplyExtraction": {
      "Strategy": "reasoningContent",  // reasoningContent | delimiter | regex
      "Delimiter": "```END_REASONING```",
      "Regex": "(?s)^.*?(?:最终回答):\\s*(.*)$"
    }
  },
  "ComfyUI": {
    "BaseUrl": "http://127.0.0.1:8188",
    "WorkflowPath": "ComfyUI/Workflows/standard.json",
    "PositiveNodeId": "6",             // 正提示词节点 ID（按你的 workflow 改）
    "NegativeNodeId": "7",
    "DefaultCkpt": "sd_xl_base_1.0.safetensors",
    "Width": 832, "Height": 1216, "Steps": 28,
    "TimeoutSeconds": 300
  },
  "Memory": {
    "DbPath": "data/bot.db",
    "EnableAutoSummary": true,         // 对话后自动总结记忆
    "MaxMemoriesPerUser": 200,
    "HistoryRetentionDays": 30         // 消息历史保留天数
  },
  "Reply": {
    "MaxRepliesPerTurn": 5,            // 单次触发最多回复条数
    "IntervalMs": 800,                 // 条间间隔
    "MaxTextLength": 1900,             // 单条上限，超长分片
    "LongReplyMode": "split"           // split 分片 | forward 合并转发
  },
  "Serilog": { "MinimumLevel": "Information" }
}
```

---

## 7. 项目目录结构（拟）

```
QQBot/
├── QQBot.sln
├── src/
│   └── QQBot/
│       ├── Program.cs                 // 入口：DI 装配 + 启动
│       ├── appsettings.json           // 主配置
│       ├── secrets.json               // API Key 等（gitignore）
│       ├── Core/
│       │   ├── OneBot/
│       │   │   ├── OneBotClient.cs    // WS + HTTP 封装
│       │   │   ├── EventDispatcher.cs // 触发过滤
│       │   │   └── Segments.cs        // 消息段模型
│       │   ├── Conversation/
│       │   │   ├── ContextManager.cs  // 上下文截取
│       │   │   └── ChatEngine.cs      // LLM 调用 + 回复截取
│       │   ├── Tools/
│       │   │   ├── ITool.cs
│       │   │   └── Builtin/           // 内置工具（生图/记忆/时间…）
│       │   ├── ComfyUI/
│       │   │   ├── ComfyClient.cs     // /prompt /history /view
│       │   │   └── WorkflowTemplate.cs
│       │   ├── Memory/
│       │   │   ├── Db.cs              // SQLite 访问
│       │   │   ├── MemoryService.cs   // 记忆读写/总结
│       │   │   └── RelationService.cs // 亲密度/标签
│       │   ├── Reply/
│       │   │   └── ReplyQueue.cs
│       │   └── Config/Options.cs
│       └── data/                      // bot.db（运行时生成，gitignore）
├── scripts/
│   ├── start.bat / stop.bat / restart.bat
│   └── install-service.ps1            // （可选）注册 Windows 服务
└── docs/QQ机器人设计方案.md            // 本文档
```

---

## 8. 运行 / 关闭 / 重启方案

| 操作 | 方式 |
|------|------|
| 启动 | `scripts/start.bat`（先拉起 NapCat，再 `dotnet run` 主程序） |
| 关闭 | `stop.bat`：优雅关闭（`Ctrl+C` 触发，等待回复队列排空，保存上下文） |
| 重启 | `restart.bat` = stop + start |
| 开机自启（可选） | `install-service.ps1` 用 NSSM 把主程序注册为 Windows 服务，NapCat 也支持开机自启 |

优雅关闭要点：`CancellationToken` + 发送协程排空 + 上下文落盘，保证重启后对话连续性（AI 记得重启前聊到哪）。

---

## 9. 你需要准备的东西（工作清单）

> 以下全是**环境准备**工作，一次性完成，之后就能安心写代码。

### 9.1 账号（最重要，先办）
- [ ] 一个**专用的 QQ 小号**（不要用主号！协议端登录有风控风险）
- [ ] 小号通过手机端 QQ 完成**实名认证**，登录稳定几天后再挂机器人（降低风控）
- [ ] 建议：把机器人加进你想测试的群，或用小号加你主号为好友

### 9.2 协议端 NapCat（Windows）
- [ ] 下载 NapCat 最新 Windows 版（GitHub：NapNeko/NapCatQQ）
- [ ] 用 QQ 小号扫码登录 NapCat
- [ ] 在 NapCat 控制台/配置里开启：**正向 WebSocket**（默认 `ws://127.0.0.1:3001`）+ **HTTP**（默认 `http://127.0.0.1:3000`）
- [ ] 确认"猫猫日志"无连接错误

### 9.3 LLM API（任选一家即可，推荐 DeepSeek）
- [ ] 注册 DeepSeek 开放平台，充值少量余额，创建 API Key（https://platform.deepseek.com）
- [ ] 或者选：通义千问（阿里云百炼）/ Kimi（Moonshot）/ 智谱 GLM / 本地 Ollama（免费但要显卡）
- [ ] 拿到 BaseUrl + ApiKey + Model 名称，填进 `secrets.json`

### 9.4 ComfyUI（本地生图）
- [ ] 已装 ComfyUI 并能正常出图（需要 N 卡，显存 ≥ 6G 起步）
- [ ] 在 ComfyUI 界面排一个你喜欢的出图工作流 → 菜单"导出 API 格式" → 存为 `ComfyUI/Workflows/standard.json`
- [ ] 记住正/负提示词节点的 ID（填进配置 `ComfyUI.PositiveNodeId/NegativeNodeId`）

### 9.5 开发环境
- [ ] 安装 .NET 10 SDK（https://dotnet.microsoft.com）
- [ ] 可选：VS Code + C# 插件，或 Visual Studio / Rider

---

## 10. 实施路线图（分 5 个阶段，每阶段可独立验证）

| 阶段 | 内容 | 验证标准 | 依赖 |
|------|------|---------|------|
| **P0 环境** | 装 .NET SDK、NapCat 登录、API Key 就绪、ComfyUI 出图 | 机器人在线、能手动发消息 | 9.1~9.5 全部 |
| **P1 骨架** | 项目初始化 + OneBot 客户端 + 事件分发器 + 回显回复 | 私聊发"ping"回"pong"，群 @ 才回 | P0 |
| **P2 对话** | 对话引擎 + 上下文截取 + 系统提示词 + cot 截取 | 机器人能正常对话，回复不含思考过程 | P1 |
| **P3 记忆** | SQLite + 记忆系统 + 用户档案/亲密度 + 自动总结 | 重启后仍记得用户；"你记得我吗"能答上 | P2 |
| **P4 函数** | 工具注册表 + 内置工具 | 语音命令能触发工具，多轮工具调用正常 | P3 |
| **P5 生图** | ComfyUI 集成 + 图片发送 + 回复队列完善 | "画个XX"→ 收到图片回复；多条回复顺序正常 | P4 |
| **P6 打磨** | 防刷/冷却、守护脚本、日志、热重载、文档 | 长期稳定运行 | P5 |

> 建议一次只推一个阶段，每阶段跑通再进下一个，避免一次堆太多难定位问题。

---

## 11. 风险与注意事项

1. **账号风控（最大风险）**：QQ 对协议端登录有检测。务必：用刚注册的小号养几天、不要高频群发、不要短时间内大量加群/加好友、机器人回复频率保持正常人类节奏。被风控时 NapCat 会掉线，重登即可。
2. **合规**：机器人行为需符合 QQ 平台规则；建议机器人名片、自动回复声明"AI 机器人"。
3. **隐私**：记忆库含用户聊天数据，SQLite 文件请放本地并定期备份（可加启动时自动备份）。API Key 不要提交到 git。
4. **本地算力**：ComfyUI 生图吃显卡，生图时机器会卡；建议生图队列串行 + 控制分辨率。
5. **成本**：LLM API 按 token 计费，记忆总结等后台任务会增加消耗，可在配置里开关 `EnableAutoSummary`。
6. **OneBot 生态**：NapCat/Lagrange 均为社区项目，若 QQ 协议大改可能出现短暂不可用，关注其更新即可（这也是协议端独立进程设计的好处——换协议端不影响业务代码）。

---

## 12. 下一步

1. 确认技术选型（第 1 节）与默认配置（第 6 节）；
2. 按第 9 节清单准备环境；
3. 从 **P1 骨架** 开始搭建：我可以直接帮你生成完整的项目骨架代码（sln + Program.cs + OneBot 客户端 + 事件分发器 + 回显），跑通后逐阶段推进。
