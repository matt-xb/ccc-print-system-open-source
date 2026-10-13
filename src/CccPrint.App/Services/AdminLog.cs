using System.IO;
using CccPrint.Networking;

namespace CccPrint.Services;

public static class AdminLog
{
    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CCC资料打印系统公开版", "管理端", "logs");
    public static void Write(string category, string result, string reason = "", string id = "", long elapsedMs = 0, int count = 0) =>
        OperationLog.Write(DirectoryPath, category, result, reason, id, elapsedMs, count);
}
