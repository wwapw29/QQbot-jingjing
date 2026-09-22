using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopPet.Pet;

namespace DesktopPet.UI;

/// <summary>
/// 气泡窗口：独立于宠物窗口（不塞进角色窗，免得撑大窗口、挡住鼠标、缩放时把人拉变形）。
///  - 跟随宠物（移动/缩放时自动重新定位到头顶）
///  - 支持文字 + 图片（她要是当场画了图，直接在这儿给你看）
///  - **逐字显示**：文字按 bubble.typeSpeed（字/秒）一个个冒出来；**出完才开始算停留时长**
///  - 自动消失：停留时长 = bubble.baseSeconds + 字数 × bubble.secondsPerChar（baseSeconds ≤ 0 = 点一下才关）
///  - 对上层抛 <see cref="TypingFinished"/> / <see cref="Hidden"/>，动作编排（talk 循环 → 表情）由 MainWindow 接
/// </summary>
public partial class BubbleWindow : Window
{
    private readonly BubbleConfig _cfg = App.Config.Bubble;
    private Window? _owner;
    private DispatcherTimer? _hideTimer;
    private DispatcherTimer? _typeTimer;  // 打字机
    private bool? _lastFlipBelow;         // 上次是不是翻到了她脚下方（null=还没算过；只在变化时打日志）

    private string _fullText = "";        // 这句话的完整文字（逐字显示时按它取前 N 个字）
    private int _typeShown;               // 已经露出来的字数
    private double _typeAcc;              // 逐字累加器（按真实流逝时间算，不受定时器抖动影响）
    private DateTime _typeLastAt;
    private bool _hasImage;               // 这条是图片气泡（图片不逐字）
    private double _lastW, _lastH;        // 上次摆位时的尺寸（只有尺寸变了才重新摆）

    /// <summary>逐字出完了（或本来就不用逐字）——上层据此把 talk 换成表情动作、并知道"该开始算停留了"</summary>
    public event Action? TypingFinished;

    /// <summary>气泡被关掉了（自动到时 / 点了一下）</summary>
    public event Action? Hidden;

    public BubbleWindow()
    {
        InitializeComponent();

        // 外观全部来自 pet.json → 换气泡样式不用改代码
        BubbleBorder.CornerRadius = new CornerRadius(_cfg.CornerRadius);
        BubbleBorder.Background = Brush(_cfg.Background);
        BubbleBorder.BorderBrush = Brush(_cfg.Border);
        BubbleText.Foreground = Brush(_cfg.Foreground);
        BubbleText.FontFamily = new FontFamily(_cfg.FontFamily);
        BubbleText.FontSize = _cfg.FontSize;
        BubbleText.MaxWidth = _cfg.MaxWidth;
        BubbleBorder.MaxWidth = _cfg.MaxWidth + 24;
        BubbleImage.MaxWidth = _cfg.MaxWidth;
        BubbleImage.MaxHeight = 420;
        foreach (var tail in new[] { TailTop, TailBottom })
        {
            tail.Fill = Brush(_cfg.Background);
            tail.Stroke = Brush(_cfg.Border);
        }
    }

    /// <summary>XAML 里 Grid 的 Margin：给阴影留的空间（算尾巴位置时要减掉）</summary>
    private const double ShadowMargin = 14;

    /// <summary>尾巴半宽（Data 里是 14 宽）</summary>
    private const double TailHalfWidth = 7;

    private static Brush Brush(string hex)
    {
        try { return (Brush)new BrushConverter().ConvertFromString(hex)!; }
        catch (Exception ex)
        {
            PetLog.Warn($"颜色解析失败（{hex}）：{ex.Message}");
            return Brushes.White;
        }
    }

    /// <summary>鼠标穿透状态（游戏模式下气泡也不该挡住游戏操作）</summary>
    private bool _clickThrough;

    /// <summary>设置鼠标穿透；开启时点击会落到底下的程序上（气泡只能等它自己消失）</summary>
    public void SetClickThrough(bool on)
    {
        _clickThrough = on;
        ClickThrough.Set(this, on);
    }

    /// <summary>显示一条文字消息（同一个窗口复用；重复调用会替换内容并重新计时）</summary>
    public void ShowMessage(Window owner, string text) => Show(owner, text, null);

    /// <summary>显示一张图片（dataUrl 或本地文件路径），可附图注；解不出来就退回文字</summary>
    public void ShowPicture(Window owner, string source, string? caption)
    {
        var img = Load(source);
        if (img is null) Show(owner, caption ?? "（这张图打不开……）", null);
        else Show(owner, caption ?? "", img);
    }

    private void Show(Window owner, string text, BitmapImage? image)
    {
        _owner ??= owner;
        _owner.LocationChanged -= OnOwnerMoved;
        _owner.LocationChanged += OnOwnerMoved;
        _owner.SizeChanged -= OnOwnerMoved;
        _owner.SizeChanged += OnOwnerMoved;

        _typeTimer?.Stop();
        _typeTimer = null;
        _fullText = text ?? "";
        _hasImage = image is not null;

        // 逐字只对文字有意义（图片没有"字"）→ 图片整张直接出来
        var typeMs = image is null ? _cfg.TypeMs(_fullText.Length) : 0;
        _typeShown = 0;
        _typeAcc = 0;

        BubbleText.Text = typeMs > 0 ? "" : _fullText;
        BubbleText.Visibility = string.IsNullOrEmpty(_fullText) ? Visibility.Collapsed : Visibility.Visible;
        BubbleImage.Source = image;
        BubbleImage.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;

        if (!IsVisible) Show();
        if (_clickThrough) ClickThrough.Set(this, true);   // 窗口重建过 → 穿透状态要重新贴上
        UpdateLayout();          // SizeToContent：先把新内容量出来，否则按旧尺寸定位会偏
        _lastW = ActualWidth;
        _lastH = ActualHeight;
        Reposition();
        // 首帧尺寸偶尔量不准（尤其中文换行/图片刚解码），等布局落定后再正一次
        Dispatcher.BeginInvoke(new Action(Reposition), DispatcherPriority.Loaded);

        // 淡入（不抢焦点）
        Opacity = 0;
        BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));

        if (typeMs > 0)
        {
            PetLog.Info($"气泡逐字：{_fullText.Length} 字 @ {_cfg.TypeSpeed:0.#} 字/秒 → 约 {typeMs / 1000.0:0.#} 秒出完，" +
                        $"出完才开始算停留");
            _typeLastAt = DateTime.UtcNow;
            _typeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _typeTimer.Tick += OnTypeTick;
            _typeTimer.Start();
        }
        else
        {
            FinishTyping();      // 没逐字：直接进"该开始算停留了"那一步
        }
    }

    /// <summary>打字机：按真实流逝时间往外吐字，吐完就交给 <see cref="FinishTyping"/></summary>
    private void OnTypeTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        _typeAcc += (now - _typeLastAt).TotalSeconds * _cfg.TypeSpeed;
        _typeLastAt = now;

        var want = Math.Min(_fullText.Length, (int)_typeAcc);
        if (want <= _typeShown) return;

        _typeShown = want;
        BubbleText.Text = _fullText[.._typeShown];
        GrowReposition();
        if (_typeShown >= _fullText.Length) FinishTyping();
    }

    /// <summary>字变多了 → 气泡长大了 → 重新摆一次（不然会长出屏幕，尾巴也会指偏）</summary>
    private void GrowReposition()
    {
        UpdateLayout();
        if (Math.Abs(ActualWidth - _lastW) < 0.5 && Math.Abs(ActualHeight - _lastH) < 0.5) return;
        _lastW = ActualWidth;
        _lastH = ActualHeight;
        Reposition();
    }

    /// <summary>
    /// 文字全部出完（或本来就不用逐字）：补上完整文字 → **这时候才开始算停留时长**
    /// （主人的要求：字都出完了才计时，不然长句子会被提前收走）→ 通知上层收尾。
    /// </summary>
    private void FinishTyping()
    {
        _typeTimer?.Stop();
        _typeTimer = null;
        if (BubbleText.Text != _fullText)
        {
            BubbleText.Text = _fullText;
            GrowReposition();
        }
        RestartHideTimer(_hasImage);
        TypingFinished?.Invoke();
    }

    /// <summary>
    /// 按公式 <c>停留时长 = 保底秒数 m + 字数 × 每字秒数 n</c> 重新计时（可在后台调 m/n）。
    /// m ≤ 0 = 不自动消失（点一下才关）。字数按**完整文字**算（逐字过程中也算得对）。
    /// </summary>
    private void RestartHideTimer(bool isImage)
    {
        _hideTimer?.Stop();
        var chars = _fullText.Length;
        var ms = _cfg.DurationMs(chars, isImage);
        if (ms <= 0) return;

        PetLog.Info($"气泡停留 {ms / 1000.0:0.#} 秒 = 保底 {_cfg.BaseSeconds:0.#}s + {chars} 字 × {_cfg.SecondsPerChar:0.##}s/字" +
                    $"{(isImage ? "（图片另保底 12s）" : "")}");
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        _hideTimer.Tick += (_, _) => { _hideTimer!.Stop(); HideBubble(); };
        _hideTimer.Start();
    }

    /// <summary>把 dataUrl / 本地路径 解成可显示的位图；失败返回 null（退回纯文字）</summary>
    private static BitmapImage? Load(string source)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;

            if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = source.IndexOf(',');
                if (comma < 0) return null;
                var bytes = Convert.FromBase64String(source[(comma + 1)..]);
                bmp.StreamSource = new MemoryStream(bytes);
            }
            else if (File.Exists(source))
            {
                bmp.UriSource = new Uri(source);
            }
            else return null;

            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            PetLog.Warn($"气泡图片加载失败：{ex.Message}");
            return null;
        }
    }

    private void OnOwnerMoved(object? sender, EventArgs e) => Reposition();

    /// <summary>
    /// 把气泡摆到她头顶；头顶放不下（她贴着屏幕顶边）就翻到她脚下方——否则会被顶边截掉一半。
    /// 边界用"她所在那块屏的工作区"（DIP，不含任务栏）：多显示器/任务栏下比整块虚拟桌面靠谱。
    /// 尾巴会跟着换边，并对准她的中心。
    /// </summary>
    private void Reposition()
    {
        if (_owner is null) return;
        UpdateLayout();
        var w = ActualWidth > 0 ? ActualWidth : Math.Max(120, Width);
        var h = ActualHeight > 0 ? ActualHeight : 120;

        var (ax, ay, aw, ah) = WindowSnap.WorkAreaDip(_owner);

        var petLeft = _owner.Left;
        var petTop = _owner.Top;
        var petW = Math.Max(1, _owner.ActualWidth);
        var petH = Math.Max(1, _owner.ActualHeight);

        var x = petLeft + (petW - w) / 2;

        var aboveY = petTop - h + 4;                  // 贴在头顶（尾巴朝下）
        var belowY = petTop + petH - 4;               // 翻到脚下（尾巴朝上）
        var roomAbove = petTop - ay;                  // 头顶还有多少空间
        var roomBelow = ay + ah - (petTop + petH);    // 脚下还有多少空间

        // 头顶放不下，且脚下比头顶宽裕 → 翻下去；脚下也放不下就仍回头顶（下面会夹紧）
        var flipBelow = aboveY < ay && roomBelow >= roomAbove && belowY + h <= ay + ah;
        var y = flipBelow ? belowY : aboveY;

        Left = Math.Clamp(x, ax + 2, Math.Max(ax + 2, ax + aw - w - 2));
        Top = Math.Clamp(y, ay + 2, Math.Max(ay + 2, ay + ah - h - 2));

        // 尾巴换边 + 对准她（气泡被夹到屏幕边上时，尾巴也跟着偏，别指着空气）
        TailTop.Visibility = flipBelow ? Visibility.Visible : Visibility.Collapsed;
        TailBottom.Visibility = flipBelow ? Visibility.Collapsed : Visibility.Visible;
        if (_lastFlipBelow is null || _lastFlipBelow != flipBelow)
        {
            _lastFlipBelow = flipBelow;
            PetLog.Info($"气泡翻边：{(flipBelow ? "头顶放不下 → 翻到她脚下方（尾巴朝上）" : "放她头顶（尾巴朝下）")}；" +
                        $"工作区=({ax:0},{ay:0},{aw:0}x{ah:0}) 气泡={w:0}x{h:0} 落点=({Left:0},{Top:0})");
        }
        var tail = flipBelow ? TailTop : TailBottom;
        var tailLeft = petLeft + petW / 2 - Left - ShadowMargin - TailHalfWidth;
        var tailMax = Math.Max(0, w - ShadowMargin * 2 - TailHalfWidth * 2);
        tail.Margin = new Thickness(Math.Clamp(tailLeft, 0, tailMax), 0, 0, 0);
    }

    private void OnClickHide(object sender, MouseButtonEventArgs e) => HideBubble();

    private void HideBubble()
    {
        _hideTimer?.Stop();
        _typeTimer?.Stop();      // 逐字还没出完就被点掉了 → 停掉吐字，交给上层知道"说话结束了"
        _typeTimer = null;
        Hide();
        Hidden?.Invoke();
    }

    protected override void OnClosed(EventArgs e)
    {
        _typeTimer?.Stop();
        _typeTimer = null;
        _hideTimer?.Stop();
        if (_owner is not null)
        {
            _owner.LocationChanged -= OnOwnerMoved;
            _owner.SizeChanged -= OnOwnerMoved;
        }
        base.OnClosed(e);
    }
}
