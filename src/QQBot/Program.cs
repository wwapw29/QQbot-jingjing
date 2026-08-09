using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QQBot.Core.Chat;
using QQBot.Core.Commands;
using QQBot.Core.ComfyUI;
using QQBot.Core.Dispatcher;
using QQBot.Core.Hosted;
using QQBot.Core.Memory;
using QQBot.Core.OneBot;
using QQBot.Core.Options;
using QQBot.Core.Tools;

// Windows 控制台默认 GBK，设置 UTF-8 保证中文日志不乱码
Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = Host.CreateApplicationBuilder(args);

// 配置：确保从可执行文件所在目录读取 appsettings.json（兼容 dotnet run 与直接运行 exe）
builder.Configuration.SetBasePath(AppContext.BaseDirectory);
builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

// 文件日志：按天落盘 data/logs/yyyy-MM-dd.log，保留 7 天（后台面板「Debug 日志」页数据源）
var adminCfg = builder.Configuration.GetSection("Bot:Admin");
builder.Logging.AddProvider(new QQBot.Core.Logging.FileLoggerProvider(
    Path.Combine(AppContext.BaseDirectory, adminCfg["LogsDir"] ?? "data/logs"),
    int.TryParse(adminCfg["LogRetentionDays"], out var rd) ? rd : 7));

// 配置：绑定 Bot 节点，并注册为可直接注入的 BotOptions 单例
builder.Services
    .AddOptions<BotOptions>()
    .Bind(builder.Configuration.GetSection("Bot"))
    .ValidateOnStart();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<BotOptions>>().Value);

// 子配置节点也从 BotOptions 直接取（单例，随主配置一起加载）
builder.Services.AddSingleton(sp => sp.GetRequiredService<BotOptions>().Llm);
builder.Services.AddSingleton(sp => sp.GetRequiredService<BotOptions>().Prompt);
builder.Services.AddSingleton(sp => sp.GetRequiredService<BotOptions>().Memory);
builder.Services.AddSingleton(sp => sp.GetRequiredService<BotOptions>().Reply);
builder.Services.AddSingleton(sp => sp.GetRequiredService<BotOptions>().Command);
builder.Services.AddSingleton(sp => sp.GetRequiredService<BotOptions>().ComfyUI);
builder.Services.AddSingleton(sp => sp.GetRequiredService<BotOptions>().Shell);
builder.Services.AddSingleton(sp => sp.GetRequiredService<BotOptions>().AutoActivity);
builder.Services.AddSingleton(sp => sp.GetRequiredService<BotOptions>().Tools);

// 记忆系统（SQLite + 神经链记忆）
builder.Services.AddSingleton(sp =>
    new Database(Path.Combine(AppContext.BaseDirectory, sp.GetRequiredService<BotOptions>().Memory.DbPath),
                 sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Database>>()));
builder.Services.AddSingleton<MemoryService>();

// 核心服务
builder.Services.AddSingleton<OneBotClient>();
builder.Services.AddSingleton<EventDispatcher>();

// 主人命令系统（前缀命令，仅主人可用）
builder.Services.AddSingleton<CommandRouter>();
builder.Services.AddSingleton<IEnumerable<IBotCommand>>(sp =>
    BuiltinCommands.CreateAll(sp.GetRequiredService<ChatContext>(),
                              sp.GetRequiredService<Database>(),
                              sp.GetRequiredService<GenerateImageTool>(),
                              sp.GetRequiredService<MemoryService>()));

// 工具系统（LLM 自主函数调用）
builder.Services.AddSingleton<ToolRegistry>();
builder.Services.AddSingleton<ComfyClient>();
builder.Services.AddSingleton<GenerateImageTool>();
builder.Services.AddSingleton<QQBot.Core.Vision.VisionService>();
builder.Services.AddSingleton<IEnumerable<ITool>>(sp =>
    BuiltinTools.CreateAll(
        sp.GetRequiredService<Database>(),
        sp.GetRequiredService<MemoryService>(),
        sp.GetRequiredService<OneBotClient>(),
        sp.GetRequiredService<GenerateImageTool>(),
        sp.GetRequiredService<ShellOptions>(),
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ShellTool>>(),
        sp.GetRequiredService<BotOptions>().Prompt.MaxContextMessages));

// 对话引擎
builder.Services.AddSingleton<ChatEngine>(sp => new ChatEngine(
    sp.GetRequiredService<LlmOptions>(),
    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ChatEngine>>(),
    sp.GetRequiredService<BotOptions>().Debug));
builder.Services.AddSingleton(sp =>
    new ChatContext(sp.GetRequiredService<Database>(),
                    sp.GetRequiredService<BotOptions>().Prompt.MaxContextMessages));

// 宿主（管理连接生命周期）
builder.Services.AddHostedService<BotHostedService>();
builder.Services.AddHostedService<AutoActivityService>();
// 后台管理面板（Admin.Enabled 时启动，进程内嵌 HTTP 服务）
builder.Services.AddHostedService<QQBot.Core.Admin.AdminService>();

var app = builder.Build();

// 识图缓存启动清空：只保留运行期间压缩后的图片（防堆积）
try
{
    var visionCache = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data/vision_cache"));
    if (Directory.Exists(visionCache)) Directory.Delete(visionCache, true);
}
catch { /* 清理失败不影响启动 */ }

await app.RunAsync();
