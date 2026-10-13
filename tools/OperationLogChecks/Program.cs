using CccPrint.Networking;
using System.Text.Json;

var directory = Path.Combine(Path.GetTempPath(), "ccc-operation-log-" + Guid.NewGuid());
try
{
    var id = Guid.NewGuid().ToString("N");
    OperationLog.Write(directory, "print", "started", id: id, count: 3);
    OperationLog.Write(directory, "print", "submitted", id: id, elapsedMs: 12, count: 3);
    var file = Path.Combine(directory, "operations.log");
    var lines = File.ReadAllLines(file);
    Check(lines.Length == 2 && lines.All(line => JsonDocument.Parse(line).RootElement.GetProperty("id").GetString() == id));
    for (var index = 0; index < 5; index++)
    {
        File.WriteAllText(file, new string('x', 1024 * 1024));
        OperationLog.Write(directory, "rotation", "completed");
    }
    Check(Directory.GetFiles(directory).Length == 4 && new FileInfo(file).Length < 1024 * 1024);
    var blocked = Path.Combine(directory, "blocked");
    File.WriteAllText(blocked, "file");
    OperationLog.Write(blocked, "unwritable", "completed");
    Console.WriteLine("CCC log correlation, metadata, rotation and unwritable directory passed.");
}
finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

static void Check(bool condition) { if (!condition) throw new Exception("Operation log check failed"); }
