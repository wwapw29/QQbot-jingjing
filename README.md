# 静静 · QQ 机器人（QQBot）

基于 **NapCat（OneBot 11）+ C#/.NET 10** 的 QQ 机器人。内置 AI 女仆人设「静静」，支持自然对话、长期记忆、识图、本地 AI 生图（ComfyUI）、定时自主活动，并带一个完整的后台管理面板。

---

## 功能特性

- **自然对话**：DeepSeek（OpenAI 兼容接口）驱动，支持工具调用（function calling）
- **长期记忆**：SQLite 存储，按归属（全局/用户/群/群内成员）隔离，自动总结沉淀 + 显式 `remember` 工具
- **识图**（可选）：图片压缩后交给视觉模型描述，再注入主对话
- **AI 生图**（可选）：本地 ComfyUI，LLM 扩写提示词后出图发到 QQ
- **自主活动**（可选）：定时主动找主人说话、逛群、整理记忆
- **后台管理面板**：`http://127.0.0.1:7088`，8 个页面
- **Shell 沙箱**：可在工作区内执行命令、写文件

## 技术栈

| 组件 | 说明 |
|---|---|
| NapCat.Shell | OneBot 11 协议端（WS 3001 收事件 / HTTP 3000 调 API） |
| .NET 10 | 宿主 + 对话引擎 + 工具系统（C#） |
| LLM | OpenAI 兼容 API（DeepSeek / 豆包等均可） |
| SQLite | 长期记忆（WAL 模式） |
| ComfyUI | 本地生图（可选） |
| 后台面板 | 零依赖 HttpListener + 单 HTML（admin.html） |

## 目录结构

```
QQBot/
├── QQBot.slnx                # 解决方案
├── scripts/                  # 启停脚本（start / stop / restart）
├── 外部依赖/                 # QQ 安装程序 + NapCat 安装包
└── src/QQBot/
    ├── Program.cs            # 入口 & DI 注册
    ├── appsettings.json      # 全部配置（JSONC，支持 // 注释）
    ├── Core/                 # 核心代码（对话/记忆/工具/命令/面板…）
    ├── ComfyUI/Workflows/    # 生图工作流模板（可自备替换）
    └── wwwroot/              # 后台面板前端（admin.html + d3/three 库）
```

## 快速开始

### 0. 前置环境

- Windows 10 或 11（x64）
- [.NET 10 SDK](https://dotnet.microsoft.com/download)（构建与运行需要）
- 一个闲置 QQ 号（作为机器人本体）

### 1. 安装 QQ 并登录

运行 `外部依赖/QQ_9.9.33_x64.exe`，用机器人 QQ 号登录并保持在线（可关闭弹窗，但 QQ 进程需在后台运行）。

### 2. 安装 NapCat.Shell

- 方式 A（推荐）：解压 `外部依赖/NapCat.Shell.zip` 到任意目录（如 `tools/NapCat.Shell/`），运行目录内的 `start-napcat.bat`
- 方式 B：使用 `外部依赖/NapCat.Shell.Windows.OneKey.zip` 一键包（按包内说明操作）

启动后在 NapCat 的 WebUI（默认 `http://127.0.0.1:6099`）配置 **OneBot 11**：

| 项 | 值 |
|---|---|
| 正向 WebSocket | 端口 `3001`（收事件） |
| HTTP 服务 | 端口 `3000`（调 API） |
| 访问令牌 | 可留空，或设置后同步填写到 `appsettings.json` 的 `Bot.AccessToken` |

### 3. 配置 appsettings.json

打开 `src/QQBot/appsettings.json`（VS Code 请把语言模式切到 **JSON with Comments**，否则注释会标红），至少修改：

| 字段 | 说明 |
|---|---|
| `Bot.OwnerId` | **你的 QQ 号**（主人，最高权限） |
| `Bot.SelfId` | **机器人 QQ 号** |
| `Bot.Llm.BaseUrl` | 模型 API 地址（如 DeepSeek `https://api.deepseek.com/v1`） |
| `Bot.Llm.ApiKey` | 模型 API 密钥 |
| `Bot.Llm.Model` | 模型名 |
| `Bot.Admin.Token` | 后台面板访问令牌（**必须改**，留空 = 仅本机无认证） |

可选：`Bot.Vision.*` 识图、`Bot.ComfyUI.*` 生图、`Bot.AutoActivity.*` 自主活动，默认关闭或按需开启。

### 4. 构建 & 运行

```bash
cd src/QQBot
dotnet build
```

然后**务必使用脚本启停**（脚本会先同步配置再启动，避免配置不生效）：

```
scripts\start.bat      # 启动（首次）
scripts\restart.bat    # 重启（改配置后）
scripts\stop.bat       # 停止
```

### 5. 验证

- 用主人 QQ 私聊机器人发消息，应收到回复
- 打开后台面板 `http://127.0.0.1:7088`，输入 `Admin.Token` 后查看统计/配置/记忆等

## 常用命令（仅主人，前缀 `!`）

| 命令 | 说明 |
|---|---|
| `!help` | 列出所有命令 |
| `!status` | 机器人状态统计 |
| `!memories [global\|all\|QQ号]` | 查看长期记忆 |
| `!history [QQ号\|group 群号] [条数]` | 查看聊天记录 |
| `!clear [QQ号\|group 群号]` | 清空聊天上下文 |
| `!wipe [QQ号\|all]` | 清空长期记忆（慎用） |
| `!remember [global] 内容` | 手动添加记忆 |
| `!mdel <id>` / `!mmove <id>` / `!mimp <id> <1-5>` | 记忆管理 |
| `!draw <提示词>` | 直连 ComfyUI 生图（所有人可用） |
| `!summarize` | 手动总结对话沉淀记忆 |

## 后台管理面板

| 页面 | 功能 |
|---|---|
| 数据统计 | 消息/会话/记忆量 + 近 7 天趋势 + 机器人信息 |
| 功能开关 | 分组可视化编辑全部配置（热更新，无需重启） |
| 基础提示词 | 编辑人设/场景提示词 |
| 记忆库 | 记忆 CRUD + 归属调整 |
| 记忆图谱 | 2D 分层树 / 3D 球状图（点击聚焦、拖动、返回原点） |
| Tools 工具 | 工具启停 + 描述覆盖（热更新） |
| !命令控制台 | 以主人身份执行 `!` 命令，结果回显面板 |
| Debug 日志 | 按天日志查看（级别/关键词过滤、自动刷新） |

## 常见问题

- **配置保存不生效？** 改配置后用 `scripts/restart.bat` 重启；面板里改的配置即时热更新。
- **面板 Token 留空了？** `Admin.Token` 留空时接口无认证（仅限本机），分享/公网使用务必设置。
- **日志在哪？** `data/logs/yyyy-MM-dd.log`，保留 7 天自动清理。
- **生图不可用？** 需要本地运行 ComfyUI，并把 `ComfyUI.WorkflowPath` 指向 API 格式工作流（可参考 `ComfyUI/Workflows/` 下的模板自行替换）。
- **appsettings.json 打开报红？** 文件是 JSONC（带注释），把编辑器语言模式切到 "JSON with Comments"。

## 免责声明

本项目仅供学习交流。使用本机器人请遵守所在地区法律法规与腾讯 QQ 平台规则，请勿用于骚扰、营销等用途。
