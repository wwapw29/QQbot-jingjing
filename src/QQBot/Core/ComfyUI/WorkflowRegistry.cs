using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using QQBot.Core.Options;

namespace QQBot.Core.ComfyUI;

/// <summary>一个可选工作流的元信息（文件 + 后台写的说明 + 该流自己的注入/保存节点 ID）</summary>
public sealed record WorkflowInfo(
    string File,              // 文件名（含 .json）
    string FullPath,          // 绝对路径
    string Desc,              // 后台写的说明（会告诉静静，她据此选用）
    string PositiveNodeId,    // 注入提示词节点 ID（空=用全局 ComfyUI.PositiveNodeId）
    string PositiveValueKey,  // 注入字段名（空=用全局）
    string SaveImageNodeId,   // 保存图片节点 ID（空=用全局；用于从 history 里取图）
    bool IsDefault,
    int NodeCount,
    long Size,
    DateTime ModifiedAt)
{
    /// <summary>给 LLM/用户看的短名（去扩展名）</summary>
    public string Label => Path.GetFileNameWithoutExtension(File);
}

/// <summary>
/// 工作流注册表：扫描 WorkflowDir（静静个人空间里的 workflows 目录）下所有 *.json，
/// 说明/节点 ID/默认流来自目录内的 workflows.json（后台「生图」页可编辑）。
/// 静静在 generate_image 里可按说明自选工作流。
/// </summary>
public sealed class WorkflowRegistry
{
    private readonly ComfyUIOptions _options;
    private readonly ILogger<WorkflowRegistry> _logger;

    public WorkflowRegistry(ComfyUIOptions options, ILogger<WorkflowRegistry> logger)
    {
        _options = options;
        _logger = logger;
    }

    private const string MetaFile = "workflows.json";

    /// <summary>工作流目录（绝对路径）</summary>
    public string Dir
    {
        get
        {
            var dir = _options.WorkflowDir;
            if (string.IsNullOrWhiteSpace(dir) && !string.IsNullOrWhiteSpace(_options.WorkflowPath))
                dir = Path.GetDirectoryName(_options.WorkflowPath.Replace('\\', '/')) ?? "";
            if (string.IsNullOrWhiteSpace(dir)) dir = "data/workspace/workflows";
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, dir));
        }
    }

    /// <summary>元数据文件（说明/节点 ID/默认流）绝对路径</summary>
    public string MetaPath => Path.Combine(Dir, MetaFile);

    /// <summary>扫描目录 + 合并元数据，返回全部工作流（默认流排最前）</summary>
    public List<WorkflowInfo> Load()
    {
        var list = new List<WorkflowInfo>();
        try
        {
            if (!Directory.Exists(Dir))
            {
                _logger.LogWarning("工作流目录不存在：{Dir}", Dir);
                return list;
            }

            var meta = ReadMeta();
            var defaultFile = meta["default"]?.GetValue<string>() ?? "";
            var flows = meta["flows"] as JsonObject;

            foreach (var path in Directory.GetFiles(Dir, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var file = Path.GetFileName(path);
                if (file.Equals(MetaFile, StringComparison.OrdinalIgnoreCase)) continue;
                if (file.StartsWith('_')) continue;   // _ 开头视为草稿/备份

                var fi = new FileInfo(path);
                var nodeCount = 0;
                try
                {
                    nodeCount = (JsonNode.Parse(File.ReadAllText(path)) as JsonObject)?.Count ?? 0;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "工作流解析失败（{File}），节点数计 0", file);
                }

                var f = flows?[file] as JsonObject;
                list.Add(new WorkflowInfo(
                    file,
                    path,
                    f?["desc"]?.GetValue<string>() ?? "",
                    f?["positiveNodeId"]?.GetValue<string>() ?? "",
                    f?["positiveValueKey"]?.GetValue<string>() ?? "",
                    f?["saveImageNodeId"]?.GetValue<string>() ?? "",
                    false,
                    nodeCount,
                    fi.Length,
                    fi.LastWriteTime));
            }

            // 默认流：元数据指定 > 唯一的文件 > 排在最前的
            var defIdx = list.FindIndex(w => w.File.Equals(defaultFile, StringComparison.OrdinalIgnoreCase));
            if (defIdx < 0) defIdx = 0;
            if (list.Count > 0) list[defIdx] = list[defIdx] with { IsDefault = true };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "扫描工作流目录失败：{Dir}", Dir);
        }
        return list;
    }

    /// <summary>
    /// 按名字解析工作流：支持 文件名 / 去扩展名的名字 / 说明关键词（LLM 可能传"女仆装流"之类）。
    /// name 为空或找不到时返回默认流；目录为空返回 null。
    /// </summary>
    public WorkflowInfo? Resolve(string? name)
    {
        var all = Load();
        if (all.Count == 0) return null;
        var def = all.FirstOrDefault(w => w.IsDefault) ?? all[0];
        if (string.IsNullOrWhiteSpace(name)) return def;

        var n = name.Trim();
        var hit = all.FirstOrDefault(w => w.File.Equals(n, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(w => w.Label.Equals(n, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(w => w.Label.Contains(n, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(w => w.Desc.Contains(n, StringComparison.OrdinalIgnoreCase));
        return hit ?? def;
    }

    /// <summary>拼给 LLM 的可用工作流清单（附在绘图工具说明里）</summary>
    public string BuildToolBlock()
    {
        var all = Load();
        if (all.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var w in all)
        {
            sb.Append("- ").Append(w.File);
            if (!string.IsNullOrWhiteSpace(w.Desc)) sb.Append("：").Append(w.Desc);
            if (w.IsDefault) sb.Append("（默认）");
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>写回元数据（默认流 + 每个流的说明/节点 ID）；只保留目录里真实存在的文件</summary>
    public void SaveMeta(string defaultFile, IEnumerable<(string File, string Desc, string PositiveNodeId, string PositiveValueKey, string SaveImageNodeId)> flows)
    {
        var existing = Load().Select(w => w.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var flowsObj = new JsonObject();
        foreach (var f in flows)
        {
            if (string.IsNullOrWhiteSpace(f.File) || !existing.Contains(f.File)) continue;
            flowsObj[f.File] = new JsonObject
            {
                ["desc"] = f.Desc ?? "",
                ["positiveNodeId"] = f.PositiveNodeId ?? "",
                ["positiveValueKey"] = f.PositiveValueKey ?? "",
                ["saveImageNodeId"] = f.SaveImageNodeId ?? "",
            };
        }
        var root = new JsonObject
        {
            ["default"] = existing.Contains(defaultFile) ? defaultFile : (flowsObj.FirstOrDefault().Key ?? ""),
            ["flows"] = flowsObj,
        };
        Directory.CreateDirectory(Dir);
        File.WriteAllText(MetaPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
            new UTF8Encoding(false));
        _logger.LogInformation("工作流元数据已保存：{Path}（{N} 个流，默认 {Def}）", MetaPath, flowsObj.Count, root["default"]);
    }

    /// <summary>读元数据（不存在/损坏返回空对象）</summary>
    public JsonObject ReadMeta()
    {
        try
        {
            if (!File.Exists(MetaPath)) return new JsonObject();
            return JsonNode.Parse(File.ReadAllText(MetaPath)) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "工作流元数据解析失败：{Path}", MetaPath);
            return new JsonObject();
        }
    }
}
