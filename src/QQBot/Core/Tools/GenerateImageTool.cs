using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using QQBot.Core.Chat;
using QQBot.Core.ComfyUI;
using QQBot.Core.OneBot;
using QQBot.Core.Options;
using QQBot.Core.Pet;
using QQBot.Core.Vision;

namespace QQBot.Core.Tools;

/// <summary>
/// generate_image —— 调用本地 ComfyUI 生图并把图片发给用户。
/// 流程：扩写提示词（LLM）→ 写入 workflow 正面节点（如 319 的 value）→ 提交 → 轮询 → 下载 → 发图。
/// </summary>
public sealed class GenerateImageTool : ITool
{
    private readonly ChatEngine _engine;
    private readonly ComfyClient _comfy;
    private readonly ComfyUIOptions _options;
    private readonly OneBotClient _client;
    private readonly VisionService _vision;
    private readonly WorkflowRegistry _workflows;
    private readonly PetBridge _pet;
    private readonly ILogger<GenerateImageTool> _logger;

    public GenerateImageTool(ChatEngine engine, ComfyClient comfy, ComfyUIOptions options,
                             OneBotClient client, VisionService vision, WorkflowRegistry workflows,
                             PetBridge pet, ILogger<GenerateImageTool> logger)
    {
        _engine = engine;
        _comfy = comfy;
        _options = options;
        _client = client;
        _vision = vision;
        _workflows = workflows;
        _pet = pet;
        _logger = logger;
        _imageLock = new SemaphoreSlim(1, 1);
    }

    /// <summary>生图串行锁：同一时刻只跑一个生成任务，防止 ComfyUI 显存爆掉</summary>
    private readonly SemaphoreSlim _imageLock;

    public string Name => "generate_image";

    /// <summary>
    /// 工具说明：基础说明 + **当前可用工作流清单（来自后台为每个流写的说明）**，
    /// 让静静能根据画面需求自己挑合适的流。清单随工作流目录/后台设置变化即时更新。
    /// </summary>
    public string Description
    {
        get
        {
            var sb = new System.Text.StringBuilder(
                "使用本地 ComfyUI 生成图片并发送给用户。当用户要求画图、生成图片、绘图、作画时调用。参数 prompt 为用户想要的画面描述。");
            var block = _workflows.BuildToolBlock();
            if (!string.IsNullOrEmpty(block))
            {
                sb.Append("\n【可选工作流】按画面需求挑最合适的一个，用参数 workflow 传文件名或说明里的关键词（不传则用默认流）：\n");
                sb.Append(block);
            }
            sb.Append("\n自主执行：判断出用户想画图就立即调用本工具，无需先询问用户确认；生成完成后如实汇报结果（成功就说画好了，失败就如实说明原因）。");
            return sb.ToString();
        }
    }

    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["prompt"] = new JsonObject { ["type"] = "string", ["description"] = "用户想要的画面描述（自然语言，无需精炼）" },
            ["workflow"] = new JsonObject { ["type"] = "string", ["description"] = "要使用的工作流文件名或说明中的关键词（可选；不填=默认流）" }
        },
        ["required"] = new JsonArray("prompt"),
        ["additionalProperties"] = false
    };

    public async Task<string> ExecuteAsync(string argsJson, ToolContext ctx, CancellationToken ct)
    {
        var args = JsonNode.Parse(argsJson) as JsonObject;
        var intent = args?["prompt"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(intent))
        {
            return "提示词为空，请让用户描述想画的画面";
        }

        // 0. 选工作流：LLM 传了 workflow（文件名/说明关键词）就按它匹配，否则用默认流
        var wanted = args?["workflow"]?.GetValue<string>();
        var wf = _workflows.Resolve(wanted);
        if (wf is null)
        {
            return $"没有可用的工作流（目录：{_workflows.Dir}）——请让主人把 ComfyUI 导出的 API 格式工作流 json 放进这个目录，并在后台写清说明。";
        }
        if (!string.IsNullOrWhiteSpace(wanted) && !wf.File.Contains(wanted.Trim(), StringComparison.OrdinalIgnoreCase) && !wanted.Trim().Equals(wf.Label, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("指定的工作流「{Wanted}」没匹配到，改用默认流 {File}", wanted, wf.File);
        }

        // 1. 先让用户知道在画（立即反馈，不干等）——文案可在 appsettings ComfyUI.SubmitMessage 编辑（{Prompt}/{Workflow}）
        var submit = _options.SubmitMessage.Replace("{Prompt}", Truncate(intent, 20)).Replace("{Workflow}", wf.Label);
        await SendTextAsync(ctx, submit, ct);

        // 2. LLM 扩写提示词（可配置开关；失败退回原话）
        var positive = _options.EnableEnhance ? await EnhancePromptAsync(intent, ct) : null;
        if (string.IsNullOrWhiteSpace(positive)) positive = intent;
        _logger.LogInformation("生图提示词（工作流 {File}）：{Prompt}", wf.File, Truncate(positive, 120));

        // 3. 提交 ComfyUI 并发送（共用底层流程）
        var (ok, message, imageBytes) = await GenerateAndSendAsync(positive, wf, ctx, ct);
        if (!ok)
        {
            // 失败：返回给 LLM 的结果带强制指令，防止"没画出来却说画好了"的幻觉
            return $"生图失败：{message}。" +
                   "【重要】你必须如实告知用户生图失败了，绝不能说“画好了”“图已发送”或假装成功，" +
                   "请向用户说明失败原因并询问是否重试。";
        }

        var result = new System.Text.StringBuilder(message);

        // 3.1 生图后让静静"看到"自己的作品：识图开启时把刚画的图交给识图模型描述一遍
        if (_options.ShowOwnImageToSelf && imageBytes is not null && _vision.Enabled)
        {
            var desc = await _vision.DescribeImageBytesAsync(imageBytes,
                "这是你自己刚刚画出来的图，请描述画面内容（主体、动作、氛围、画风）", ct);
            if (!string.IsNullOrWhiteSpace(desc))
                result.Append($"\n【你自己刚画好的图】{desc}（以上是识图模型对你作品的描述，你已「看到」这张图）");
        }

        // 3.2 完成提示（可在后台「生图(ComfyUI) → 生图完成提示」编辑）：告诉 LLM 别再重复生图
        var hint = _options.ImageDoneHint?.Trim();
        if (!string.IsNullOrWhiteSpace(hint)) result.Append('\n').Append(hint);

        return result.ToString();
    }

    /// <summary>
    /// 直接用给定提示词提交 ComfyUI 并发送图片（跳过 LLM 扩写）。
    /// 供 LLM 工具（扩写后调用）与主人命令（!draw 直连）共用。
    /// 成功时已发送图片；失败时已发送友好提示，返回 ok=false + 错误信息。
    /// </summary>
    public Task<(bool Ok, string Message, byte[]? ImageBytes)> GenerateAndSendAsync(string positive, ToolContext ctx, CancellationToken ct)
        => GenerateAndSendAsync(positive, _workflows.Resolve(null), ctx, ct);

    public async Task<(bool Ok, string Message, byte[]? ImageBytes)> GenerateAndSendAsync(string positive, WorkflowInfo? workflow, ToolContext ctx, CancellationToken ct)
    {
        if (workflow is null)
            return (false, $"没有可用的工作流（目录：{_workflows.Dir}）", null);

        // 生图串行：同一时刻只跑一个任务（防止 ComfyUI 显存爆掉）；排队任务自动等待
        if (_options.SerializeImage)
        {
            await _imageLock.WaitAsync(ct);
            try
            {
                return await GenerateCoreAsync(positive, workflow, ctx, ct);
            }
            finally
            {
                _imageLock.Release();
            }
        }
        return await GenerateCoreAsync(positive, workflow, ctx, ct);
    }

    /// <summary>实际生图流程（已受串行锁保护时调用）</summary>
    private async Task<(bool Ok, string Message, byte[]? ImageBytes)> GenerateCoreAsync(string positive, WorkflowInfo workflow, ToolContext ctx, CancellationToken ct)
    {
        // 1. 加载该工作流的模板并写入正面提示词；节点 ID 优先用这个流自己的，没配才用全局
        var posNode = string.IsNullOrWhiteSpace(workflow.PositiveNodeId) ? _options.PositiveNodeId : workflow.PositiveNodeId;
        var posKey = string.IsNullOrWhiteSpace(workflow.PositiveValueKey) ? _options.PositiveValueKey : workflow.PositiveValueKey;
        WorkflowTemplate template;
        try
        {
            template = new WorkflowTemplate(workflow.FullPath, posNode, posKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "workflow 模板加载失败：{File}", workflow.File);
            await SendTextAsync(ctx, "呜，绘图模板没找到……主人检查下工作流文件？", ct);
            return (false, $"workflow 加载失败：{ex.Message}", null);
        }

        JsonObject wfJson;
        try
        {
            wfJson = template.Build(positive);
        }
        catch (Exception ex)
        {
            return (false, $"workflow 模板错误：{ex.Message}", null);
        }

        // 2. 提交 ComfyUI
        var promptId = await _comfy.SubmitAsync(wfJson, ct);
        if (promptId is null)
        {
            await SendTextAsync(ctx, "呜，本地绘图服务没连上……主人检查下 ComfyUI 是不是开着？", ct);
            return (false, "ComfyUI 提交失败", null);
        }

        // 3. 等待生成
        var images = await _comfy.WaitForResultAsync(promptId, ct, workflow.SaveImageNodeId);
        if (images is null || images.Count == 0)
        {
            await SendTextAsync(ctx, "呜，画图失败/超时了……要不要再试一次？", ct);
            return (false, "ComfyUI 生成失败", null);
        }

        // 4. 下载第一张图并发给用户（带说明：引用触发消息 + @发起者(群聊) + 提示词标识，并发出图不混淆）
        var img = images[0];
        var bytes = await _comfy.DownloadImageAsync(img, ct);
        if (bytes is null)
        {
            return (false, "图片下载失败", null);
        }

        var b64 = Convert.ToBase64String(bytes);
        // 图片标题文案可在 appsettings ComfyUI.CaptionMessage 编辑（{Prompt}=正面提示词）
        var sent = await SendImageAsync(ctx, b64, _options.CaptionMessage.Replace("{Prompt}", Truncate(positive, 30)), ct);
        if (!sent)
        {
            // 图生成了但没发出去——不算成功，防止"图已发送"幻觉
            await SendTextAsync(ctx, "呜，图画好了但发送失败了……稍后重试一下？", ct);
            return (false, "图片发送失败（QQ 拒绝或网络问题）", null);
        }
        return (true, $"已生成并发送图片（{img.Filename}，工作流：{workflow.Label}）", bytes);
    }

    /// <summary>调用 LLM 扩写提示词（用配置的 EnhanceInstruction，支持占位符替换）</summary>
    private async Task<string?> EnhancePromptAsync(string intent, CancellationToken ct)
    {
        try
        {
            var instruction = _options.EnhanceInstruction
                .Replace("{QualityTags}", _options.QualityTags)
                .Replace("{Prompt}", intent)
                .Replace("{Negative}", _options.DefaultNegative);

            // 简单任务：按配置关闭 LLM 思维链（省 token）
            JsonObject? extraBody = null;
            if (_options.EnhanceDisableReasoning && !string.IsNullOrWhiteSpace(_options.DisableReasoningPayload))
            {
                try { extraBody = JsonNode.Parse(_options.DisableReasoningPayload) as JsonObject; }
                catch { /* payload 格式错误则忽略 */ }
            }

            var result = await _engine.CompleteAsync(
            [
                new ChatMessage("system", instruction),
                new ChatMessage("user", intent)
            ], ct, extraBody);
            return result.Content?.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "提示词扩写失败");
            return null;
        }
    }

    /// <summary>发送文字消息（供命令 !draw 复用）；桌面宠物会话则收进 sink，由桌面端播气泡</summary>
    public Task SendTextAsync(ToolContext ctx, string text, CancellationToken ct)
    {
        if (_pet.TryGet(ctx.Message.SessionKey, out var sink))
        {
            sink.Add(PetReply.Text0(text));
            return Task.CompletedTask;
        }
        return ctx.Message.IsPrivate
            ? _client.SendPrivateMessageAsync(ctx.Message.UserId, [Segments.Text(text)], ct)
            : _client.SendGroupMessageAsync(ctx.Message.GroupId, [Segments.Text(text)], ct);
    }

    /// <summary>
    /// 发送图片（带说明段）：引用触发消息 + @发起者（群聊）+ 提示词说明 + 图片。
    /// 返回是否发送成功（供上层判定生图是否真正送达，防止"图已发送"幻觉）。
    /// </summary>
    private async Task<bool> SendImageAsync(ToolContext ctx, string base64, string? caption, CancellationToken ct)
    {
        // 桌面宠物会话：图片不回 QQ，交给桌面端显示（返回 true —— 对上层而言"图已送达"成立）
        if (_pet.TryGet(ctx.Message.SessionKey, out var sink))
        {
            sink.Add(PetReply.Image0($"data:image/png;base64,{base64}", caption));
            return true;
        }

        var segments = new List<JsonNode>();
        if (ctx.Message.MessageId > 0)
        {
            segments.Add(Segments.Reply((int)ctx.Message.MessageId));
        }
        if (!ctx.Message.IsPrivate)
        {
            segments.Add(Segments.At(ctx.Message.UserId));
        }
        if (!string.IsNullOrWhiteSpace(caption))
        {
            segments.Add(Segments.Text(caption + " "));
        }
        segments.Add(Segments.Image($"base64://{base64}"));

        return ctx.Message.IsPrivate
            ? await _client.SendPrivateMessageAsync(ctx.Message.UserId, segments, ct)
            : await _client.SendGroupMessageAsync(ctx.Message.GroupId, segments, ct);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
