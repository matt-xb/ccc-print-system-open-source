using System.IO;

namespace CccPrint.Client;

public static class ClientLog
{

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "CCC资料打印系统公开版",
        "打印端",
        "logs");

    public static void Write(string category, Exception exception) =>
        Event(category, "failed", reason: $"{exception.GetBaseException().GetType().Name}:0x{exception.GetBaseException().HResult:X8}");

    public static void Event(string category, string result, string reason = "", string id = "", long elapsedMs = 0, int count = 0) =>
        CccPrint.Networking.OperationLog.Write(LogDirectory, category, result, reason, id, elapsedMs, count);
}
