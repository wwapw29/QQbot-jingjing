using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopPet.Pet;

/// <summary>
/// 鼠标穿透开关：给窗口加上/去掉 <c>WS_EX_TRANSPARENT</c> 扩展样式。
/// 加上之后这个窗口**完全不接收鼠标事件**（点击会落到底下真正在跑的程序上）——
/// 主人的游戏模式就用它防误触：宠物和气泡都不挡操作。
///
/// 注意：穿透期间宠物自己也拖不动、点不了（这是预期）；要恢复就点托盘菜单。
/// </summary>
public static class ClickThrough
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    private static long GetStyle(IntPtr h) => IntPtr.Size == 8 ? GetWindowLongPtr64(h, GWL_EXSTYLE).ToInt64() : GetWindowLong32(h, GWL_EXSTYLE);

    private static void SetStyle(IntPtr h, long value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(h, GWL_EXSTYLE, new IntPtr(value));
        else SetWindowLong32(h, GWL_EXSTYLE, (int)value);
    }

    /// <summary>当前是否已穿透</summary>
    public static bool IsOn(Window? w)
    {
        try
        {
            var h = HandleOf(w);
            if (h == IntPtr.Zero) return false;
            return (GetStyle(h) & WS_EX_TRANSPARENT) != 0;
        }
        catch { return false; }
    }

    /// <summary>开/关穿透；窗口还没创建句柄时直接跳过（返回 false 表示没生效）</summary>
    public static bool Set(Window? w, bool on)
    {
        try
        {
            var h = HandleOf(w);
            if (h == IntPtr.Zero) return false;

            var style = GetStyle(h);
            var next = on ? (style | WS_EX_TRANSPARENT) : (style & ~(long)WS_EX_TRANSPARENT);
            if (next == style) return true;
            SetStyle(h, next);
            return true;
        }
        catch (Exception ex)
        {
            PetLog.Warn($"设置鼠标穿透失败：{ex.Message}");
            return false;
        }
    }

    private static IntPtr HandleOf(Window? w)
    {
        if (w is null) return IntPtr.Zero;
        var helper = new WindowInteropHelper(w);
        return helper.Handle;   // 窗口已显示才有句柄
    }
}
