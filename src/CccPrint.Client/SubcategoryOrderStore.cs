using System.IO;
using System.Text.Json;

namespace CccPrint.Client;

public sealed class SubcategoryOrderStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CCC资料打印系统公开版",
        "打印端",
        "subcategory-order.json");

    public Dictionary<string, List<string>> Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new Dictionary<string, List<string>>(
                StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var stored = JsonSerializer.Deserialize<
                Dictionary<string, List<string>>>(
                File.ReadAllText(_settingsPath),
                JsonOptions);
            return stored is null
                ? new Dictionary<string, List<string>>(
                    StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, List<string>>(
                    stored,
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is IOException or JsonException)
        {
            ClientLog.Write("读取分组排序失败", exception);
            return new Dictionary<string, List<string>>(
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public static IReadOnlyList<string> ApplyOrder(
        IReadOnlyList<string> discovered,
        IReadOnlyList<string> saved)
    {
        var available = discovered.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        return saved
            .Where(available.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Concat(discovered.Where(name => !saved.Contains(
                name,
                StringComparer.OrdinalIgnoreCase)))
            .ToArray();
    }

    public static IReadOnlyList<string> Move(
        IReadOnlyList<string> current,
        string sourceName,
        string targetName,
        bool insertAfter)
    {
        var names = current.ToList();
        if (sourceName.Equals(
                targetName,
                StringComparison.OrdinalIgnoreCase) ||
            !names.Remove(sourceName))
        {
            return names;
        }

        var targetIndex = names.FindIndex(name => name.Equals(
            targetName,
            StringComparison.OrdinalIgnoreCase));
        if (targetIndex < 0)
        {
            return current;
        }

        names.Insert(
            targetIndex + (insertAfter ? 1 : 0),
            sourceName);
        return names;
    }

    public void Save(IReadOnlyDictionary<string, List<string>> orders)
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(
                directory,
                $".subcategory-order-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(orders, JsonOptions));
            File.Move(temporaryPath, _settingsPath, true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            ClientLog.Write("保存分组排序失败", exception);
        }
    }
}
