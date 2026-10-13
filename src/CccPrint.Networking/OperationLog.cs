using System.Text;
using System.Text.Json;

namespace CccPrint.Networking;

public static class OperationLog
{
    private static readonly object Sync = new();

    // Callers supply fixed event labels and counts, never file names, bodies or exception messages.
    public static void Write(string directory, string category, string result,
        string reason = "", string id = "", long elapsedMs = 0, int count = 0)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(directory);
                var file = Path.Combine(directory, "operations.log");
                var line = JsonSerializer.Serialize(new {
                    time = DateTimeOffset.UtcNow, pid = Environment.ProcessId,
                    category, id, result, reason, elapsedMs, count
                }) + Environment.NewLine;
                if (File.Exists(file) && new FileInfo(file).Length + Encoding.UTF8.GetByteCount(line) > 1024 * 1024)
                {
                    for (var index = 3; index >= 1; index--)
                    {
                        var older = index == 1 ? file : $"{file}.{index - 1}";
                        var newer = $"{file}.{index}";
                        File.Delete(newer);
                        if (File.Exists(older)) File.Move(older, newer);
                    }
                }
                File.AppendAllText(file, line, Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // Diagnostics cannot interrupt publishing, synchronization or printing.
        }
    }
}
