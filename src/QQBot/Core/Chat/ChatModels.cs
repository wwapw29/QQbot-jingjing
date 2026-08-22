using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace QQBot.Core.Chat;

/// <summary>
/// OpenAI 兼容的聊天消息（发给 LLM 的 messages 元素）。
/// ToolCalls：assistant 发起工具调用时回传；ToolCallId：tool 角色结果消息关联。
/// ImageDataUrls：识图模式（Vision.Enabled）下挂载的图片（base64 data URL），序列化时 content 变为多模态数组。
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(ChatMessageConverter))]
public sealed class ChatMessage
{
    public ChatMessage(string role, string? content)
    {
        Role = role;
        Content = content;
    }

    [JsonPropertyName("role")] public string Role { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }

    /// <summary>识图模式的图片（base64 data URL，如 data:image/jpeg;base64,...）；序列化为 content 数组的 image_url</summary>
    [JsonIgnore]
    public List<string>? ImageDataUrls { get; set; }

    /// <summary>DeepSeek Files API 的 file_id 列表（如 file-api-xxx）；序列化为 content 数组的 file 块（与 ImageDataUrls 互斥，优先）</summary>
    [JsonIgnore]
    public List<string>? FileIds { get; set; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<JsonObject>? ToolCalls { get; set; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }

    /// <summary>assistant 的思维链内容（DeepSeek 要求：携带 tools 的请求必须完整回传 reasoning_content，否则 400）</summary>
    [JsonPropertyName("reasoning_content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningContent { get; set; }

    /// <summary>消息发送者 QQ（仅本地记录用，不随请求发给 LLM）</summary>
    [JsonIgnore]
    public long? UserId { get; set; }
}

/// <summary>
/// ChatMessage 序列化器：带图时 content 输出多模态数组 [text, image_url...]，否则输出普通字符串。
/// </summary>
public sealed class ChatMessageConverter : JsonConverter<ChatMessage>
{
    // ChatMessage 只用于请求序列化（响应用独立模型 ChatCompletionResponse），无需反序列化
    public override ChatMessage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException("ChatMessage 仅用于序列化（请求构造），不参与反序列化");

    public override void Write(Utf8JsonWriter writer, ChatMessage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("role", value.Role);

        if (value.ImageDataUrls is { Count: > 0 } || value.FileIds is { Count: > 0 })
        {
            // 多模态 content 数组：文本 + 图片（Files API file 块优先，其次 base64 image_url 块）
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            if (!string.IsNullOrWhiteSpace(value.Content))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", value.Content);
                writer.WriteEndObject();
            }
            if (value.FileIds is { Count: > 0 })
            {
                foreach (var fid in value.FileIds)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "file");
                    writer.WriteString("file_id", fid);
                    writer.WriteEndObject();
                }
            }
            foreach (var url in value.ImageDataUrls ?? [])
            {
                writer.WriteStartObject();
                writer.WriteString("type", "image_url");
                writer.WritePropertyName("image_url");
                writer.WriteStartObject();
                writer.WriteString("url", url);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        else if (value.Content is not null)
        {
            writer.WritePropertyName("content");
            writer.WriteStringValue(value.Content);
        }
        // content 为 null（如 assistant 工具调用轮）时不输出 content 键，与原 JsonIgnoreCondition.WhenWritingNull 行为一致

        if (value.ToolCalls is not null)
        {
            writer.WritePropertyName("tool_calls");
            JsonSerializer.Serialize(writer, value.ToolCalls, options);
        }
        if (value.ToolCallId is not null)
        {
            writer.WritePropertyName("tool_call_id");
            writer.WriteStringValue(value.ToolCallId);
        }
        if (value.ReasoningContent is not null)
        {
            writer.WritePropertyName("reasoning_content");
            writer.WriteStringValue(value.ReasoningContent);
        }
        writer.WriteEndObject();
    }
}

/// <summary>一次完整对话的返回结果（content 与 reasoning_content 分离）</summary>
public sealed record ChatResult(
    string? Content,          // 最终回复内容
    string? ReasoningContent  // 思考过程（DeepSeek-R1 等才有；其余为 null）
)
{
    public bool HasContent => !string.IsNullOrWhiteSpace(Content);
}

/// <summary>LLM 发起的工具调用</summary>
public sealed record ToolCall(string Id, string Name, string Arguments);

/// <summary>带工具能力的对话结果</summary>
public sealed record ChatToolResult(
    string? Content,
    string? ReasoningContent,
    IReadOnlyList<ToolCall> ToolCalls)
{
    public bool HasToolCalls => ToolCalls.Count > 0;
    public bool HasContent => !string.IsNullOrWhiteSpace(Content);
}

/// <summary>chat/completions 请求体（标准 OpenAI 格式，支持 tools）</summary>
public sealed class ChatCompletionRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; set; } = [];
    [JsonPropertyName("temperature")] public double Temperature { get; set; } = 0.7;
    [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; } = 1024;
    [JsonPropertyName("stream")] public bool Stream { get; set; } = false;

    /// <summary>工具定义（function calling）</summary>
    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? ToolChoice { get; set; }
}

/// <summary>chat/completions 响应体（只取需要的字段）</summary>
public sealed class ChatCompletionResponse
{
    [JsonPropertyName("choices")] public List<Choice> Choices { get; set; } = [];

    public sealed class Choice
    {
        [JsonPropertyName("message")] public ResponseMessage? Message { get; set; }
    }

    public sealed class ResponseMessage
    {
        [JsonPropertyName("role")] public string? Role { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }

        /// <summary>DeepSeek-R1 的思维链字段（标准 OpenAI 响应中可能不存在）</summary>
        [JsonPropertyName("reasoning_content")] public string? ReasoningContent { get; set; }

        /// <summary>工具调用（function calling）</summary>
        [JsonPropertyName("tool_calls")] public List<ResponseToolCall>? ToolCalls { get; set; }
    }

    public sealed class ResponseToolCall
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("function")] public ResponseFunction? Function { get; set; }
    }

    public sealed class ResponseFunction
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("arguments")] public string? Arguments { get; set; }
    }
}
