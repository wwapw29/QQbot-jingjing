using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using QQBot.Core.Options;

namespace QQBot.Core.Tools;

/// <summary>
/// run_shell —— 在机器人自己的沙箱工作区里执行简单命令（丐版终端）。
/// 安全边界：
///  - 命令固定以沙箱目录为工作目录（WorkingDirectory）
///  - 拦截危险命令（格式化/关机/杀进程/系统级删除/网络用户管理等）
///  - 超时自动终止（含子进程树）、输出截断
/// 说明：这是"防呆不防黑"的轻量沙箱，供主人自用；不要在里面跑不受信任的命令。
/// </summary>
public sealed class ShellTool : ITool
{
    private readonly ShellOptions _options;
    private readonly ILogger<ShellTool> _logger;

    /// <summary>危险命令片段黑名单（小写匹配；命中直接拒绝执行）</summary>
    private static readonly string[] Dangerous =
    [
        "format ", "diskpart", "shutdown", "taskkill", "rmdir /s", "rd /s", "rm -rf", "rm -r",
        "del /s", "del /f /s", "net user", "net localgroup", "reg delete", "reg add", "reg import",
        "sc delete", "powershell -enc", "powershell -e ", "certutil", "wmic process call",
        "attrib -r -s", "mkfs", "dd if=", "fdisk", "move /y c:\\", "copy /y c:\\"
    ];

    public ShellTool(ShellOptions options, ILogger<ShellTool> logger)
    {
        _options = options;
        _logger = logger;
    }

    public string Name => "run_shell";
    public string Description =>
        "在机器人的工作文件夹（沙箱）里执行命令。当用户要求静静创建/修改/整理文件、写代码或脚本、运行程序、查看文件夹内容等文件类操作时调用。" +
        "命令将在工作区目录下运行，可用的命令如 dir、type、echo、copy、mkdir、ren、del、python、node 等。" +
        "注意：工作文件夹里的所有文件都是你自己（静静）之前写的，属于你自己的私人领地，可以自由查看、修改、整理、删除。" +
        "写文件时请使用 UTF-8 编码（echo 已默认 UTF-8；用 python 写文件时带上 encoding='utf-8'），不要写 GBK/ANSI 编码。" +
        "自主执行：判断出需要文件/命令操作时立即调用，无需先询问用户确认；执行完成后如实汇报输出和结果（成功/失败都如实说）。";

    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["command"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "要在工作区中执行的命令（Windows cmd 语法）"
            }
        },
        ["required"] = new JsonArray("command"),
        ["additionalProperties"] = false
    };

    public async Task<string> ExecuteAsync(string argsJson, ToolContext ctx, CancellationToken ct)
    {
        var args = JsonNode.Parse(argsJson) as JsonObject;
        var command = args?["command"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(command)) return "命令为空";

        // 危险命令拦截
        var lower = command.ToLowerInvariant();
        foreach (var d in Dangerous)
        {
            if (lower.Contains(d))
            {
                _logger.LogWarning("拦截危险命令：{Command}", command);
                return $"命令被安全策略拦截（含敏感操作：{d.Trim()}）。请换一个安全范围内的操作。";
            }
        }

        // 沙箱目录
        var sandbox = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _options.SandboxPath));
        try { Directory.CreateDirectory(sandbox); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建沙箱目录失败：{Dir}", sandbox);
            return $"沙箱目录创建失败：{ex.Message}";
        }

        // 所有权说明文件：让静静（和 LLM）通过 dir 也能直观确认"这是自己的领地"
        try
        {
            var readme = Path.Combine(sandbox, "README-我的小空间.txt");
            if (!File.Exists(readme))
            {
                File.WriteAllText(readme,
                    "这里是静静（机器人）自己的小空间！\n" +
                    "本文件夹下的所有文件都是静静自己写的（日记、脚本、笔记、草稿等），属于静静的私人领地。\n" +
                    "静静可以自由查看、修改、整理、删除这些文件，不用客气，主人不会动这里。\n",
                    System.Text.Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入小空间说明文件失败（不影响使用）");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            // chcp 65001：让 cmd 以 UTF-8 代码页运行——echo/type 写读文件都按 UTF-8，避免中文乱码（默认 GBK）
            Arguments = $"/c chcp 65001 >nul & {command}",
            WorkingDirectory = sandbox,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        _logger.LogInformation("[Shell] {Sandbox} $ {Command}", sandbox, command);
        try
        {
            using var proc = new Process { StartInfo = psi };
            if (!proc.Start())
            {
                return "命令启动失败";
            }

            // 并行读输出，避免管道满死锁
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            // 超时控制
            var finished = await Task.WhenAny(
                proc.WaitForExitAsync(ct),
                Task.Delay(TimeSpan.FromSeconds(_options.TimeoutSeconds), ct));
            bool timedOut = finished != proc.WaitForExitAsync(ct) && !proc.HasExited;
            if (timedOut)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* 已退出则忽略 */ }
                _logger.LogWarning("[Shell] 命令超时已终止：{Command}", command);
                return $"命令执行超时（{_options.TimeoutSeconds} 秒），已终止。";
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var output = (stdout + (string.IsNullOrWhiteSpace(stderr) ? "" : "\n[stderr]\n" + stderr)).Trim();

            if (output.Length > _options.MaxOutputChars)
            {
                output = output[.._options.MaxOutputChars] + "\n…(输出过长已截断)";
            }
            _logger.LogInformation("[Shell] 退出码={Code}，输出 {Len} 字符", proc.ExitCode, output.Length);
            return output.Length == 0
                ? $"命令已执行（退出码 {proc.ExitCode}），无输出。"
                : $"命令输出（退出码 {proc.ExitCode}）：\n{output}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "命令执行被中断";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Shell] 命令执行异常：{Command}", command);
            return $"命令执行失败：{ex.Message}";
        }
    }
}
