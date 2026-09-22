using System.Windows;

namespace DesktopPet.Pet;

/// <summary>
/// 屏幕边缘吸附计算。
/// 关键点：WPF 的 Left/Top 是"设备无关单位(DIP)"，而 WinForms 的 Screen.WorkingArea 是物理像素——
/// 在缩放不是 100% 的屏幕上两者不相等，必须转换，否则高 DPI 下会吸错位置。
/// 另外用 FromPoint 取"她当前所在的那块屏"，多显示器下才不会吸到主屏边缘去。
/// </summary>
public static class WindowSnap
{
    /// <summary>她当前所在显示器的工作区（DIP 坐标：左、上、宽、高）</summary>
    public static (double X, double Y, double W, double H) WorkAreaDip(Window w)
    {
        try
        {
            var src = PresentationSource.FromVisual(w);
            if (src?.CompositionTarget is not null)
            {
                var toPx = src.CompositionTarget.TransformToDevice;
                var toDip = src.CompositionTarget.TransformFromDevice;

                var cx = w.Left + Math.Max(1, w.ActualWidth) / 2;
                var cy = w.Top + Math.Max(1, w.ActualHeight) / 2;
                var centerPx = toPx.Transform(new Point(cx, cy));

                var screen = System.Windows.Forms.Screen.FromPoint(
                    new System.Drawing.Point((int)centerPx.X, (int)centerPx.Y));
                var wa = screen.WorkingArea;

                var tl = toDip.Transform(new Point(wa.Left, wa.Top));
                var br = toDip.Transform(new Point(wa.Right, wa.Bottom));
                return (tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);
            }
        }
        catch (Exception ex)
        {
            PetLog.Warn($"取显示器工作区失败，回退主屏：{ex.Message}");
        }
        var fallback = SystemParameters.WorkArea;
        return (fallback.Left, fallback.Top, fallback.Width, fallback.Height);
    }

    /// <summary>
    /// 算出吸附目标位置。返回 null = 四条边都不在 threshold 距离内（不该吸）。
    /// 只动一个轴：取"离得最近的那条边"，把窗口贴上去，另一个轴保持不动。
    /// </summary>
    public static (double X, double Y, string Edge)? Target(Window w, double threshold)
    {
        var wa = WorkAreaDip(w);
        var width = Math.Max(1, w.ActualWidth);
        var height = Math.Max(1, w.ActualHeight);
        var x = w.Left;
        var y = w.Top;

        var left = wa.X;                    // 左边缘贴上去后的 X
        var right = wa.X + wa.W - width;    // 右边缘
        var top = wa.Y;                     // 上边缘
        var bottom = wa.Y + wa.H - height;  // 下边缘（任务栏之上）

        // 候选：括号里是"当前位置离贴上去还差多少"（负数=已经越过边缘，也要拉回来）
        var candidates = new (string Edge, double Dist, double X, double Y)[]
        {
            ("left",   x - left,   left,  y),
            ("right",  x - right,  right, y),
            ("top",    y - top,    x,     top),
            ("bottom", y - bottom, x,     bottom),
        };

        var pick = candidates.OrderBy(c => Math.Abs(c.Dist)).First();
        if (Math.Abs(pick.Dist) > Math.Max(1, threshold)) return null;
        return (pick.X, pick.Y, pick.Edge);
    }
}
