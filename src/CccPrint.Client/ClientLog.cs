using System.IO;

namespace CccPrint.Client;

public static class ClientLog
{
    private static readonly object SyncRoot = new();

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "CCC资料打印系统公开版",
        "打印端",
        "logs");

    public static void Write(string category, Exception exception)
    {
        try
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(LogDirectory);
                var path = Path.Combine(
                    LogDirectory,
                    $"client-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(
                    path,
                    $"[{DateTimeOffset.Now:O}] {category}{Environment.NewLine}" +
                    $"{exception}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败不能再次影响主程序。
        }
    }
}
