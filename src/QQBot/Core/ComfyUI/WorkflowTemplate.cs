using System.Text.Json.Nodes;

namespace QQBot.Core.ComfyUI;

/// <summary>
/// Workflow 模板：加载 ComfyUI 导出的 API 格式 JSON，
/// 把正面提示词写入指定节点（如 319 多行字符串的 value 字段），其余节点不动。
/// </summary>
public sealed class WorkflowTemplate
{
    private readonly JsonObject _template;
    private readonly string _positiveNodeId;
    private readonly string _positiveValueKey;

    /// <summary>从文件加载模板</summary>
    public WorkflowTemplate(string path, string positiveNodeId, string positiveValueKey)
    {
        var text = File.ReadAllText(path);
        _template = JsonNode.Parse(text) as JsonObject
            ?? throw new InvalidDataException("workflow 不是有效的 JSON 对象");
        _positiveNodeId = positiveNodeId;
        _positiveValueKey = positiveValueKey;
    }

    /// <summary>深拷贝模板并写入正面提示词，返回可提交的 workflow</summary>
    public JsonObject Build(string positivePrompt)
    {
        var clone = (JsonObject)_template.DeepClone();

        if (!clone.TryGetPropertyValue(_positiveNodeId, out var nodeObj) || nodeObj is not JsonObject node)
        {
            throw new InvalidOperationException($"workflow 中不存在节点 {_positiveNodeId}");
        }
        var inputs = node["inputs"] as JsonObject
            ?? throw new InvalidOperationException($"节点 {_positiveNodeId} 缺少 inputs");

        inputs[_positiveValueKey] = positivePrompt;
        return clone;
    }
}
