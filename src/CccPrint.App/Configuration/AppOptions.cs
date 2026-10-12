using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CccPrint.Configuration;

public sealed class AppOptions
{
    public string Mode { get; init; } = "Admin";

    public string ServerHost { get; init; } = "pc-202502211303";

    public int ServerPort { get; init; } = 8010;

    public int DiscoveryPort { get; init; } = 8011;

    public string SourcePath { get; init; } = @"V:\10-18仓库资料2.0";

    public string? DataPath { get; init; }

    public string ClientUpdateVersion { get; init; } = "0.6.29";

    public string ClientUpdatePackagePath { get; init; } =
        @"..\installer\CCC资料打印端安装包_v0.6.29.exe";

    public string ClientUpdateNotes { get; init; } =
        "修复工艺卡目录中的焊接、制造工艺卡在打印端分组错误的问题。";

    public bool IsAdminMode =>
        string.Equals(Mode, "Admin", StringComparison.OrdinalIgnoreCase);

    public string ServerAddress => $"http://{ServerHost}:{ServerPort}";

    [JsonIgnore]
    public string DataRoot =>
        string.IsNullOrWhiteSpace(DataPath)
            ? Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "CCC资料打印系统",
                "管理端")
            : Environment.ExpandEnvironmentVariables(DataPath);

    public static AppOptions Load(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            return new AppOptions();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppOptions>(
                       json,
                       new JsonSerializerOptions
                       {
                           PropertyNameCaseInsensitive = true
                       })
                   ?? new AppOptions();
        }
        catch
        {
            return new AppOptions();
        }
    }
}
