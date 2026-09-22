using System.Windows;
using System.Windows.Input;
using DesktopPet.Pet;

namespace DesktopPet.UI;

/// <summary>
/// 输入框：双击宠物（或右键菜单「说话…」/托盘菜单）唤出，位置在宠物头顶。
/// Enter = 发送，Esc = 取消，点到别处（失焦）= 自动收起。
/// </summary>
public partial class InputWindow : Window
{
    private Window? _owner;
    private Action<string>? _onSubmit;

    public InputWindow()
    {
        InitializeComponent();
    }

    /// <summary>在 owner 头顶弹出输入框</summary>
    public void ShowFor(Window owner, Action<string> onSubmit)
    {
        _owner = owner;
        _onSubmit = onSubmit;
        Input.Text = "";

        if (!IsVisible) Show();
        Reposition();
        Activate();
        Input.Focus();
        Keyboard.Focus(Input);
    }

    private void Reposition()
    {
        if (_owner is null) return;
        UpdateLayout();
        var w = ActualWidth > 0 ? ActualWidth : 400;
        var h = ActualHeight > 0 ? ActualHeight : 60;
        Left = Math.Clamp(_owner.Left + (_owner.ActualWidth - w) / 2,
            SystemParameters.VirtualScreenLeft + 4,
            SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - w - 4);
        Top = Math.Clamp(_owner.Top - h + 6,
            SystemParameters.VirtualScreenTop + 4,
            SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - h - 4);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Submit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    private void OnSendClick(object sender, RoutedEventArgs e) => Submit();

    private void Submit()
    {
        var text = Input.Text.Trim();
        Hide();
        if (text.Length > 0) _onSubmit?.Invoke(text);
    }

    /// <summary>失焦自动收起（别在屏幕上挂一个没人管的输入框）</summary>
    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (IsVisible) Hide();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 主窗口关闭时才真关，平时只隐藏
        if (Application.Current.MainWindow?.IsVisible == true && _owner?.IsVisible == true)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
