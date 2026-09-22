using System.Text;

namespace DesktopPet.Pet;

/// <summary>
/// 极简文件日志（pet.log）：桌面宠物出问题时没人看得见控制台，
/// 所以启动关键步骤、异常都落盘，方便排查（也是自检依据）。
/// </summary>
public static class PetLog
{
    private static readonly object Lock = new();
    private static string? _path;

    public static void Init(string appDir)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jingjing-pet");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "pet.log");
            // 简单轮转：超过 1MB 就归档一份
            if (File.Exists(_path) && new FileInfo(_path).Length > 1024 * 1024)
                File.Move(_path, _path + ".1", overwrite: true);
            Info($"======== {DateTime.Now:yyyy-MM-dd HH:mm:ss} 启动（{appDir}）========");
        }
        catch { _path = null; }
    }

    public static void Info(string msg) => Write("INF", msg);
    public static void Warn(string msg) => Write("WRN", msg);

    public static void Error(string msg, Exception? ex = null) =>
        Write("ERR", ex is null ? msg : $"{msg} :: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(string level, string msg)
    {
        if (_path is null) return;
        try
        {
            lock (Lock)
            {
                File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}\n", new UTF8Encoding(false));
            }
        }
        catch { /* 日志失败绝不拖累程序 */ }
    }
}
