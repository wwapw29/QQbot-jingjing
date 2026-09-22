# NapCat 安装教程（Windows · 已实测走通）

> 目标：让机器人 QQ 小号通过 NapCat 上线，开放 OneBot 11 接口（HTTP :3000 + 正向 WebSocket :3001）供 C# 主程序对接。
> **本机部署状态（2026-08-05 已完成）：小号「静静」1234567890 已登录，3000/3001/6099 全部在线。**

---

## 0. 本机环境速查

| 项目 | 路径 / 值 |
|------|----------|
| NapCat.Shell 版 | `D:\你的目录\QQrobot\tools\NapCat.Shell\` |
| 绿色版 QQ 9.9.33 | `D:\你的目录\QQrobot\tools\qq-green\Files\QQ.exe` |
| NapCat 启动脚本 | `D:\你的目录\QQrobot\tools\NapCat.Shell\start-napcat.bat` |
| OneBot 11 配置 | `D:\你的目录\QQrobot\tools\NapCat.Shell\config\onebot11.json` |
| WebUI | `http://127.0.0.1:6099/webui`（Token：见 `config/webui.json`） |
| 机器人账号 | 静静 1234567890 |

> ⚠️ **本机部署要点**：
> - NapCat 用 `NapCatWinBootMain.exe` 拉起 QQ 是**无头模式（无窗口）**，登录**必须走 WebUI 扫码**，不要等 QQ 窗口弹出来。
> - 已登录过账号后，QQ 登录态保存在绿色版数据目录，重启 NapCat 会尝试自动登录（若未自动登录，WebUI 扫码一次即可）。
> - 一台电脑上同时开多个 QQ 客户端没问题，但**同一个号不能同时登两处**。

---

## 第 1 步：下载

**官方下载地址**（GitHub Releases）：

```
https://github.com/NapNeko/NapCatQQ/releases/latest
```

Windows 需要两个包：
- `NapCat.Shell.zip`（NapCat 本体，约 30MB）——**推荐**，配已装的 QQ 使用
- ~~`NapCat.Shell.Windows.OneKey.zip`~~：一键包自带的 QQ 下载链接已失效（404），不推荐

> 国内网络访问 GitHub 慢的话，可用镜像前缀替换域名：
> `https://nclatest.znin.net/NapNeko/NapCatQQ/releases/latest`

---

## 第 2 步：准备绿色版 QQ（无需安装）

> NapCat 需要 NTQQ 本体。OneKey 安装器下载 QQ 的链接已失效，改为**直接解压官方 QQ 安装包**成绿色版：

1. 从腾讯官方下载 QQ Windows 安装包：`https://im.qq.com/qq/download/`（本机用的是 9.9.33）
2. 用 7z 解压安装包到目录（如 `tools\qq-green`）：
   ```
   7z x QQ_9.9.33_x64.exe -o"qq-green" -y
   ```
3. 绿色版 QQ 主程序在：`qq-green\Files\QQ.exe`

---

## 第 3 步：配置 OneBot 11 接口

NapCat 的 OneBot 11 配置是**单个文件**：`config\onebot11.json`（注意：不是分协议的 onebot11_ws.json！文件名是 `onebot11_<QQ号>.json`，不存在时回退 `onebot11.json`）。

```json
{
  "network": {
    "httpServers": [
      { "name": "http-api", "enable": true, "host": "127.0.0.1", "port": 3000,
        "messagePostFormat": "array", "reportSelfMessage": false, "token": "",
        "postUrls": [], "timeout": 5, "debug": false, "enableWebsocket": false }
    ],
    "httpClients": [],
    "httpSseServers": [],
    "websocketServers": [
      { "name": "bot-main-ws", "enable": true, "host": "127.0.0.1", "port": 3001,
        "messagePostFormat": "array", "reportSelfMessage": false, "token": "",
        "enableForcePushEvent": true, "debug": false, "heartInterval": 30000,
        "enableWebsocket": true }
    ],
    "websocketClients": []
  }
}
```

---

## 第 4 步：启动与登录（关键）

1. 双击 `start-napcat.bat`（内容等价于官方 launcher.bat，但 QQ 路径指向绿色版）：
   - 设置环境变量 `NAPCAT_*`
   - 运行 `NapCatWinBootMain.exe "<绿色版QQ.exe路径>" "<NapCatWinBootHook.dll路径>"`
2. QQ 以**无头模式**后台运行（无窗口，属正常）。
3. 浏览器打开 WebUI：`http://127.0.0.1:6099/webui`，输入 `config\webui.json` 里的 Token。
4. WebUI → 「登录」页 → 获取二维码 → 手机 QQ（机器人小号）扫码。
5. 登录成功后回到命令行验证：
   ```
   curl http://127.0.0.1:3000/get_login_info
   ```
   返回 `{"user_id":...,"nickname":"..."}` 即成功。

---

## 第 5 步：验证就绪

| 端口 | 用途 | 验证 |
|------|------|------|
| 3000 | OneBot HTTP API | `curl http://127.0.0.1:3000/get_login_info` |
| 3001 | OneBot 正向 WebSocket | C# 主程序连接 `ws://127.0.0.1:3001` |
| 6099 | NapCat WebUI | 浏览器访问 |

---

## 注意事项

- **始终用 QQ 小号**，别用主号；小号先正常登录用几天再挂机器人，降低风控概率。
- 启动顺序：**先开 NapCat，再启动机器人主程序**。
- 端口冲突：3000/3001/6099 被占用时改 `onebot11.json` / `webui.json` 里的端口，并同步到 `appsettings.json`。
- 重启电脑后：双击 `start-napcat.bat` → 若无自动登录则 WebUI 扫码一次。

---

## 常见问题

| 问题 | 处理 |
|------|------|
| 一键包安装器报「下载QQ失败 404」 | 安装器内嵌的 QQ 下载链接失效，改用 Shell 版 + 绿色版 QQ（见第 2 步） |
| NapCat 起来了但 3000/3001 没监听 | 确认已通过 WebUI 扫码登录；确认 `config\onebot11.json` 文件名和格式正确后重启 |
| QQ 窗口没弹出来 | 正常！NapCat 是无头模式，登录走 WebUI |
| WebUI 打不开 | 确认 NapCat 进程在；端口 6099 |
| 扫码后登录失败 | 小号密码/验证码/风控；换网络环境或过几小时再试 |
| 端口被占用 | 任务管理器找占用进程，或改端口 |

