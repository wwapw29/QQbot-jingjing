using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using DesktopPet.Pet;

namespace DesktopPet.UI;

/// <summary>
/// 托盘图标（用 WinForms 的 NotifyIcon，WPF 这边最省事的做法）：
/// 显示/隐藏、置顶、贴边、缩放、说话、打开日志、退出。
/// 宠物窗口被拖到屏幕外/被全屏应用盖住时，托盘是最后的"找回入口"。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly MainWindow _pet;
    private readonly ToolStripMenuItem _topItem = new("保持置顶") { CheckOnClick = false };
    private readonly ToolStripMenuItem _snapItem = new("边缘吸附") { CheckOnClick = false };
    private readonly ToolStripMenuItem _lockItem = new("锁定位置和大小") { CheckOnClick = false };
    private readonly ToolStripMenuItem _noTouchItem = new("游戏模式防误触") { CheckOnClick = false };

    public TrayIcon(MainWindow pet)
    {
        _pet = pet;
        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Visible = true,
            Text = $"{App.Config.Name}（桌面宠物）"
        };
        _icon.DoubleClick += (_, _) => ShowPet();
        _icon.ContextMenuStrip = BuildMenu();
        PetLog.Info("托盘图标已就绪");
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("让静静出来", null, (_, _) => ShowPet());
        menu.Items.Add("躲起来", null, (_, _) => _pet.Hide());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("说话…", null, (_, _) => { ShowPet(); _pet.ShowInputBox(); });
        menu.Items.Add("挥挥手", null, (_, _) => _pet.PlayAction("talk", 2));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("贴到最近的边", null, (_, _) => _pet.SnapToEdge());
        _snapItem.Click += (_, _) => _pet.ToggleSnap();
        menu.Items.Add(_snapItem);
        _topItem.Click += (_, _) => _pet.ToggleTopmost();
        menu.Items.Add(_topItem);
        menu.Items.Add("放大", null, (_, _) => _pet.ChangeScale(1.15));
        menu.Items.Add("缩小", null, (_, _) => _pet.ChangeScale(1 / 1.15));
        _lockItem.Click += (_, _) => _pet.ToggleLock();
        menu.Items.Add(_lockItem);
        _noTouchItem.Click += (_, _) => _pet.ToggleClickThroughInGame();
        menu.Items.Add(_noTouchItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开日志", null, (_, _) => OpenLog());
        menu.Items.Add("退出", null, (_, _) => System.Windows.Application.Current.Shutdown());

        // 每次展开时同步勾选状态（这些开关在宠物右键菜单里也能改）
        menu.Opening += (_, _) =>
        {
            _snapItem.Checked = App.Config.Snap.Enabled;
            _topItem.Checked = App.Config.AlwaysOnTop;
            _lockItem.Checked = App.Config.Locked;
            _noTouchItem.Checked = App.Config.ClickThroughInGame;
        };
        return menu;
    }

    private void ShowPet()
    {
        _pet.Show();
        _pet.Activate();
        _pet.Say("……叫我干嘛，我在这儿。");
    }

    /// <summary>优先用素材目录里的 tray.ico，没有就退化成系统图标（不至于没图标）</summary>
    private static Icon LoadIcon()
    {
        try
        {
            var path = Path.Combine(App.AppDir, App.Config.SkinDir(App.AppDir), "tray.ico");
            if (File.Exists(path)) return new Icon(path);
        }
        catch (Exception ex) { PetLog.Warn($"托盘图标加载失败：{ex.Message}"); }
        return SystemIcons.Application;
    }

    private static void OpenLog()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".jingjing-pet", "pet.log");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { PetLog.Warn($"打开日志失败：{ex.Message}"); }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
