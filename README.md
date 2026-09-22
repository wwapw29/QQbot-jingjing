# 静静 QQ 机器人（QQBot）

一个运行在本地 Windows 上的 **AI QQ 机器人**，基于 **NapCat（OneBot 11 协议）** + **C# / .NET 10** 构建。
静静支持私聊/群聊对话、长期记忆、LLM 自主调用工具、本地 ComfyUI 生图、文件沙箱、定时自主活动等能力，
外加一只**桌面宠物**（WPF）：桌上那只静静和她共用同一套人格与记忆——不是第二个 bot。

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
QQBot-share/
├── scripts/
│   ├── start.bat               启动（同步配置 + 运行）
│   ├── stop.bat                停止
│   └── restart.bat             ⭐ 改配置后一键重启
├── 启动静静.bat                ⭐ 启动桌面宠物（没构建过会自动构建）
├── 外部依赖/                   NapCat 程序 / 绿色版 QQ（体积大，未入库）
└── src/
    ├── QQBot/                  机器人本体（C# / .NET 10）
    │   ├── Program.cs          DI 装配入口
    │   ├── appsettings.json    所有配置（人设/模型/记忆/开关…）
    │   ├── wwwroot/            后台管理面板（单文件 admin.html）
    │   └── Core/
    │       ├── Options/        配置模型
    │       ├── OneBot/         OneBot 客户端（WS + HTTP + 去重）
    │       ├── Dispatcher/     事件分发器（触发/命令/Agent 循环）
    │       ├── Chat/           对话引擎 / 上下文 / 回复解析
    │       ├── Memory/         SQLite + 神经链记忆
    │       ├── Commands/       主人命令（!help 等）
    │       ├── Tools/          ITool 工具系统（画图/网页/shell/记忆…）
    │       ├── Vision/         识图（专用识图模型 / 主模型嵌入式两种模式）
    │       ├── Pet/            桌面宠物桥（回复改道 / 主动消息 / 动作提示）
    │       ├── Activity/       主机活动监测（游戏 / 一般 / 看家三模式）
    │       ├── ComfyUI/        ComfyUI 客户端
    │       └── Hosted/         宿主服务 + 自主活动服务
    └── DesktopPet/             桌面宠物（WPF，**只做前端**；人格与记忆复用 QQBot）
        ├── DesktopPet.csproj
        ├── pet.json            外观 / 气泡 / 吸附 / 动作配置（后端地址与令牌也在这）
        ├── assets/             素材（自带的占位立绘；第三方素材不入库）
        ├── UI/                 气泡窗口 / 输入框 / 托盘图标
        └── Pet/                后端客户端 / 配置 / 动作与情绪规则 / 帧加载
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
已连接机器人账号：静静 (你的机器人QQ)
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
| `Admin` | 后台管理面板：开关 / 端口（默认 7088）/ 访问令牌（**部署时务必改成自己的**） |
| `Vision` | 识图：专用模型 / 主模型嵌入式 / 图片压缩与元数据抽取 |
| `Pet` | 桌面宠物接口（`/api/pet/*`）：会话键 / 超时 / 离线判定秒数 |
| `Activity` | 三模式联动（游戏 / 一般 / 看家）的判定阈值；游戏模式还有一套独立管线（间隔 / 提示词 / 工具白名单 / 专属记忆） |
| `BurnToken` | 「烧token模式」：正式回复前先静默收集信息（自己查历史/记忆/文件）再答 |
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

## 🐾 桌面宠物（DesktopPet）

桌面上那只静静：透明置顶的小人 + 气泡说话。**她和 QQ 里的是同一个她**——
`POST /api/pet/chat` 直接复用同一套人设、记忆、工具，桌宠只是她的"另一只手"，不是一个新 bot。

### 启动
1. 先启动机器人（`scripts\restart.bat`）——她的大脑在那边；
2. 双击仓库根目录的 **`启动静静.bat`**（没构建过会自动 `dotnet build`，首次要还原 NuGet 包，稍等）。

她会出现在屏幕右下角；位置、尺寸、锁定状态会被记住。右键她或托盘图标有菜单，双击她弹出输入框。
日志在 `%USERPROFILE%\.jingjing-pet\pet.log`（GUI 程序没控制台，出问题先看它）。

### 换素材（换皮肤）
1. 把立绘丢进 `src\DesktopPet\assets\<你的目录>\`（PNG 序列或 GIF 都行，GIF 会自动拆帧）；
2. 改 `src\DesktopPet\pet.json`：`assetDir` 指到你的目录，各动作的 `frames` 指向帧文件。

**系统动作** `idle` / `talk` / `thinking` / `startup`（登场）/ `error`（报错）是程序按名字调的，别删；
普通动作可以配 `triggers`（触发词，说出来就播）/ `weight`（权重）/ `tools`（绑定的工具名，她调这个工具时播一次）。
更省事的做法：**后台面板 →「桌面精灵」**里可视化编辑皮肤、气泡、动作、工具绑定，存了就热更新。

> 仓库里的 `assets/placeholder` 是脚本画的占位立绘（够跑通整套流程）。
> `assets/placeholder/nienie/` 那份第三方角色素材没入库——请自备素材，别把别人的作品打包进公开仓库。

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
- **面板令牌**：`Admin.Token` 在仓库里是占位值 `change-me`，部署后请改成自己的；留空 = 本机不校验（不建议）。
- **别把凭据提交上去**：`OwnerId` / `ApiKey` / `Admin.Token` 填完自己的就行，别往仓库里推。
