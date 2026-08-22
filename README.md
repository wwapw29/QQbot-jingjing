# 静静 QQ 机器人（QQBot）

一个运行在本地 Windows 上的 **AI QQ 机器人**，基于 **NapCat（OneBot 11 协议）** + **C# / .NET 10** 构建。
静静支持私聊/群聊对话、长期记忆、LLM 自主调用工具、本地 ComfyUI 生图、文件沙箱、定时自主活动等能力。

> ⚠️ 请始终使用**养过一段时间的 QQ 小号**运行机器人，切勿使用主号（防风控）。

---

## ✨ 功能特性

| 能力 | 说明 |
|------|------|
| **私聊 / 群聊** | 私聊直接触发；群聊仅 @ 或回复静静时触发 |
| **AI 对话** | OpenAI 兼容接口（默认 DeepSeek），主/客双人设，私聊/群聊场景区分 |
| **多轮回复** | 静静可自发地多次请求 LLM，像真人一样"说一句补一句"（最多 4 轮） |
| **回复格式校验** | LLM 输出不符格式时自动带纠正提示重试（最多 2 次），生图/执行类工具成功后放宽 |
| **长期记忆** | SQLite 持久化 + 神经链记忆：**通用/用户/群三层**，两步定位提取（QQ/群号精确检索 + 语境筛选），function call 结构化总结，去重合并 + 用进废退衰减 |
| **函数调用** | LLM 自主调用工具：查时间、记记忆、查记忆、发消息、**浏览网页**、**执行命令**、**ComfyUI 生图** |
| **生图** | 接入本地 ComfyUI（你排好的 workflow），LLM 扩写提示词 → 出图 → 发 QQ，失败如实汇报 |
| **文件沙箱** | 静静可在自己的 `data/workspace` 小空间里自由创建/修改文件、跑脚本（危险命令拦截） |
| **主人命令** | 仅主人可用的 `!` 命令：管理记忆、查记录、直连生图等 |
| **自主活动** | 完全没人理静静超过设定时长后，她会"无聊"地主动私聊你、看群聊插嘴、整理小空间 |
| **多线程安全** | 全局并发门 + 会话级串行锁 + 长任务让出锁，多人同时聊不乱不阻塞 |
| **消息去重** | NapCat 重复推送同一条消息时自动去重，不会双倍回复 |

---

## 🏗 技术架构

```
QQ 小号
  │ 登录
NapCat（独立进程，OneBot 11 协议）
  ├─ 正向 WebSocket :3001  ──→  收消息事件
  └─ HTTP API :3000      ←──  发消息
              │
      ┌───────┴────────┐
      │  QQBot (C#/net10) │
      │  事件分发器 → 主人命令 / Agent 循环（LLM+工具）│
      │  记忆系统（SQLite）│
      └───────┬────────┘
        DeepSeek API（OpenAI 兼容）   ComfyUI :8188（生图）
```

| 组件 | 角色 |
|------|------|
| **NapCat** | QQ 协议端，独立进程，机器人登录与协议收发都靠它 |
| **QQBot 主程序** | C# / .NET 10 控制台应用，全部业务逻辑 |
| **DeepSeek** | 对话/记忆总结/提示词扩写的 LLM（标准 OpenAI 格式，可换任意兼容服务） |
| **ComfyUI** | 本地生图服务（你自备 workflow，静静只写正面提示词节点） |
| **SQLite** | 聊天记录、用户档案、长期记忆（单文件 `data/bot.db`） |

---

## 📁 目录结构

```
QQrobot/
├── docs/                       设计文档 + NapCat 安装教程
├── tools/
│   ├── NapCat.Shell/           NapCat 程序（含启动脚本）
│   └── qq-green/               绿色版 QQ（9.9.33，NapCat 注入运行）
├── QQBot/
│   ├── QQBot.sln
│   ├── scripts/
│   │   ├── start.bat           启动（同步配置 + 运行）
│   │   ├── stop.bat            停止
│   │   └── restart.bat         ⭐ 改配置后一键重启
│   └── src/QQBot/
│       ├── Program.cs          DI 装配入口
│       ├── appsettings.json    所有配置（人设/模型/记忆/开关…）
│       └── Core/
│           ├── Options/        配置模型
│           ├── OneBot/         OneBot 客户端（WS + HTTP + 去重）
│           ├── Dispatcher/     事件分发器（触发/命令/Agent 循环）
│           ├── Chat/           对话引擎 / 上下文 / 回复解析
│           ├── Memory/         SQLite + 神经链记忆
│           ├── Commands/       主人命令（!help 等）
│           ├── Tools/          ITool 工具系统（画图/网页/shell/记忆…）
│           ├── ComfyUI/        ComfyUI 客户端
│           ├── Hosted/         宿主服务 + 自主活动服务
│           └── ActivityClock.cs 自主活动空闲检测时钟
```

---

## 🚀 快速启动

### 0. 环境要求
- Windows + .NET 10 SDK
- QQ 小号（已登录到绿色版 QQ）
- DeepSeek API Key（或任意 OpenAI 兼容服务）
- ComfyUI（可选，生图用）

### 1. 启动 NapCat（协议端）
```bat
cd tools\NapCat.Shell
start-napcat.bat        # 会拉起绿色版 QQ，扫码登录小号
```
确保 3000（HTTP）/ 3001（WS）/ 6099（WebUI）端口在监听。详见 `docs/NapCat安装教程.md`。

### 2. 启动机器人
双击 `QQBot\scripts\restart.bat`，看到日志：
```
已连接机器人账号：静静 (2049592241)
WebSocket 已连接 ✓
```
即上线成功。

### 3. 改配置后重启
改完 `appsettings.json` → 双击 `restart.bat`（会自动把配置同步到运行目录）。

---

## ⚙️ 配置说明（appsettings.json → Bot 节点）

| 节点 | 说明 |
|------|------|
| `OwnerId` | 主人 QQ（最高权限，命令可用） |
| `Llm` | 模型配置：BaseUrl / ApiKey / Model / 超时重试 / 关闭思维链 |
| `Prompt` | 提示词：全局前置/后置（role 可自定义）+ 主人/客人双人设 + **4 个场景独立 Profile**（见下） |
| `Memory` | 记忆：DB 路径 / 保留天数 / 唤起参数 / 拒绝型对话跳过总结关键词 |
| `Reply` | 回复：多轮上限 / 间隔 / 格式重试次数 |
| `Command` | 命令前缀（默认 `!`） |
| `ComfyUI` | 生图：workflow 路径 / 正提示词节点 ID / 扩写开关 |
| `Shell` | 文件沙箱：目录 / 超时 / 输出限制 |
| `AutoActivity` | 自主活动：空闲时长 / 各行动独立开关 |
| `Concurrency` | 并发：同时处理的对话数上限 |
| `Debug` | 调试开关（输出 LLM 完整请求/响应） |

> `Llm.DisableReasoningPayload`：`{"thinking":{"type":"disabled"}}` 完全关闭思维链；`{"reasoning_effort":"low"}` 低强度思考。

### 🎭 提示词场景管理（Prompt 节点）

提示词按 **身份 × 场景** 解析，最终 system = 全局前置 + 解析后的身份提示词 + 场景补充 + 记忆注入 + 格式指令 + 全局后置：

| 场景 Profile | 覆盖时机 | 未配置时回退到 |
|------|------|------|
| `OwnerPrivate` | 与主人私聊 | `Owner` 身份 + `PrivateExtra` |
| `GuestPrivate` | 与客人私聊 | `Guest` 身份 + `PrivateExtra` |
| `OwnerGroup` | 群聊中回复主人 | `Owner` 身份 + `GroupExtra` |
| `GuestGroup` | 群聊中回复他人 | `Guest` 身份 + `GroupExtra` |

每个 Profile 可单独设置 `SystemPrompt` / `PrePrompt` / `PostPrompt` / `Extra`(场景专属补充)：
- **字段留空 → 自动回退**到身份默认(`Owner`/`Guest` 的对应字段)和场景补充(`PrivateExtra`/`GroupExtra`)
- 全部留空 = 与旧版行为完全一致，随时可只覆盖想改的场景
- 占位符 `{UserName}` / `{UserQQ}` / `{OwnerId}` 在所有提示词字段中可用

---

## 🎮 主人命令（仅主人 QQ 可用，前缀 `!`）

```
!help       命令清单          !status    机器人状态统计
!memories   查看记忆          !history   查看聊天记录（支持 group 群号）
!clear      清空聊天记录      !wipe      清空记忆
!remember   添加记忆          !mdel      删除单条记忆
!mmove      移动记忆归属      !mimp      修改重要度
!summarize  手动总结最近对话沉淀记忆
!draw       直连生图（跳过 LLM 扩写）
```

---

## 🔧 二次开发：新增一个工具

1. 新建类实现 `ITool`（`Core/Tools/` 下）：
   ```csharp
   public sealed class MyTool : ITool
   {
       public string Name => "my_tool";
       public string Description => "干什么用的、什么时候调用";
       public JsonObject ParametersSchema => new() { /* OpenAI JSON Schema */ };
       public async Task<string> ExecuteAsync(string argsJson, ToolContext ctx, CancellationToken ct) { /* 干活 */ }
   }
   ```
2. 在 `BuiltinTools.CreateAll()` 注册一行即可——LLM 就会自动学会使用它。

---

## ⚠️ 注意事项

- **别关 NapCat 的黑窗口**——关了就掉线。
- **频繁掉线 = 风控前兆**，停用半天再试，别硬刚。
- **命令/工具自主性**：shell 沙箱是"防呆不防黑"级别，别让陌生人使唤静静跑命令。
- 数据库单文件在 `bin\Debug\net10.0\data\bot.db`，备份直接拷走。
