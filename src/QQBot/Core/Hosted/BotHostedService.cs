using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QQBot.Core.Dispatcher;
using QQBot.Core.OneBot;
using QQBot.Core.Options;

namespace QQBot.Core.Hosted;

/// <summary>
/// 机器人宿主服务：负责装配订阅、启动连接、优雅关闭。
/// </summary>
public sealed class BotHostedService : BackgroundService
{
    private readonly OneBotClient _client;
    private readonly EventDispatcher _dispatcher;
    private readonly BotOptions _options;
    private readonly ILogger<BotHostedService> _logger;

    public BotHostedService(OneBotClient client, EventDispatcher dispatcher, BotOptions options, ILogger<BotHostedService> logger)
    {
        _client = client;
        _dispatcher = dispatcher;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _client.OnEvent += evt => _dispatcher.HandleAsync(evt);

        _logger.LogInformation("QQ 机器人启动中...（主人 OwnerId={OwnerId}）", _options.OwnerId);
        var login = await _client.GetLoginInfoAsync(stoppingToken);
        if (login is not null)
        {
            var uid = login["data"]?["user_id"]?.GetValue<long>();
            var nick = login["data"]?["nickname"]?.GetValue<string>();
            _logger.LogInformation("已连接机器人账号：{Nick} ({Uid})", nick, uid);
        }
        else
        {
            _logger.LogWarning("HTTP 接口暂不可用（NapCat 未启动？），将仅依赖 WebSocket 运行");
        }

        await _client.RunAsync(stoppingToken);
    }
}
