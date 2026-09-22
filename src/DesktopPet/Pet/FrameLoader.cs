using System.Windows.Media.Imaging;

namespace DesktopPet.Pet;

/// <summary>
/// 一个动作的帧集合：图片 + **每一帧各自的停留毫秒**。
/// <c>DelaysMs[i] == 0</c> = 这帧没带时长，按动作的 fps 算（普通 PNG 序列就是这种）。
/// </summary>
public sealed class ActionFrames
{
    public List<BitmapSource> Images { get; } = new();
    public List<int> DelaysMs { get; } = new();

    public int Count => Images.Count;

    /// <summary>带自带时长的帧数（GIF 拆出来的那些）——只用于日志</summary>
    public int TimedFrames => DelaysMs.Count(d => d > 0);

    /// <summary>整套帧播一遍大约多久（每帧：自带延时优先，没有就按 <paramref name="fallbackFps"/> 算）</summary>
    public double TotalMs(double fallbackFps)
    {
        var fps = fallbackFps > 0 ? fallbackFps : 4;
        double total = 0;
        for (var i = 0; i < Count; i++)
            total += i < DelaysMs.Count && DelaysMs[i] > 0 ? DelaysMs[i] : 1000.0 / fps;
        return total;
    }
}

/// <summary>
/// 帧文件读取：普通位图 1 帧；**<c>.gif</c> 拆成它自己的每一帧，并带上各帧自带的停留时长**。
///
/// 为什么非得自己拆：WPF 的 <c>Image</c> 配 <c>BitmapImage</c> 读 GIF **只会显示第一帧**，
/// 动画是死的（WPF 没有内置 GIF 动画支持）。用 <c>GifBitmapDecoder</c> 才能拿到全部帧。
/// </summary>
public static class FrameLoader
{
    /// <summary>读一张图；读不出来返回 null，原因写进 <paramref name="error"/></summary>
    public static List<(BitmapSource Image, int DelayMs)>? LoadFile(string path, out string? error)
    {
        error = null;
        try
        {
            if (Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase))
                return LoadGif(path, out error);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // 别锁文件，方便换素材
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return new() { (bmp, 0) };
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>把 GIF 解码成每一帧，连同各帧自带的延时（毫秒）</summary>
    private static List<(BitmapSource, int)> LoadGif(string path, out string? error)
    {
        error = null;
        var frames = new List<(BitmapSource, int)>();
        try
        {
            var dec = new GifBitmapDecoder(new Uri(path),
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            foreach (var f in dec.Frames)
            {
                if (f.CanFreeze) f.Freeze();
                frames.Add((f, ReadDelayMs(f)));
            }
            if (frames.Count == 0) error = "GIF 里一帧都没有";
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        return frames;
    }

    /// <summary>
    /// 读某帧的停留时长（毫秒）。GIF 把延时存在"图形控制扩展"里：<c>/grctlext/Delay</c>，单位 1/100 秒。
    /// 注意 **0 和 1 是编码器的"我没填"**：各家浏览器一律按 100ms 处理，这里跟着来
    /// （否则会出现"一帧 10ms、闪到根本看不见"）。读不到就返回 0 = 交给动作的 fps。
    /// </summary>
    private static int ReadDelayMs(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata meta && meta.GetQuery("/grctlext/Delay") is ushort raw)
            {
                var ms = raw * 10;
                return ms <= 10 ? 100 : ms;
            }
        }
        catch { /* 没元数据就当没写 */ }
        return 0;
    }
}
