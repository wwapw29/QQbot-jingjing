using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace QQBot.Core.Pet;

/// <summary>
/// 桌面精灵的"后台配置"存储：面板里改的那份，桌面端下次心跳时自动取走并应用。
///
/// 文件：`data/pet-config.json`（相对运行目录）
/// <code>
/// { "version": 3, "updatedAt": "2026-09-20 21:00:00", "config": { ...pet.json 同结构... } }
/// </code>
/// version 每保存一次 +1，桌面端靠它判断"要不要重新拉配置"（不必每秒传整份配置）。
/// 桌面端本地 pet.json 仍然有效——远端配置**覆盖**它（位置这种纯本机状态不在这份配置里）。
/// </summary>
public sealed class PetConfigStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly ILogger<PetConfigStore> _logger;

    private JsonObject? _config;      // 面板存的那份（null = 从没存过）
    private int _version;

    public PetConfigStore(ILogger<PetConfigStore> logger)
    {
        _logger = logger;
        _path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data/pet-config.json"));
        Load();
    }

    /// <summary>配置版本号（桌面端据此判断要不要重新拉）</summary>
    public int Version { get { lock (_lock) return _version; } }

    /// <summary>是否已经有后台配置（没有的话桌面端就用自己本地的 pet.json）</summary>
    public bool HasConfig { get { lock (_lock) return _config is not null; } }

    public string FilePath => _path;

    /// <summary>取当前配置（深拷贝，调用方可随意改）</summary>
    public JsonObject? Snapshot()
    {
        lock (_lock) return _config?.DeepClone() as JsonObject;
    }

    /// <summary>保存新配置（version+1）。config 必须是能解析的 JSON 对象</summary>
    public (bool Ok, int Version, string? Error) Save(JsonObject config)
    {
        try
        {
            lock (_lock)
            {
                _config = config.DeepClone() as JsonObject;
                _version++;
                var wrapper = new JsonObject
                {
                    ["version"] = _version,
                    ["updatedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["config"] = _config?.DeepClone(),
                };
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path,
                    wrapper.ToJsonString(new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    }),
                    new UTF8Encoding(false));
                _logger.LogInformation("桌面精灵配置已更新（v{V}，存到 {Path}）", _version, _path);
                return (true, _version, null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存桌面精灵配置失败");
            return (false, _version, ex.Message);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var node = JsonNode.Parse(File.ReadAllText(_path, Encoding.UTF8)) as JsonObject;
            _version = node?["version"]?.GetValue<int>() ?? 0;
            _config = node?["config"] as JsonObject;
            _logger.LogInformation("已载入桌面精灵配置 v{V}（{Path}）", _version, _path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取桌面精灵配置失败（当作没有，用桌面端本地 pet.json）");
        }
    }
}
