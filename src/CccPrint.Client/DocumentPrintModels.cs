using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using CccPrint.Networking;
using TinyPinyin;

namespace CccPrint.Client;

public sealed class DocumentGroupViewModel : INotifyPropertyChanged
{
    private bool _isExpanded;

    public DocumentGroupViewModel(
        string sourceRelativePath,
        IReadOnlyList<LibrarySyncFile> files)
    {
        SourceRelativePath = sourceRelativePath;
        Title = CleanTitle(Path.GetFileNameWithoutExtension(sourceRelativePath));
        Extension = Path.GetExtension(sourceRelativePath)
            .TrimStart('.')
            .ToUpperInvariant();
        LibraryId = files[0].LibraryId;
        CategoryId = DocumentSearch.GetCategoryId(sourceRelativePath);
        WorkshopCategoryId =
            DocumentSearch.GetWorkshopCategoryId(sourceRelativePath);
        CustomGroupName = GetCustomGroupName(
            sourceRelativePath,
            LibraryId,
            CategoryId,
            WorkshopCategoryId);
        AllItems = files
            .OrderBy(file => file.WorksheetIndex ?? int.MaxValue)
            .ThenBy(file => file.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(file => new SheetPrintItem(
                file,
                Title,
                files.Count > 1))
            .ToArray();
        VisibleItems = new ObservableCollection<SheetPrintItem>(AllItems);
        SearchText = DocumentSearch.BuildSearchText(
            Title,
            sourceRelativePath);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SourceRelativePath { get; }

    public string Title { get; }

    public string Extension { get; }

    public string LibraryId { get; }

    public string CategoryId { get; }

    public string? CustomGroupName { get; }

    public string WorkshopCategoryId { get; }

    public string SearchText { get; }

    public IReadOnlyList<SheetPrintItem> AllItems { get; }

    public ObservableCollection<SheetPrintItem> VisibleItems { get; }

    public int SheetCount => AllItems.Count;

    public bool IsSingleItem => SheetCount == 1;

    public bool HasMultipleItems => SheetCount > 1;

    public SheetPrintItem SingleItem => AllItems[0];

    public int TotalPages => AllItems.Sum(item => item.PageCount);

    public string Summary =>
        string.IsNullOrWhiteSpace(CustomGroupName)
            ? GetFileSummary()
            : $"{CustomGroupName} · {GetFileSummary()}";

    public bool IsExpanded
    {
        get => HasMultipleItems && _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    public bool ApplySearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query) ||
            DocumentSearch.IsMatch(SearchText, query))
        {
            SetVisibleItems(AllItems);
            IsExpanded = !string.IsNullOrWhiteSpace(query);
            return true;
        }

        var exactItems = AllItems
            .Where(item =>
                DocumentSearch.IsExactMatch(item.SearchText, query))
            .ToArray();
        var matchedItems = exactItems.Length > 0
            ? exactItems
            : AllItems.Where(item =>
                DocumentSearch.IsMatch(item.SearchText, query));
        SetVisibleItems(matchedItems);

        if (VisibleItems.Count > 0)
        {
            IsExpanded = true;
            return true;
        }

        return false;
    }

    private void SetVisibleItems(IEnumerable<SheetPrintItem> items)
    {
        var next = items.ToArray();
        if (VisibleItems.Count == next.Length &&
            VisibleItems.SequenceEqual(next))
        {
            return;
        }

        VisibleItems.Clear();
        foreach (var item in next)
        {
            VisibleItems.Add(item);
        }
    }

    private static string CleanTitle(string value) => value.Trim();

    private string GetFileSummary() =>
        SheetCount == 1
            ? $"{Extension} · {TotalPages} 页"
            : $"{Extension} · {SheetCount} 张子表 · 共 {TotalPages} 页";

    private static string? GetCustomGroupName(
        string sourceRelativePath,
        string libraryId,
        string categoryId,
        string workshopCategoryId)
    {
        var segments = sourceRelativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (libraryId == "ccc-gas" && categoryId == "workshop")
        {
            var businessGroup = workshopCategoryId switch
            {
                "premix" => "全预混",
                "self-draft" => "自吸风",
                "burner" => "燃烧机",
                "burner-process" => "炉头工艺卡",
                "burner-guide" => "炉头指导书",
                _ => null
            };
            if (businessGroup is not null || segments.Length <= 3)
            {
                return businessGroup;
            }

            return segments[2] switch
            {
                "全预混资料" => "全预混",
                "自吸风资料" => "自吸风",
                "燃烧机资料" => "燃烧机",
                "炉头作业指导书" => "炉头指导书",
                _ => segments[2]
            };
        }

        return segments.Length > 3 ? segments[2] : null;
    }

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class SheetPrintItem : INotifyPropertyChanged
{
    private bool _isSelected;
    private int _copies = 1;

    public SheetPrintItem(
        LibrarySyncFile file,
        string groupTitle,
        bool showWorksheetName)
    {
        File = file;
        GroupTitle = groupTitle;
        Name = showWorksheetName &&
               !string.IsNullOrWhiteSpace(file.WorksheetName)
            ? file.WorksheetName
            : groupTitle;
        SearchText = DocumentSearch.BuildSearchText(
            groupTitle,
            Name,
            file.DisplayName,
            file.SourceRelativePath);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public LibrarySyncFile File { get; }

    public string GroupTitle { get; }

    public string Name { get; }

    public string SearchText { get; }

    public int PageCount => File.PageCount;

    public bool RequiresReview => File.RequiresReview;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public int Copies
    {
        get => _copies;
        set
        {
            var next = Math.Clamp(value, 1, 99);
            if (_copies == next)
            {
                return;
            }

            _copies = next;
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}

public static class DocumentSearch
{
    public static string BuildSearchText(params string[] values)
    {
        var source = string.Join(' ', values.Where(value =>
            !string.IsNullOrWhiteSpace(value)));
        var normalized = Normalize(source);
        var fullPinyin = new StringBuilder();
        var initials = new StringBuilder();

        foreach (var character in source)
        {
            if (PinyinHelper.IsChinese(character))
            {
                var pinyin = PinyinHelper.GetPinyin(character)
                    .ToLowerInvariant();
                fullPinyin.Append(pinyin);
                if (pinyin.Length > 0)
                {
                    initials.Append(pinyin[0]);
                }
            }
            else if (char.IsLetterOrDigit(character))
            {
                var lower = char.ToLowerInvariant(character);
                fullPinyin.Append(lower);
                initials.Append(lower);
            }
        }

        return $"{normalized}|{fullPinyin}|{initials}";
    }

    public static bool IsMatch(string searchText, string query)
    {
        var normalizedQuery = Normalize(query);
        if (normalizedQuery.Length == 0)
        {
            return true;
        }

        var fields = searchText.Split('|');
        if (IsExactMatch(searchText, query))
        {
            return true;
        }

        var threshold = normalizedQuery.Length switch
        {
            <= 3 => 1,
            <= 7 => 2,
            _ => 3
        };
        return fields.Any(field =>
            GetClosestDistance(field, normalizedQuery, threshold) <= threshold);
    }

    public static bool IsExactMatch(string searchText, string query)
    {
        var normalizedQuery = Normalize(query);
        return normalizedQuery.Length == 0 ||
               searchText.Split('|').Any(field => field.Contains(
                   normalizedQuery,
                   StringComparison.OrdinalIgnoreCase));
    }

    public static string GetCategoryId(string relativePath)
    {
        var segments = relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        var library = segments.FirstOrDefault() ?? string.Empty;
        var department = segments.Length > 1 ? segments[1] : string.Empty;

        if (library.Equals(
                "政府资料",
                StringComparison.OrdinalIgnoreCase))
        {
            return department switch
            {
                "应急管理" => "emergency",
                "安全监管" => "safety",
                "政策法规" => "policy",
                _ => "other"
            };
        }

        if (department.Contains("仓库", StringComparison.OrdinalIgnoreCase))
        {
            return "warehouse";
        }

        if (department.Contains("车间", StringComparison.OrdinalIgnoreCase) ||
            department.Contains("生产", StringComparison.OrdinalIgnoreCase))
        {
            return "workshop";
        }

        if (department.Contains("财务", StringComparison.OrdinalIgnoreCase))
        {
            return "finance";
        }

        return "other";
    }

    public static string GetWorkshopCategoryId(string relativePath)
    {
        if (relativePath.Contains(
                "炉头工艺卡",
                StringComparison.OrdinalIgnoreCase) ||
            relativePath.Contains("炉头", StringComparison.OrdinalIgnoreCase) &&
            relativePath.Contains("工艺卡", StringComparison.OrdinalIgnoreCase))
        {
            return "burner-process";
        }

        if (relativePath.Contains(
                "炉头作业指导书",
                StringComparison.OrdinalIgnoreCase) ||
            relativePath.Contains("炉头", StringComparison.OrdinalIgnoreCase) &&
            relativePath.Contains("指导书", StringComparison.OrdinalIgnoreCase))
        {
            return "burner-guide";
        }

        if (relativePath.Contains("全预混", StringComparison.OrdinalIgnoreCase))
        {
            return "premix";
        }

        if (relativePath.Contains("自吸风", StringComparison.OrdinalIgnoreCase))
        {
            return "self-draft";
        }

        if (relativePath.Contains("燃烧机", StringComparison.OrdinalIgnoreCase))
        {
            return "burner";
        }

        return "other";
    }

    private static string Normalize(string value) =>
        new(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static int GetClosestDistance(
        string candidate,
        string query,
        int threshold)
    {
        if (candidate.Length == 0)
        {
            return query.Length;
        }

        if (candidate.Length <= query.Length + threshold)
        {
            return Levenshtein(candidate, query);
        }

        var best = int.MaxValue;
        var minimumLength = Math.Max(1, query.Length - threshold);
        var maximumLength = Math.Min(
            candidate.Length,
            query.Length + threshold);
        for (var windowLength = minimumLength;
             windowLength <= maximumLength;
             windowLength++)
        {
            for (var index = 0;
                 index <= candidate.Length - windowLength;
                 index++)
            {
                best = Math.Min(
                    best,
                    Levenshtein(
                        candidate.Substring(index, windowLength),
                        query));
                if (best <= threshold)
                {
                    return best;
                }
            }
        }

        return best;
    }

    private static int Levenshtein(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];

        for (var leftIndex = 1;
             leftIndex <= left.Length;
             leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1;
                 rightIndex <= right.Length;
                 rightIndex++)
            {
                var substitution =
                    left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1;
                current[rightIndex] = Math.Min(
                    Math.Min(
                        current[rightIndex - 1] + 1,
                        previous[rightIndex] + 1),
                    previous[rightIndex - 1] + substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
