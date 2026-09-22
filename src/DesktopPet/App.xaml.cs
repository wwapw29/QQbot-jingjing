using System.Windows;
using DesktopPet.Pet;
using DesktopPet.UI;

namespace DesktopPet;

/// <summary>
/// 程序入口：单实例保护 → 日志 → 载入配置 → 创建宠物窗口 + 托盘。
/// （不设 StartupUri，窗口全部手写创建，方便控制托盘与生命周期）
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private TrayIcon? _tray;

    /// <summary>程序所在目录（素材、配置的相对基准）</summary>
    public static string AppDir => AppContext.BaseDirectory;

    /// <summary>全局配置</summary>
    public static PetConfig Config { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        PetLog.Init(AppDir);

        // 单实例：桌面上躺两只静静会很吵
        _singleInstance = new Mutex(true, "DesktopPet_Jingjing_SingleInstance", out var isNew);
        if (!isNew)
        {
            PetLog.Warn("已有实例在运行，本次启动退出");
            Shutdown();
            return;
        }

        // 全局异常兜底：崩了也要留痕
        DispatcherUnhandledException += (_, args) =>
        {
            PetLog.Error("UI 线程未处理异常", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            PetLog.Error("非 UI 线程未处理异常", args.ExceptionObject as Exception);

        try
        {
            Config = PetConfig.Load(AppDir);
            PetLog.Info($"配置载入：皮肤={Config.AssetDir} 缩放={Config.Scale} 置顶={Config.AlwaysOnTop} 动作={string.Join(",", Config.Actions.Keys)}");

            var pet = new MainWindow();
            pet.Closed += (_, _) => PetLog.Info("宠物窗口已关闭");
            pet.Show();
            PetLog.Info("宠物窗口已显示");

            _tray = new TrayIcon(pet);
        }
        catch (Exception ex)
        {
            PetLog.Error("启动失败", ex);
            MessageBox.Show($"静静启动失败：{ex.Message}\n\n详情见日志：{PetConfig.UserConfigPath.Replace("pet.json", "pet.log")}",
                "桌面宠物", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Config.SaveUserConfig();
            _tray?.Dispose();
            PetLog.Info("已退出");
        }
        catch { /* 退出时不再折腾 */ }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
