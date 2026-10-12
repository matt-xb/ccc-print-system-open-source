using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CccPrint.Configuration;
using CccPrint.Models;
using CccPrint.Networking;
using CccPrint.Services;
using Microsoft.Win32;
using Microsoft.VisualBasic.FileIO;

namespace CccPrint;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".doc",
            ".docx",
            ".xls",
            ".xlsx",
            ".pdf"
        };
    private static readonly DestinationOption[] LibraryDestinations =
    {
        new("CCC 燃气资料库", "CCC燃气资料"),
        new("电加热资料库", "电加热资料"),
        new("政府资料库", "政府资料")
    };
    private static readonly DestinationOption[] CategoryDestinations =
    {
        new("仓库", "仓库"),
        new("车间", "车间"),
        new("财务", "财务"),
        new("普通资料", "普通资料")
    };
    private static readonly DestinationOption[] GovernmentCategoryDestinations =
    {
        new("应急管理", "应急管理"),
        new("安全监管", "安全监管"),
        new("政策法规", "政策法规"),
        new("其他资料", "其他资料")
    };
    private static readonly DestinationOption[] WorkshopDestinations =
    {
        new("请选择", string.Empty),
        new("全预混", "全预混资料"),
        new("自吸风", "自吸风资料"),
        new("燃烧机", "燃烧机资料"),
        new("炉头工艺卡", "炉头工艺卡"),
        new("炉头指导书", "炉头作业指导书")
    };

    private readonly AppOptions _options;
    private readonly LibraryCatalogService _catalog;
    private readonly SourceBaselineService _baseline;
    private readonly DraftPreparationService _drafts;
    private readonly DraftReviewService _notes;
    private readonly PublishedLibraryService _published;
    private readonly TestLibraryService _testLibrary;
    private readonly ClientInventoryService _clientInventory;
    private IReadOnlyList<SourceChangeRow> _changeRows =
        Array.Empty<SourceChangeRow>();
    private IReadOnlyList<DraftBatch> _batches =
        Array.Empty<DraftBatch>();
    private IReadOnlyList<PublishItemRow> _publishItems =
        Array.Empty<PublishItemRow>();
    private IReadOnlyList<LibrarySyncManifest> _publishedHistory =
        Array.Empty<LibrarySyncManifest>();
    private LibrarySyncManifest _currentManifest = EmptyManifest();
    private LibrarySyncManifest _effectiveManifest = EmptyManifest();
    private LibrarySyncManifest? _viewingArchiveManifest;
    private ClientLibraryInventory? _reportedInventory;
    private DraftBatch? _selectedBatch;
    private DraftReviewState? _releaseState;
    private bool _changingBatch;
    private bool _changingPublishedFilter;
    private bool _loadingReleaseNote;
    private bool _isBusy;
    private readonly HashSet<string> _selectedPublishedRemovalPaths =
        new(StringComparer.OrdinalIgnoreCase);

    public MainWindow(AppOptions options, LibraryCatalogService catalog)
    {
        InitializeComponent();
        _options = options;
        _catalog = catalog;
        _baseline = new SourceBaselineService(
            options.SourcePath,
            options.DataRoot);
        _drafts = new DraftPreparationService(
            options.SourcePath,
            options.DataRoot);
        _notes = new DraftReviewService(options.DataRoot);
        _published = new PublishedLibraryService(
            options.DataRoot,
            options.SourcePath);
        _testLibrary = new TestLibraryService(options.DataRoot, _drafts);
        _clientInventory = new ClientInventoryService(options.DataRoot);

        ImportLibrarySelector.ItemsSource = LibraryDestinations;
        ImportLibrarySelector.SelectedIndex = 0;
        RefreshImportCategoryOptions();
    }

    public void SetServerStatus(bool isRunning, string message)
    {
        var version = Assembly.GetExecutingAssembly()
            .GetName()
            .Version?
            .ToString(3) ?? "0.0.0";
        ServerStatusText.Text =
            $"{(isRunning ? "服务正常" : "服务未启动")} · v{version}";
        ServerStatusDot.Fill = new SolidColorBrush(
            isRunning
                ? Color.FromRgb(25, 120, 90)
                : Color.FromRgb(208, 82, 67));
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AnimatePageIn();
        await LoadStateAsync();
    }

    private void LibraryPageButton_Click(
        object sender,
        RoutedEventArgs e) =>
        ShowManagementPage(LibraryPage, LibraryPageButton);

    private void ChangesPageButton_Click(
        object sender,
        RoutedEventArgs e) =>
        ShowManagementPage(ChangesPage, ChangesPageButton);

    private void PublishPageButton_Click(
        object sender,
        RoutedEventArgs e) =>
        ShowManagementPage(PublishPage, PublishPageButton);

    private void ShowManagementPage(
        FrameworkElement page,
        Button activeButton)
    {
        foreach (var candidate in new[]
                 {
                     LibraryPage,
                     ChangesPage,
                     PublishPage
                 })
        {
            candidate.Visibility = candidate == page
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        foreach (var button in new[]
                 {
                     LibraryPageButton,
                     ChangesPageButton,
                     PublishPageButton
                 })
        {
            button.Style = (Style)FindResource(
                button == activeButton
                    ? "ManagementNavActiveButton"
                    : "ManagementNavButton");
        }

        if (!SystemParameters.ClientAreaAnimation || !IsLoaded)
        {
            return;
        }

        page.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(
                0,
                1,
                TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new QuadraticEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            });
    }

    private async void RefreshButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await LoadStateAsync(_selectedBatch?.BatchId);
    }

    private void ImportDestinationSelector_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (sender == ImportLibrarySelector)
        {
            RefreshImportCategoryOptions();
        }

        ImportWorkshopGroupPanel.Visibility = Visibility.Collapsed;
        ImportCustomGroupPanel.Visibility =
            ImportLibrarySelector.SelectedItem is DestinationOption selectedLibrary &&
            ImportCategorySelector.SelectedItem is DestinationOption selectedCategory
                ? Visibility.Visible
                : Visibility.Collapsed;
        RefreshCustomGroupOptions();
    }

    private void RefreshImportCategoryOptions()
    {
        if (ImportCategorySelector is null ||
            ImportLibrarySelector.SelectedItem is not DestinationOption library)
        {
            return;
        }

        var categories = GetCategoryDestinations(library);
        var previousFolder =
            (ImportCategorySelector.SelectedItem as DestinationOption)
            ?.FolderName;
        ImportCategorySelector.ItemsSource = categories;
        ImportCategorySelector.SelectedItem = categories.FirstOrDefault(
            option => option.FolderName.Equals(
                previousFolder,
                StringComparison.OrdinalIgnoreCase));
        if (ImportCategorySelector.SelectedItem is null)
        {
            ImportCategorySelector.SelectedIndex = 0;
        }
    }

    private async void ChooseSourceFilesButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (ImportLibrarySelector.SelectedItem is not DestinationOption library ||
            ImportCategorySelector.SelectedItem is not DestinationOption category)
        {
            return;
        }

        string? customGroup;
        try
        {
            customGroup = ValidateCustomGroupName(
                ImportCustomGroupSelector.Text);
        }
        catch (InvalidDataException exception)
        {
            MessageBox.Show(
                exception.Message,
                "分组名称无效",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var subgroupName = customGroup;
        var destinationLabel = string.IsNullOrWhiteSpace(subgroupName)
            ? $"{library.Name} → {category.Name}"
            : $"{library.Name} → {category.Name} → {subgroupName}";

        var dialog = new OpenFileDialog
        {
            Title = $"选择要发布到“{destinationLabel}”的资料",
            Filter = "可发布资料|*.doc;*.docx;*.xls;*.xlsx;*.pdf|" +
                     "Word 文件|*.doc;*.docx|Excel 文件|*.xls;*.xlsx|" +
                     "PDF 文件|*.pdf",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var categoryDirectory = ResolveCategoryDirectory(library, category);
        var targetDirectory = string.IsNullOrWhiteSpace(customGroup)
            ? categoryDirectory
            : Path.Combine(
                categoryDirectory,
                NormalizeGroupFolderName(library, category, customGroup));
        var imports = dialog.FileNames
            .Select(source => new
            {
                Source = Path.GetFullPath(source),
                Destination = Path.Combine(
                    targetDirectory,
                    Path.GetFileName(source))
            })
            .ToArray();
        var replacing = imports
            .Where(item =>
                File.Exists(item.Destination) &&
                !item.Source.Equals(
                    Path.GetFullPath(item.Destination),
                    StringComparison.OrdinalIgnoreCase))
            .Select(item => Path.GetFileName(item.Destination))
            .ToArray();
        var message =
            $"资料去向：{destinationLabel}\n" +
            $"已选择 {imports.Length} 个文件。";
        if (replacing.Length > 0)
        {
            message +=
                $"\n\n其中 {replacing.Length} 个同名文件会替换现有源资料：\n" +
                string.Join("\n", replacing.Take(6));
        }

        if (MessageBox.Show(
                message,
                replacing.Length > 0 ? "确认替换资料" : "确认加入资料",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            SetBusy(true, "正在加入所选资料…");
            Directory.CreateDirectory(targetDirectory);
            await Task.Run(() =>
            {
                foreach (var item in imports)
                {
                    if (item.Source.Equals(
                            Path.GetFullPath(item.Destination),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    File.Copy(item.Source, item.Destination, true);
                }
            });
            await LoadStateAsync(_selectedBatch?.BatchId);
            var importedPaths = imports
                .Select(item => Path.GetRelativePath(
                        _options.SourcePath,
                        item.Destination)
                    .Replace(Path.DirectorySeparatorChar, '/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _changeRows.Where(row =>
                         importedPaths.Contains(row.Change.RelativePath)))
            {
                row.IsSelected = row.CanSelect;
            }

            ChangesList.Items.Refresh();
            UpdatePrepareAction();
            MessageBox.Show(
                $"已加入 {imports.Length} 个文件，并自动选中待处理项。\n\n" +
                $"发布位置：{destinationLabel}\n" +
                "下一步点击“生成本次发布内容”。",
                "资料已加入",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.GetBaseException().Message,
                "无法加入资料",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, "资料选择完成");
        }
    }

    private void RefreshCustomGroupOptions()
    {
        if (ImportCustomGroupSelector is null ||
            ImportLibrarySelector.SelectedItem is not DestinationOption library ||
            ImportCategorySelector.SelectedItem is not DestinationOption category)
        {
            return;
        }

        var categoryDirectory = ResolveCategoryDirectory(library, category);
        var sourceGroups = Directory.Exists(categoryDirectory)
            ? Directory.EnumerateDirectories(categoryDirectory)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => GetGroupDisplayName(name!))
            : Array.Empty<string>();
        IEnumerable<string> additionalGroups = Array.Empty<string>();
        if (library.FolderName == "CCC燃气资料" &&
            category.FolderName == "车间")
        {
            additionalGroups = WorkshopDestinations
                .Where(option => !string.IsNullOrWhiteSpace(option.FolderName))
                .Select(option => option.Name);
        }

        ImportCustomGroupSelector.ItemsSource = BuildCustomGroupOptions(
            sourceGroups,
            _effectiveManifest,
            library.FolderName,
            category.FolderName,
            additionalGroups);
        ImportCustomGroupSelector.Text = string.Empty;
    }

    private void SelectAllChangesButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var select = _changeRows.Any(row =>
            row.CanSelect && !row.IsSelected);
        foreach (var row in _changeRows.Where(row => row.CanSelect))
        {
            row.IsSelected = select;
        }

        ChangesList.Items.Refresh();
        UpdatePrepareAction();
    }

    private async void MoveSelectedToGroupButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (ImportLibrarySelector.SelectedItem is not DestinationOption library ||
            ImportCategorySelector.SelectedItem is not DestinationOption category)
        {
            return;
        }

        string? groupName;
        try
        {
            groupName = ValidateCustomGroupName(
                ImportCustomGroupSelector.Text);
        }
        catch (InvalidDataException exception)
        {
            MessageBox.Show(
                exception.Message,
                "分组名称无效",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(groupName))
        {
            MessageBox.Show(
                "先输入要移动到的分组名称。",
                "请输入分组",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var selected = _changeRows
            .Where(row =>
                row.IsSelected &&
                row.Kind == "added" &&
                IsSameDestination(
                    row.Change.RelativePath,
                    library,
                    category))
            .ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(
                "请先勾选当前分类中尚未发布的新增资料。",
                "没有可移动资料",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var moves = selected
            .Select(row =>
            {
                var source = ResolveSourcePath(row.Change.RelativePath);
                return new
                {
                    Source = source,
                    Destination = Path.Combine(
                        ResolveSourceCategoryDirectory(source, library),
                        NormalizeGroupFolderName(
                            library,
                            category,
                            groupName),
                        row.FileName)
                };
            })
            .Where(move => !move.Source.Equals(
                Path.GetFullPath(move.Destination),
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var collision = moves.FirstOrDefault(move =>
            File.Exists(move.Destination));
        if (collision is not null)
        {
            MessageBox.Show(
                $"分组中已经存在同名文件：{Path.GetFileName(collision.Destination)}",
                "无法移动资料",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (MessageBox.Show(
                $"将 {moves.Length} 份资料移动到分组“{groupName}”？",
                "确认调整分组",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            SetBusy(true, "正在调整资料分组…");
            foreach (var move in moves)
            {
                Directory.CreateDirectory(
                    Path.GetDirectoryName(move.Destination)!);
                File.Move(move.Source, move.Destination);
            }

            await LoadStateAsync(_selectedBatch?.BatchId);
        }
        catch (Exception exception)
        {
            await LoadStateAsync(_selectedBatch?.BatchId);
            MessageBox.Show(
                exception.GetBaseException().Message,
                "无法调整分组",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, "资料分组已调整");
        }
    }

    private void ChangeCheckBox_Click(
        object sender,
        RoutedEventArgs e) =>
        UpdatePrepareAction();

    private async void DismissSourceChangeButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button
            {
                DataContext: SourceChangeRow row
            })
        {
            return;
        }

        var isAdded = row.Kind == "added";
        var message = isAdded
            ? $"从待处理列表移除“{row.FileName}”？\n\n" +
              "文件会移到 Windows 回收站，不会影响仓库里已经正式发布的资料。"
            : $"忽略“{row.FileName}”的这次{row.KindLabel}？\n\n" +
              "源文件和仓库里已经正式发布的资料都会保留，" +
              "这次变化将不再出现在待处理列表中。";
        if (MessageBox.Show(
                message,
                isAdded ? "移除新增资料" : "忽略本次变化",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            SetBusy(true, isAdded
                ? "正在移除新增资料…"
                : "正在忽略本次变化…");
            if (isAdded)
            {
                var sourcePath = ResolveSourcePath(row.Change.RelativePath);
                if (File.Exists(sourcePath))
                {
                    MoveSourceFileToRecycleBin(sourcePath);
                }
            }
            else
            {
                await _baseline.AcceptPublishedAsync(
                    new[] { row.Change.RelativePath });
            }

            await LoadStateAsync(_selectedBatch?.BatchId);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.GetBaseException().Message,
                isAdded ? "无法移除资料" : "无法忽略变化",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, isAdded
                ? "新增资料已移除"
                : "本次变化已忽略");
        }
    }

    private async void ClearPendingChangesButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_changeRows.Count == 0)
        {
            return;
        }

        var addedCount = _changeRows.Count(row => row.Kind == "added");
        var modifiedCount = _changeRows.Count(row =>
            row.Kind == "modified");
        var removedCount = _changeRows.Count(row =>
            row.Kind == "removed");
        var message =
            $"清空当前 {_changeRows.Count} 项待处理变化？\n\n" +
            $"新增 {addedCount} 项：源文件移到 Windows 回收站。\n" +
            $"替换 {modifiedCount} 项、删除 {removedCount} 项：忽略本次变化。\n\n" +
            "仓库里已经正式发布的资料不会被改动。";
        if (MessageBox.Show(
                message,
                "清空待处理",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            SetBusy(true, "正在清空待处理变化…");
            foreach (var row in _changeRows.Where(row =>
                         row.Kind == "added"))
            {
                var sourcePath = ResolveSourcePath(row.Change.RelativePath);
                if (File.Exists(sourcePath))
                {
                    MoveSourceFileToRecycleBin(sourcePath);
                }
            }

            var ignoredPaths = _changeRows
                .Where(row => row.Kind != "added")
                .Select(row => row.Change.RelativePath)
                .ToArray();
            if (ignoredPaths.Length > 0)
            {
                await _baseline.AcceptPublishedAsync(ignoredPaths);
            }

            await LoadStateAsync(_selectedBatch?.BatchId);
        }
        catch (Exception exception)
        {
            await LoadStateAsync(_selectedBatch?.BatchId);
            MessageBox.Show(
                exception.GetBaseException().Message,
                "无法清空待处理",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, "待处理变化已清空");
        }
    }

    private string ResolveSourcePath(string relativePath)
    {
        var sourceRoot = Path.GetFullPath(_options.SourcePath)
            .TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var sourcePath = Path.GetFullPath(Path.Combine(
            sourceRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!sourcePath.StartsWith(
                sourceRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("待处理资料不在源资料目录内。");
        }

        return sourcePath;
    }

    private static void MoveSourceFileToRecycleBin(string sourcePath) =>
        FileSystem.DeleteFile(
            sourcePath,
            UIOption.OnlyErrorDialogs,
            RecycleOption.SendToRecycleBin);

    private async void PrepareDraftButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var selected = _changeRows
            .Where(row => row.CanSelect && row.IsSelected)
            .ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        var convert = selected
            .Where(row => row.Change.Kind != "removed")
            .Select(row => row.Change.RelativePath)
            .ToArray();
        var remove = selected
            .Where(row => row.Change.Kind == "removed")
            .Select(row => row.Change.RelativePath)
            .ToArray();
        var answer = MessageBox.Show(
            $"本次将处理 {selected.Length} 组资料：\n" +
            $"转换或替换 {convert.Length} 组，删除 {remove.Length} 组。\n\n" +
            "Excel 的每张可见子表都会生成独立 PDF。\n" +
            "生成后可以预览，确认无误再发布。",
            "生成本次发布内容",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            SetBusy(true, "正在使用 WPS 转换 PDF…");
            var batch = await _drafts.PrepareAsync(convert, remove);
            await LoadStateAsync(batch.BatchId);
            ShowManagementPage(PublishPage, PublishPageButton);
            MessageBox.Show(
                $"已生成 {batch.PdfCount} 份 PDF。\n" +
                $"其中 Excel 子表会逐张列出；另有 {batch.RemovedSourcePaths.Count} 组待删除资料。\n\n" +
                "请在下方抽查 PDF，填写更新说明后发布。",
                "发布内容已准备",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.GetBaseException().Message,
                "无法生成发布内容",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, "发布内容准备完成");
        }
    }

    private async void RemovePublishedButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var selectedGroups = _effectiveManifest.Files
            .GroupBy(
                file => file.SourceRelativePath,
                StringComparer.OrdinalIgnoreCase)
            .Where(group =>
                _selectedPublishedRemovalPaths.Contains(group.Key))
            .Select(group => new
            {
                SourceRelativePath = group.Key,
                PdfCount = group.Count()
            })
            .OrderBy(
                group => group.SourceRelativePath,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedGroups.Length == 0)
        {
            return;
        }

        if (selectedGroups.Length > 20)
        {
            MessageBox.Show(
                "单次最多移除 20 组资料，请减少勾选后重试。",
                "选择过多",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var preview = string.Join(
            "\n",
            selectedGroups
                .Take(8)
                .Select(group =>
                    $"• {GetDestinationLabel(group.SourceRelativePath)} → " +
                    Path.GetFileName(group.SourceRelativePath)));
        if (selectedGroups.Length > 8)
        {
            preview += $"\n• 另有 {selectedGroups.Length - 8} 组资料";
        }

        var answer = MessageBox.Show(
            $"将从仓库正式资料中移除 {selectedGroups.Length} 组资料，" +
            $"共 {selectedGroups.Sum(group => group.PdfCount)} 份 PDF：\n\n" +
            $"{preview}\n\n源 Word/Excel/PDF 不会被删除。\n" +
            "生成发布内容后，还需要点击“立即发布到仓库”才会生效。",
            "批量移除仓库资料",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            SetBusy(true, "正在准备删除资料…");
            var batch = await _drafts.PrepareAsync(
                Array.Empty<string>(),
                selectedGroups
                    .Select(group => group.SourceRelativePath)
                    .ToArray());
            _selectedPublishedRemovalPaths.Clear();
            await LoadStateAsync(batch.BatchId);
            ShowManagementPage(PublishPage, PublishPageButton);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.GetBaseException().Message,
                "无法准备删除",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, "删除操作已加入本次发布");
        }
    }

    private void PublishedRemovalCheckBox_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not CheckBox
            {
                DataContext: PublishedTreeNode
                {
                    CanSelectForRemoval: true,
                    SourceGroup: not null
                } node
            })
        {
            return;
        }

        if (node.IsRemovalSelected)
        {
            _selectedPublishedRemovalPaths.Add(
                node.SourceGroup.SourceRelativePath);
        }
        else
        {
            _selectedPublishedRemovalPaths.Remove(
                node.SourceGroup.SourceRelativePath);
        }

        UpdatePublishedRemovalAction();
        e.Handled = true;
    }

    private void UpdatePublishedRemovalAction()
    {
        var count = _selectedPublishedRemovalPaths.Count;
        RemovePublishedButton.Content = count == 0
            ? "移除已选"
            : $"移除已选（{count}）";
        RemovePublishedButton.IsEnabled =
            !_isBusy && _viewingArchiveManifest is null && count > 0;
    }

    private async void BatchSelector_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_changingBatch ||
            BatchSelector.SelectedItem is not BatchOption option)
        {
            return;
        }

        await SelectBatchAsync(option.Batch);
    }

    private void CurrentLibraryViewButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_viewingArchiveManifest is not null)
        {
            _viewingArchiveManifest = null;
            BindPublishedLibrary();
        }

        SetLibraryView(false);
    }

    private void HistoryLibraryViewButton_Click(
        object sender,
        RoutedEventArgs e) =>
        SetLibraryView(true);

    private void SetLibraryView(bool showHistory)
    {
        CurrentLibraryView.Visibility = showHistory
            ? Visibility.Collapsed
            : Visibility.Visible;
        HistoryLibraryView.Visibility = showHistory
            ? Visibility.Visible
            : Visibility.Collapsed;
        var activeBackground =
            new SolidColorBrush(Color.FromRgb(20, 43, 58));
        var inactiveBackground = Brushes.White;
        CurrentLibraryViewButton.Background = showHistory
            ? inactiveBackground
            : activeBackground;
        CurrentLibraryViewButton.Foreground = showHistory
            ? (Brush)FindResource("Ink")
            : Brushes.White;
        CurrentLibraryViewButton.BorderBrush = showHistory
            ? (Brush)FindResource("Line")
            : activeBackground;
        HistoryLibraryViewButton.Background = showHistory
            ? activeBackground
            : inactiveBackground;
        HistoryLibraryViewButton.Foreground = showHistory
            ? Brushes.White
            : (Brush)FindResource("Ink");
        HistoryLibraryViewButton.BorderBrush = showHistory
            ? activeBackground
            : (Brush)FindResource("Line");
    }

    private void PublishedSearchBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        var hasQuery = !string.IsNullOrWhiteSpace(PublishedSearchBox.Text);
        PublishedSearchPlaceholder.Visibility = hasQuery
            ? Visibility.Collapsed
            : Visibility.Visible;
        ClearPublishedSearchButton.Visibility = hasQuery
            ? Visibility.Visible
            : Visibility.Collapsed;
        BindPublishedLibrary();
    }

    private void ClearPublishedSearchButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        PublishedSearchBox.Clear();
        PublishedSearchBox.Focus();
    }

    private void PublishedScopeSelector_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _changingPublishedFilter)
        {
            return;
        }

        BindPublishedLibrary();
    }

    private void CollapsePublishedTreeButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        PublishedTree.UpdateLayout();
        CollapseTreeItems(PublishedTree);
    }

    private static void CollapseTreeItems(ItemsControl parent)
    {
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item)
                is not TreeViewItem container)
            {
                continue;
            }

            container.IsExpanded = false;
            CollapseTreeItems(container);
        }
    }

    private void PublishedHistoryList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        var selected =
            PublishedHistoryList.SelectedItem as PublishedArchiveRow;
        OpenPublishedArchiveButton.IsEnabled =
            !_isBusy && selected is not null;
        RestorePublishedArchiveButton.IsEnabled =
            !_isBusy && selected is { IsCurrent: false };
        PublishedHistorySelectionText.Text = selected is null
            ? "选择一个历史版本"
            : $"版本 {selected.Manifest.Version}";
    }

    private void OpenPublishedArchiveButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (PublishedHistoryList.SelectedItem is not PublishedArchiveRow
            {
                Manifest: { Version: not null }
            } selected)
        {
            return;
        }

        _viewingArchiveManifest = selected.Manifest;
        PublishedSearchBox.Clear();
        SetLibraryView(false);
        BindPublishedLibrary();
    }

    private async void RestorePublishedArchiveButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (PublishedHistoryList.SelectedItem is not PublishedArchiveRow
            {
                IsCurrent: false,
                Manifest: { Version: not null }
            } selected)
        {
            return;
        }

        var answer = MessageBox.Show(
            $"确定恢复 {selected.DateLabel} {selected.TimeLabel} 的存档吗？\n\n" +
            $"{selected.Summary}\n{selected.Notice}\n\n" +
            "恢复后会生成一个新的正式版本，仓库端下次打开会自动同步。\n" +
            "当前源 Word/Excel 和所有历史存档都不会改变。",
            "恢复历史存档",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            SetBusy(true, "正在校验并恢复历史存档…");
            var result = await _published.RestoreVersionAsync(
                selected.Manifest.Version);
            await LoadStateAsync();
            SetLibraryView(true);
            MessageBox.Show(
                $"历史存档已恢复为正式版本 {result.Version}。\n" +
                $"共 {result.FileCount} 份 PDF，仓库端下次打开会自动同步。",
                "历史版本已恢复",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.GetBaseException().Message,
                "无法恢复历史版本",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, "历史存档操作完成");
        }
    }

    private void PublishedTree_SelectedItemChanged(
        object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        PreviewPublishedButton.IsEnabled = !_isBusy &&
            PublishedTree.SelectedItem is PublishedTreeNode
            {
                File: not null
            };
        UpdatePublishedRemovalAction();
        MovePublishedGroupButton.IsEnabled =
            !_isBusy && _viewingArchiveManifest is null &&
            GetSelectedPublishedGroup() is not null;
        RefreshPublishedMoveGroupOptions();
    }

    private void RefreshPublishedMoveGroupOptions()
    {
        var selected = GetSelectedPublishedGroup();
        if (selected is null)
        {
            PublishedMoveGroupSelector.ItemsSource = Array.Empty<string>();
            PublishedMoveGroupSelector.Text = string.Empty;
            return;
        }

        var destination = GetDestinationOptions(selected.SourceRelativePath);
        var groups = _effectiveManifest.Files
            .Where(file => IsSameDestination(
                file.SourceRelativePath,
                destination.Library,
                destination.Category))
            .Select(file => GetPublishedGroupName(
                file.SourceRelativePath))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        PublishedMoveGroupSelector.ItemsSource = groups;
        PublishedMoveGroupSelector.Text =
            GetPublishedGroupName(selected.SourceRelativePath) ?? string.Empty;
    }

    private async void MovePublishedGroupButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var selected = GetSelectedPublishedGroup();
        if (selected is null)
        {
            return;
        }

        string? groupName;
        try
        {
            groupName = ValidateCustomGroupName(
                PublishedMoveGroupSelector.Text);
        }
        catch (InvalidDataException exception)
        {
            MessageBox.Show(
                exception.Message,
                "分组名称无效",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(groupName))
        {
            MessageBox.Show(
                "先选择已有分组或输入新分组。",
                "请输入分组",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var destination = GetDestinationOptions(selected.SourceRelativePath);
        var currentGroup = GetPublishedGroupName(selected.SourceRelativePath);
        if (groupName.Equals(
                currentGroup,
                StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(
                "资料已经在这个分组中。",
                "无需移动",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        string sourcePath;
        try
        {
            sourcePath = ResolvePublishedSourcePath(
                selected.SourceRelativePath,
                destination.Library);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.GetBaseException().Message,
                "找不到源文件",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var targetDirectory = Path.Combine(
            ResolveSourceCategoryDirectory(
                sourcePath,
                destination.Library),
            NormalizeGroupFolderName(
                destination.Library,
                destination.Category,
                groupName));
        var targetPath = Path.Combine(
            targetDirectory,
            Path.GetFileName(sourcePath));
        var sameSourceAndTarget = sourcePath.Equals(
            Path.GetFullPath(targetPath),
            StringComparison.OrdinalIgnoreCase);
        if (File.Exists(targetPath) && !sameSourceAndTarget)
        {
            MessageBox.Show(
                $"目标分组已经存在同名文件：{Path.GetFileName(targetPath)}",
                "无法移动资料",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var sourceRelativePath = Path.GetRelativePath(
                _options.SourcePath,
                sourcePath)
            .Replace(Path.DirectorySeparatorChar, '/');
        var targetRelativePath = Path.GetRelativePath(
                _options.SourcePath,
                targetPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (MessageBox.Show(
                $"将“{Path.GetFileName(sourcePath)}”移动到分组“{groupName}”？\n\n" +
                "源 Word/Excel/PDF 会移动到新分组，并重新生成待发布内容。\n" +
                "还需要填写更新说明并点击“立即发布到仓库”才会生效。",
                "确认移动资料",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var moved = false;
        try
        {
            SetBusy(true, "正在移动资料并生成发布内容…");
            if (!sameSourceAndTarget)
            {
                Directory.CreateDirectory(targetDirectory);
                File.Move(sourcePath, targetPath);
                moved = true;
            }
            var removals = new[]
                {
                    selected.SourceRelativePath,
                    sourceRelativePath
                }
                .Where(path => !path.Equals(
                    targetRelativePath,
                    StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var batch = await _drafts.PrepareAsync(
                new[] { targetRelativePath },
                removals);
            await LoadStateAsync(batch.BatchId);
            ShowManagementPage(PublishPage, PublishPageButton);
            MessageBox.Show(
                $"资料已移动到“{groupName}”，并生成待发布内容。\n\n" +
                "请抽查 PDF、填写更新说明，然后正式发布到仓库。",
                "分组已调整",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            if (moved && File.Exists(targetPath) && !File.Exists(sourcePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
                File.Move(targetPath, sourcePath);
            }

            await LoadStateAsync(_selectedBatch?.BatchId);
            MessageBox.Show(
                exception.GetBaseException().Message,
                "无法移动资料",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, "资料分组调整完成");
        }
    }

    private async void PublishedTree_MouseDoubleClick(
        object sender,
        MouseButtonEventArgs e) =>
        await OpenSelectedPublishedAsync();

    private async void PreviewPublishedButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await OpenSelectedPublishedAsync();

    private async Task OpenSelectedPublishedAsync()
    {
        if (PublishedTree.SelectedItem is not PublishedTreeNode
            {
                File: not null
            } node)
        {
            return;
        }

        var row = node.File;

        string? path = null;
        var manifest = _viewingArchiveManifest ?? _currentManifest;
        if (!string.IsNullOrWhiteSpace(manifest.Version) &&
            manifest.Files.Any(file =>
                file.FileId.Equals(
                    row.File.FileId,
                    StringComparison.OrdinalIgnoreCase)))
        {
            path = await _published.GetFilePathAsync(
                manifest.Version,
                row.File.FileId);
        }

        var draftOutput = _batches
            .SelectMany(batch => batch.Sources)
            .SelectMany(source => source.Outputs)
            .FirstOrDefault(output => output.OutputId.Equals(
                row.File.FileId,
                StringComparison.OrdinalIgnoreCase));
        if (path is null && draftOutput is not null)
        {
            path = Path.GetFullPath(Path.Combine(
                _options.DataRoot,
                draftOutput.RelativePdfPath.Replace(
                    '/',
                    Path.DirectorySeparatorChar)));
        }

        if (path is null)
        {
            MessageBox.Show(
                "管理端没有这份 PDF 的本地副本；仓库端当前回报中仍有该文件。",
                "无法打开 PDF",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        OpenPath(path, "正式 PDF");
    }

    private PublishedSourceGroup? GetSelectedPublishedGroup()
    {
        return PublishedTree.SelectedItem is PublishedTreeNode node
            ? node.SourceGroup
            : null;
    }

    private void DraftOutputsList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        PreviewDraftButton.IsEnabled = !_isBusy &&
            DraftOutputsList.SelectedItem is PublishItemRow
            {
                Output: not null
            };

    private async void DraftOutputCheckBox_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_selectedBatch is null ||
            sender is not CheckBox { DataContext: PublishItemRow row } ||
            row.Output is null)
        {
            return;
        }

        try
        {
            _releaseState = await _notes.SetDecisionAsync(
                _selectedBatch,
                row.Output.OutputId,
                row.IsIncluded
                    ? ReviewDecisions.Approved
                    : ReviewDecisions.Rejected,
                string.Empty);
            UpdateDraftSelectionSummary();
        }
        catch (Exception exception)
        {
            row.IsIncluded = !row.IsIncluded;
            DraftOutputsList.Items.Refresh();
            MessageBox.Show(
                exception.GetBaseException().Message,
                "无法保存子表选择",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void SelectAllDraftOutputsButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await SetAllDraftOutputsAsync(true);

    private async void ClearDraftOutputsButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await SetAllDraftOutputsAsync(false);

    private async Task SetAllDraftOutputsAsync(bool included)
    {
        if (_selectedBatch is null ||
            !_selectedBatch.Status.Equals(
                "pending-publish",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            _releaseState = await _notes.SetAllDecisionsAsync(
                _selectedBatch,
                included
                    ? ReviewDecisions.Approved
                    : ReviewDecisions.Rejected);
            foreach (var row in _publishItems.Where(item =>
                         item.Output is not null))
            {
                row.IsIncluded = included;
            }

            DraftOutputsList.Items.Refresh();
            UpdateDraftSelectionSummary();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.GetBaseException().Message,
                "无法更新子表选择",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void DraftOutputsList_MouseDoubleClick(
        object sender,
        MouseButtonEventArgs e) =>
        OpenSelectedDraft();

    private void PreviewDraftButton_Click(
        object sender,
        RoutedEventArgs e) =>
        OpenSelectedDraft();

    private void OpenSelectedDraft()
    {
        if (DraftOutputsList.SelectedItem is not PublishItemRow
            {
                Output: not null
            } row)
        {
            return;
        }

        var path = Path.GetFullPath(Path.Combine(
            _options.DataRoot,
            row.Output.RelativePdfPath.Replace(
                '/',
                Path.DirectorySeparatorChar)));
        OpenPath(path, "待发布 PDF");
    }

    private void ReleaseNoteBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (_loadingReleaseNote || _releaseState is null)
        {
            return;
        }

        _releaseState = _releaseState with
        {
            ReleaseNote = ReleaseNoteBox.Text
        };
        UpdatePublishAction();
    }

    private async void PublishButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_selectedBatch is null || _releaseState is null)
        {
            return;
        }

        var includedOutputs = _publishItems
            .Where(item => item.Output is not null && item.IsIncluded)
            .ToArray();
        var includedOutputIds = includedOutputs
            .Select(item => item.Output!.OutputId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var addedOrReplaced = _selectedBatch.Sources
            .Where(source => source.Success && source.Outputs.Any(output =>
                includedOutputIds.Contains(output.OutputId)))
            .Select(source => source.RelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var removed = _selectedBatch.RemovedSourcePaths.Count;
        var destinations = GetDestinationSummary(
            GetIncludedSourcePaths());
        var answer = MessageBox.Show(
            $"即将发布到仓库：\n" +
            $"新增或替换 {addedOrReplaced} 组，删除 {removed} 组，" +
            $"共 {includedOutputs.Length} 份新 PDF。\n\n" +
            $"资料去向：\n{destinations}\n\n" +
            "发布完成后，仓库端下次打开软件会自动核对并同步。",
            "立即发布到仓库",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            SetBusy(true, "正在生成正式版本并校验文件…");
            _releaseState = await _notes.SetReleaseNoteAsync(
                _selectedBatch,
                ReleaseNoteBox.Text);
            var result = await _published.PublishAsync(
                _selectedBatch,
                _releaseState);
            var completedSources = _selectedBatch.Sources
                .Where(source =>
                    source.Success &&
                    source.Outputs.Count > 0 &&
                    source.Outputs.All(output =>
                        includedOutputIds.Contains(output.OutputId)))
                .Select(source => source.RelativePath);
            await _baseline.AcceptPublishedAsync(
                completedSources
                    .Concat(_selectedBatch.RemovedSourcePaths)
                    .ToArray());
            await LoadStateAsync(_selectedBatch.BatchId);
            MessageBox.Show(
                $"正式版本 {result.Version} 已发布，共 {result.FileCount} 份 PDF。\n\n" +
                "仓库端下次打开软件会自动核对并下载。",
                "已发布到仓库",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.GetBaseException().Message,
                "发布失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, "发布流程完成");
        }
    }

    private async Task LoadStateAsync(string? preferredBatchId = null)
    {
        try
        {
            SetBusy(true, "正在读取仓库资料和源文件变化…");
            _ = _catalog.Scan();
            var changesTask = _baseline.GetChangesAsync();
            var batchesTask = _drafts.GetPendingAsync();
            var manifestTask = _published.GetCurrentManifestAsync();
            var historyTask = _published.GetHistoryAsync();
            var testManifestTask = _testLibrary.GetManifestAsync();
            var inventoryTask = _clientInventory.GetLatestAsync();
            await Task.WhenAll(
                changesTask,
                batchesTask,
                manifestTask,
                historyTask,
                testManifestTask,
                inventoryTask);

            var changes = await changesTask;
            _batches = await batchesTask;
            _currentManifest = await manifestTask;
            _publishedHistory = await historyTask;
            _effectiveManifest = MergeEffectiveManifest(
                _currentManifest,
                await testManifestTask);
            _reportedInventory = await inventoryTask;
            RefreshCustomGroupOptions();

            BindPublishedLibrary();
            BindPublishedHistory();
            var publishedSourcePaths = _effectiveManifest.Files
                .Select(file => file.SourceRelativePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _changeRows = changes.Changes
                .Select(change => new SourceChangeRow(
                    change,
                    publishedSourcePaths.Contains(change.RelativePath)))
                .ToArray();
            ChangesList.ItemsSource = _changeRows;
            NoChangesText.Visibility = _changeRows.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            ChangeSummaryText.Text =
                $"新增 {_changeRows.Count(row => row.Kind == "added")} · " +
                $"修改 {_changeRows.Count(row => row.Kind == "modified")} · " +
                $"删除 {_changeRows.Count(row => row.Kind == "removed")}";

            var options = _batches
                .Where(batch =>
                    !batch.Status.StartsWith(
                        "test-",
                        StringComparison.OrdinalIgnoreCase))
                .Select(batch => new BatchOption(batch))
                .ToArray();
            _changingBatch = true;
            BatchSelector.ItemsSource = options;
            var selected = options.FirstOrDefault(option =>
                               option.Batch.BatchId.Equals(
                                   preferredBatchId,
                                   StringComparison.OrdinalIgnoreCase))
                           ?? options.FirstOrDefault(option =>
                               option.Batch.Status.Equals(
                                   "pending-publish",
                                   StringComparison.OrdinalIgnoreCase))
                           ?? options.FirstOrDefault();
            BatchSelector.SelectedItem = selected;
            _changingBatch = false;

            if (selected is null)
            {
                ClearBatch();
            }
            else
            {
                await SelectBatchAsync(selected.Batch);
            }

            UpdatePrepareAction();
            FooterStatusText.Text =
                $"仓库正式资料 {_currentManifest.Files.Count} 份 · 待处理变化 {_changeRows.Count} 项";
        }
        catch (Exception exception)
        {
            FooterStatusText.Text = exception.GetBaseException().Message;
            MessageBox.Show(
                exception.GetBaseException().Message,
                "无法读取管理数据",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, FooterStatusText.Text);
        }
    }

    private void BindPublishedLibrary()
    {
        var displayedManifest =
            _viewingArchiveManifest ?? _effectiveManifest;
        var allRows = displayedManifest.Files
            .OrderBy(file => file.LibraryName, StringComparer.Ordinal)
            .ThenBy(file => file.SourceRelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.WorksheetIndex)
            .Select(file => new PublishedFileRow(file))
            .ToArray();
        if (_viewingArchiveManifest is null)
        {
            _selectedPublishedRemovalPaths.IntersectWith(
                allRows.Select(row => row.File.SourceRelativePath));
        }
        RefreshPublishedScopeOptions(allRows);

        var query = PublishedSearchBox.Text.Trim();
        var selectedScope =
            (PublishedScopeSelector.SelectedItem as PublishedScopeOption)
            ?.RootLabel;
        var rows = allRows
            .Where(row =>
                selectedScope is null ||
                GetPublishedRootLabel(row.File.SourceRelativePath).Equals(
                    selectedScope,
                    StringComparison.OrdinalIgnoreCase))
            .Where(row => MatchesPublishedSearch(row, query))
            .ToArray();
        var isFiltering =
            !string.IsNullOrWhiteSpace(query) || selectedScope is not null;
        var sourceGroups = rows
            .GroupBy(
                row => row.File.SourceRelativePath,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new PublishedSourceGroup(
                group.Key,
                group.ToArray(),
                false,
                _viewingArchiveManifest is null &&
                _selectedPublishedRemovalPaths.Contains(group.Key)))
            .ToArray();
        var rootNodes = sourceGroups
            .GroupBy(
                group => GetPublishedRootLabel(group.SourceRelativePath),
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => PublishedTreeNode.ForRoot(
                group.Key,
                BuildPublishedRootChildren(group.ToArray(), isFiltering),
                isFiltering))
            .ToArray();
        PublishedTree.ItemsSource = rootNodes;
        EmptyPublishedText.Visibility = rows.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        var allGroupCount = allRows
            .Select(row => row.File.SourceRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        PublishedCountText.Text = rows.Length == allRows.Length
            ? $"{allGroupCount} 组 · {allRows.Length} PDF"
            : $"{sourceGroups.Length}/{allGroupCount} 组 · " +
              $"{rows.Length}/{allRows.Length} PDF";
        PreviewPublishedButton.IsEnabled = false;
        UpdatePublishedRemovalAction();
        MovePublishedGroupButton.IsEnabled = false;
        if (_viewingArchiveManifest is not null)
        {
            var local = _viewingArchiveManifest.GeneratedAtUtc.ToLocalTime();
            PublishedVersionText.Text =
                $"历史存档 · {local:yyyy-MM-dd HH:mm}";
            PublishedSummaryText.Text =
                _viewingArchiveManifest.Notice;
            return;
        }
        if (_reportedInventory is null)
        {
            PublishedVersionText.Text = "尚未收到仓库端回报";
            PublishedSummaryText.Text = allRows.Length == 0
                ? "仓库端升级并连接后，会自动回报本地资料清单。当前正式库尚为空。"
                : $"管理端当前会向仓库提供 {allRows.Length} 份 PDF；等待仓库端回报。";
            return;
        }

        var reportedAt = _reportedInventory.ReportedAtUtc.ToLocalTime();
        var localManifest = _reportedInventory.LocalManifest;
        var localFiles = localManifest?.Files ?? Array.Empty<LibrarySyncFile>();
        var localGroups = localFiles
            .Select(file => file.SourceRelativePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var isCurrent = localManifest is not null &&
                        localManifest.Version == displayedManifest.Version &&
                        localFiles.Count == displayedManifest.Files.Count &&
                        displayedManifest.Files.All(file => localFiles.Any(local =>
                            local.FileId.Equals(
                                file.FileId,
                                StringComparison.OrdinalIgnoreCase) &&
                            local.Sha256.Equals(
                                file.Sha256,
                                StringComparison.OrdinalIgnoreCase)));
        if (isCurrent)
        {
            PublishedVersionText.Text = "仓库端已同步 · 正式资料";
            PublishedSummaryText.Text =
                $"仓库端于 {reportedAt:MM-dd HH:mm} 回报，实际有 " +
                $"{localGroups} 组、{localFiles.Count} 份 PDF。";
            return;
        }

        PublishedVersionText.Text = "待仓库同步";
        PublishedSummaryText.Text =
            $"管理端已发布 {allGroupCount} 组、{allRows.Length} 份 PDF；" +
            $"仓库端 {reportedAt:MM-dd HH:mm} 回报 " +
            $"{localGroups} 组、{localFiles.Count} 份。" +
            "仓库端下次打开软件会自动核对并同步。";
    }

    private void RefreshPublishedScopeOptions(
        IReadOnlyList<PublishedFileRow> rows)
    {
        var previous = (PublishedScopeSelector.SelectedItem
            as PublishedScopeOption)?.RootLabel;
        var options = new[]
            {
                new PublishedScopeOption("全部资料", null)
            }
            .Concat(rows
                .Select(row => GetPublishedRootLabel(
                    row.File.SourceRelativePath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.Ordinal)
                .Select(name => new PublishedScopeOption(name, name)))
            .ToArray();
        _changingPublishedFilter = true;
        PublishedScopeSelector.ItemsSource = options;
        PublishedScopeSelector.SelectedItem = options.FirstOrDefault(option =>
            option.RootLabel?.Equals(
                previous,
                StringComparison.OrdinalIgnoreCase) == true) ?? options[0];
        _changingPublishedFilter = false;
    }

    private static bool MatchesPublishedSearch(
        PublishedFileRow row,
        string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return row.DisplayName.Contains(
                   query,
                   StringComparison.OrdinalIgnoreCase) ||
               row.File.DisplayName.Contains(
                   query,
                   StringComparison.OrdinalIgnoreCase) ||
               row.File.SourceRelativePath.Contains(
                   query,
                   StringComparison.OrdinalIgnoreCase) ||
               row.File.WorksheetName?.Contains(
                   query,
                   StringComparison.OrdinalIgnoreCase) == true ||
               GetDestinationLabel(row.File.SourceRelativePath).Contains(
                   query,
                   StringComparison.OrdinalIgnoreCase);
    }

    private void BindPublishedHistory()
    {
        var rows = _publishedHistory
            .Select(manifest => new PublishedArchiveRow(
                manifest,
                manifest.Version?.Equals(
                    _currentManifest.Version,
                    StringComparison.OrdinalIgnoreCase) == true))
            .ToArray();
        PublishedHistoryList.ItemsSource = rows;
        PublishedHistoryCountText.Text = $"{rows.Length} 个版本";
        PublishedHistorySelectionText.Text = rows.Length == 0
            ? "还没有正式发布存档"
            : "选择一个历史版本";
        OpenPublishedArchiveButton.IsEnabled = false;
        RestorePublishedArchiveButton.IsEnabled = false;
    }

    private async Task SelectBatchAsync(DraftBatch batch)
    {
        _selectedBatch = batch;
        _releaseState = await _notes.GetAsync(batch);
        var excludedOutputIds = _releaseState.Items
            .Where(item => item.Decision == ReviewDecisions.Rejected)
            .Select(item => item.OutputId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var canChoose = batch.Status.Equals(
            "pending-publish",
            StringComparison.OrdinalIgnoreCase);
        _publishItems = batch.Sources
            .Where(source => source.Success)
            .SelectMany(source => source.Outputs.Select(output =>
                PublishItemRow.ForPdf(
                    source,
                    output,
                    !excludedOutputIds.Contains(output.OutputId),
                    canChoose)))
            .Concat(batch.RemovedSourcePaths.Select(
                PublishItemRow.ForRemoval))
            .ToArray();
        DraftOutputsList.ItemsSource = _publishItems;
        EmptyDraftText.Visibility = _publishItems.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateDraftSelectionSummary();

        _loadingReleaseNote = true;
        ReleaseNoteBox.Text = _releaseState.ReleaseNote;
        _loadingReleaseNote = false;
        UpdatePublishAction();
    }

    private void ClearBatch()
    {
        _selectedBatch = null;
        _releaseState = null;
        _publishItems = Array.Empty<PublishItemRow>();
        DraftOutputsList.ItemsSource = _publishItems;
        EmptyDraftText.Visibility = Visibility.Visible;
        DraftSummaryText.Text = "选择变化并生成 PDF 后，会在这里看到最终结果。";
        PublishDestinationsText.Text = "资料去向：等待选择发布内容";
        _loadingReleaseNote = true;
        ReleaseNoteBox.Text = string.Empty;
        _loadingReleaseNote = false;
        UpdatePublishAction();
    }

    private void UpdatePrepareAction()
    {
        var selected = _changeRows.Count(row =>
            row.CanSelect && row.IsSelected);
        PrepareHintText.Text = selected == 0
            ? string.Empty
            : $"已选择 {selected} 组资料";
        PrepareHintText.Visibility = selected == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        PrepareDraftButton.IsEnabled = !_isBusy && selected > 0;
    }

    private void UpdatePublishAction()
    {
        string blocker;
        var enabled = false;
        if (_selectedBatch is null)
        {
            blocker = "先从上方选择变化并生成本次发布内容。";
        }
        else if (_selectedBatch.Status.Equals(
                     "test-only-do-not-publish",
                     StringComparison.OrdinalIgnoreCase))
        {
            blocker = "这是旧测试批次，不能正式发布。请从源资料重新生成。";
        }
        else if (_selectedBatch.Status.Equals(
                     "published",
                     StringComparison.OrdinalIgnoreCase))
        {
            blocker = "该批次已经发布。请选择新的源资料变化。";
        }
        else if (_selectedBatch.FailedSourceCount > 0)
        {
            blocker = $"有 {_selectedBatch.FailedSourceCount} 组转换失败，请重新生成。";
        }
        else if (_publishItems.Count(item =>
                     item.Output is not null && item.IsIncluded) == 0 &&
                 _selectedBatch.RemovedSourcePaths.Count == 0)
        {
            blocker = "至少勾选一份要发布的 PDF。";
        }
        else if (string.IsNullOrWhiteSpace(ReleaseNoteBox.Text))
        {
            blocker = "填写本次更新说明后即可发布。";
        }
        else
        {
            blocker = "准备完成。点击后会生成正式版本，仓库端检查更新即可收到。";
            enabled = true;
        }

        PublishBlockerText.Text = blocker;
        PublishBlockerText.Foreground = new SolidColorBrush(
            enabled
                ? Color.FromRgb(25, 120, 90)
                : Color.FromRgb(215, 101, 50));
        PublishButton.IsEnabled = !_isBusy && enabled;
        ReleaseNoteBox.IsEnabled = !_isBusy &&
            _selectedBatch is not null &&
            !_selectedBatch.Status.Equals(
                "published",
                StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateDraftSelectionSummary()
    {
        if (_selectedBatch is null)
        {
            return;
        }

        var included = _publishItems.Count(item =>
            item.Output is not null && item.IsIncluded);
        DraftSummaryText.Text =
            $"已选 {included}/{_selectedBatch.PdfCount} 份 PDF · " +
            $"{_selectedBatch.RemovedSourcePaths.Count} 组删除";
        var includedPaths = GetIncludedSourcePaths();
        PublishDestinationsText.Text = includedPaths.Count == 0
            ? "资料去向：等待勾选发布内容"
            : "资料去向：" + GetDestinationSummary(includedPaths, "；");
        UpdatePublishAction();
    }

    private IReadOnlyList<string> GetIncludedSourcePaths()
    {
        if (_selectedBatch is null)
        {
            return Array.Empty<string>();
        }

        var includedOutputIds = _publishItems
            .Where(item => item.Output is not null && item.IsIncluded)
            .Select(item => item.Output!.OutputId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _selectedBatch.Sources
            .Where(source => source.Success && source.Outputs.Any(output =>
                includedOutputIds.Contains(output.OutputId)))
            .Select(source => source.RelativePath)
            .Concat(_selectedBatch.RemovedSourcePaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void SetBusy(bool busy, string status)
    {
        _isBusy = busy;
        RefreshButton.IsEnabled = !busy;
        ChooseSourceFilesButton.IsEnabled = !busy;
        ImportLibrarySelector.IsEnabled = !busy;
        ImportCategorySelector.IsEnabled = !busy;
        ImportWorkshopGroupSelector.IsEnabled = !busy;
        ImportCustomGroupSelector.IsEnabled = !busy;
        MoveSelectedToGroupButton.IsEnabled = !busy;
        MoveSelectedToWorkshopGroupButton.IsEnabled = !busy;
        SelectAllChangesButton.IsEnabled = !busy;
        ClearPendingChangesButton.IsEnabled = !busy &&
            _changeRows.Count > 0;
        BatchSelector.IsEnabled = !busy;
        ChangesList.IsEnabled = !busy;
        DraftOutputsList.IsEnabled = !busy;
        var canChooseDraftOutputs = !busy &&
            _selectedBatch?.Status.Equals(
                "pending-publish",
                StringComparison.OrdinalIgnoreCase) == true;
        SelectAllDraftOutputsButton.IsEnabled = canChooseDraftOutputs;
        ClearDraftOutputsButton.IsEnabled = canChooseDraftOutputs;
        PublishedTree.IsEnabled = !busy;
        PublishedSearchBox.IsEnabled = !busy;
        PublishedScopeSelector.IsEnabled = !busy;
        PublishedHistoryList.IsEnabled = !busy;
        CurrentLibraryViewButton.IsEnabled = !busy;
        HistoryLibraryViewButton.IsEnabled = !busy;
        LibraryPageButton.IsEnabled = !busy;
        ChangesPageButton.IsEnabled = !busy;
        PublishPageButton.IsEnabled = !busy;
        FooterStatusText.Text = status;
        UpdatePrepareAction();
        UpdatePublishAction();
        PreviewPublishedButton.IsEnabled = !busy &&
            PublishedTree.SelectedItem is PublishedTreeNode
            {
                File: not null
            };
        UpdatePublishedRemovalAction();
        MovePublishedGroupButton.IsEnabled =
            !busy && _viewingArchiveManifest is null &&
            GetSelectedPublishedGroup() is not null;
        var selectedArchive =
            PublishedHistoryList.SelectedItem as PublishedArchiveRow;
        OpenPublishedArchiveButton.IsEnabled =
            !busy && selectedArchive is not null;
        RestorePublishedArchiveButton.IsEnabled =
            !busy && selectedArchive is { IsCurrent: false };
        DraftOutputsList_SelectionChanged(this, null!);
    }

    private void AnimatePageIn()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            PageRoot.Opacity = 1;
            PageTranslate.Y = 0;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PageRoot.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = ease
            });
        PageTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = ease
            });
    }

    private static void OpenPath(string path, string title)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            MessageBox.Show(
                $"找不到{title}：\n{path}",
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private static LibrarySyncManifest EmptyManifest() =>
        new(
            1,
            null,
            false,
            DateTimeOffset.UtcNow,
            "当前还没有正式发布的资料版本。",
            Array.Empty<LibrarySyncFile>());

    private static string GetDestinationSummary(
        IEnumerable<string> relativePaths,
        string separator = "\n") =>
        string.Join(
            separator,
            relativePaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .GroupBy(GetDestinationLabel)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Key}（{group.Count()} 组）"));

    private static IReadOnlyList<PublishedTreeNode> BuildPublishedRootChildren(
        IReadOnlyList<PublishedSourceGroup> sourceGroups,
        bool isExpanded)
    {
        var ordered = sourceGroups
            .OrderBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ordered.Length == 0)
        {
            return BuildPublishedSourceNodes(ordered, isExpanded);
        }

        var isCccWorkshop = IsCccWorkshopPath(
            ordered[0].SourceRelativePath);
        var hasSubgroups = isCccWorkshop || ordered.Any(group =>
            GetCustomGroupName(group.SourceRelativePath) is not null);
        if (!hasSubgroups)
        {
            return BuildPublishedSourceNodes(ordered, isExpanded);
        }

        return ordered
            .GroupBy(
                group => isCccWorkshop
                    ? GetWorkshopDestinationLabel(
                          group.SourceRelativePath) ??
                      GetCustomGroupName(group.SourceRelativePath) ??
                      "未分组"
                    : GetCustomGroupName(group.SourceRelativePath) ??
                      "未分组",
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => PublishedTreeNode.ForCategory(
                group.Key,
                BuildPublishedSourceNodes(group.ToArray(), isExpanded),
                isExpanded))
            .ToArray();
    }

    private static IReadOnlyList<PublishedTreeNode> BuildPublishedSourceNodes(
        IReadOnlyList<PublishedSourceGroup> sourceGroups,
        bool isExpanded) =>
        sourceGroups
            .Select(group => group.Files.Count == 1
                ? PublishedTreeNode.ForFile(
                    group.Files[0],
                    group,
                    group.DisplayName,
                    true)
                : PublishedTreeNode.ForSource(
                    group,
                    group.Files
                        .Select(file => PublishedTreeNode.ForFile(file, group))
                        .ToArray(),
                    isExpanded))
            .ToArray();

    private static string GetPublishedRootLabel(string relativePath)
    {
        var segments = relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        var library = segments.FirstOrDefault() switch
        {
            "CCC燃气资料" => "CCC",
            "电加热资料" => "电加热",
            "政府资料" => "政府",
            _ => "其他"
        };
        var destination = GetDestinationLabel(relativePath)
            .Split(" → ", StringSplitOptions.RemoveEmptyEntries);
        var category = destination.Length > 1 ? destination[1] : "资料";
        return $"{library}{category}";
    }

    private static bool IsCccWorkshopPath(string relativePath)
    {
        var segments = relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.FirstOrDefault() == "CCC燃气资料" &&
               segments.Length > 1 &&
               segments[1].Contains("车间", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetCustomGroupName(string relativePath)
    {
        var segments = relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 3
            ? segments[2]
            : null;
    }

    private static string GetDestinationLabel(string relativePath)
    {
        var segments = relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        var library = segments.FirstOrDefault() switch
        {
            "CCC燃气资料" => "CCC 燃气资料库",
            "电加热资料" => "电加热资料库",
            "政府资料" => "政府资料库",
            _ => "其他资料库"
        };
        var folder = segments.Length > 1 ? segments[1] : string.Empty;
        var category = segments.FirstOrDefault() == "政府资料"
            ? folder switch
            {
                "应急管理" => "应急管理",
                "安全监管" => "安全监管",
                "政策法规" => "政策法规",
                _ => "其他资料"
            }
            : folder.Contains("仓库", StringComparison.OrdinalIgnoreCase)
                ? "仓库"
                : folder.Contains("车间", StringComparison.OrdinalIgnoreCase) ||
                  folder.Contains("生产", StringComparison.OrdinalIgnoreCase)
                    ? "车间"
                    : folder.Contains("财务", StringComparison.OrdinalIgnoreCase)
                        ? "财务"
                        : "普通资料";
        var subgroup = category == "车间" &&
                       segments.FirstOrDefault() == "CCC燃气资料"
            ? GetWorkshopDestinationLabel(relativePath) ??
              GetCustomGroupName(relativePath)
            : GetCustomGroupName(relativePath);
        return subgroup is null
            ? $"{library} → {category}"
            : $"{library} → {category} → {subgroup}";
    }

    private string ResolveCategoryDirectory(
        DestinationOption library,
        DestinationOption category)
    {
        var libraryDirectory = Path.Combine(
            _options.SourcePath,
            library.FolderName);
        var exact = Path.Combine(
            libraryDirectory,
            category.FolderName);
        if (Directory.Exists(exact))
        {
            return exact;
        }

        var matches = Directory.Exists(libraryDirectory)
            ? Directory.EnumerateDirectories(libraryDirectory)
                .Where(path => CategoryMatchesFolder(
                    category.FolderName,
                    Path.GetFileName(path)))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();
        return matches.FirstOrDefault() ?? exact;
    }

    private string ResolvePublishedSourcePath(
        string manifestRelativePath,
        DestinationOption library)
    {
        var exact = ResolveSourcePath(manifestRelativePath);
        if (File.Exists(exact))
        {
            return exact;
        }

        var libraryDirectory = Path.Combine(
            _options.SourcePath,
            library.FolderName);
        var fileName = Path.GetFileName(manifestRelativePath);
        var matches = Directory.Exists(libraryDirectory)
            ? Directory.EnumerateFiles(
                    libraryDirectory,
                    fileName,
                    System.IO.SearchOption.AllDirectories)
                .Where(path => !Path.GetFileName(path).StartsWith(
                    "~$",
                    StringComparison.Ordinal))
                .ToArray()
            : Array.Empty<string>();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new FileNotFoundException(
                $"找不到正式资料对应的源文件：{fileName}"),
            _ => throw new InvalidOperationException(
                $"源目录中存在多个同名文件“{fileName}”，请先在资源管理器中确认后再移动。")
        };
    }

    private string ResolveSourceCategoryDirectory(
        string sourcePath,
        DestinationOption library)
    {
        var libraryDirectory = Path.GetFullPath(Path.Combine(
            _options.SourcePath,
            library.FolderName));
        var relative = Path.GetRelativePath(libraryDirectory, sourcePath);
        var segments = relative.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 ||
            segments[0] == "..")
        {
            throw new InvalidDataException(
                "源文件不在所选资料库的部门目录中。");
        }

        return Path.Combine(libraryDirectory, segments[0]);
    }

    private static (DestinationOption Library, DestinationOption Category)
        GetDestinationOptions(string relativePath)
    {
        var segments = relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        var library = LibraryDestinations.FirstOrDefault(option =>
                          option.FolderName.Equals(
                              segments.FirstOrDefault(),
                              StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidDataException(
                          "无法识别资料所属的资料库。");
        var department = segments.Length > 1 ? segments[1] : string.Empty;
        var categories = GetCategoryDestinations(library);
        var categoryName = library.FolderName == "政府资料"
            ? categories.Any(option => option.FolderName.Equals(
                department,
                StringComparison.OrdinalIgnoreCase))
                ? department
                : "其他资料"
            : department.Contains(
                "仓库",
                StringComparison.OrdinalIgnoreCase)
                ? "仓库"
                : department.Contains(
                          "车间",
                          StringComparison.OrdinalIgnoreCase) ||
                      department.Contains(
                          "生产",
                          StringComparison.OrdinalIgnoreCase)
                    ? "车间"
                    : department.Contains(
                        "财务",
                        StringComparison.OrdinalIgnoreCase)
                        ? "财务"
                        : "普通资料";
        var category = categories.First(option =>
            option.FolderName == categoryName);
        return (library, category);
    }

    private static bool IsSameDestination(
        string relativePath,
        DestinationOption library,
        DestinationOption category)
    {
        try
        {
            var destination = GetDestinationOptions(relativePath);
            return destination.Library.FolderName == library.FolderName &&
                   destination.Category.FolderName == category.FolderName;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static string? GetPublishedGroupName(string relativePath)
    {
        var custom = GetCustomGroupName(relativePath);
        if (!string.IsNullOrWhiteSpace(custom))
        {
            return GetGroupDisplayName(custom);
        }

        return IsCccWorkshopPath(relativePath)
            ? GetWorkshopDestinationLabel(relativePath)
            : null;
    }

    private static string GetGroupDisplayName(string folderName) =>
        folderName switch
        {
            "全预混资料" => "全预混",
            "自吸风资料" => "自吸风",
            "燃烧机资料" => "燃烧机",
            "炉头作业指导书" => "炉头指导书",
            _ => folderName
        };

    private static string NormalizeGroupFolderName(
        DestinationOption library,
        DestinationOption category,
        string groupName)
    {
        if (library.FolderName != "CCC燃气资料" ||
            category.FolderName != "车间")
        {
            return groupName;
        }

        return WorkshopDestinations.FirstOrDefault(option =>
                   option.Name.Equals(
                       groupName,
                       StringComparison.OrdinalIgnoreCase))
                   ?.FolderName
               ?? groupName;
    }

    internal static string[] BuildCustomGroupOptions(
        IEnumerable<string> sourceGroups,
        LibrarySyncManifest manifest,
        string libraryFolderName,
        string categoryFolderName,
        IEnumerable<string>? additionalGroups = null)
    {
        var library = new DestinationOption(
            libraryFolderName,
            libraryFolderName);
        var category = new DestinationOption(
            categoryFolderName,
            categoryFolderName);
        var publishedGroups = manifest.Files
            .Where(file => IsSameDestination(
                file.SourceRelativePath,
                library,
                category))
            .Select(file => GetPublishedGroupName(
                file.SourceRelativePath))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>();
        return sourceGroups
            .Concat(publishedGroups)
            .Concat(additionalGroups ?? Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DestinationOption[] GetCategoryDestinations(
        DestinationOption library) =>
        library.FolderName == "政府资料"
            ? GovernmentCategoryDestinations
            : CategoryDestinations;

    private static bool CategoryMatchesFolder(
        string categoryName,
        string folderName) =>
        categoryName switch
        {
            "仓库" => folderName.Contains(
                "仓库",
                StringComparison.OrdinalIgnoreCase),
            "车间" => folderName.Contains(
                          "车间",
                          StringComparison.OrdinalIgnoreCase) ||
                      folderName.Contains(
                          "生产",
                          StringComparison.OrdinalIgnoreCase),
            "财务" => folderName.Contains(
                "财务",
                StringComparison.OrdinalIgnoreCase),
            _ => folderName.Equals(
                categoryName,
                StringComparison.OrdinalIgnoreCase)
        };

    private static string? ValidateCustomGroupName(string? value)
    {
        var name = value?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (name is "." or ".." ||
            name.Length > 40 ||
            name.EndsWith('.') ||
            name.EndsWith(' ') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException(
                "分组名称不能包含路径符号或特殊字符，且不能超过 40 个字。");
        }

        return name;
    }

    private static string? GetWorkshopDestinationLabel(string relativePath)
    {
        if (relativePath.Contains("炉头工艺卡", StringComparison.OrdinalIgnoreCase) ||
            relativePath.Contains("炉头", StringComparison.OrdinalIgnoreCase) &&
            relativePath.Contains("工艺卡", StringComparison.OrdinalIgnoreCase))
        {
            return "炉头工艺卡";
        }

        if (relativePath.Contains("炉头作业指导书", StringComparison.OrdinalIgnoreCase) ||
            relativePath.Contains("炉头", StringComparison.OrdinalIgnoreCase) &&
            relativePath.Contains("指导书", StringComparison.OrdinalIgnoreCase))
        {
            return "炉头指导书";
        }

        if (relativePath.Contains("全预混", StringComparison.OrdinalIgnoreCase))
        {
            return "全预混";
        }

        if (relativePath.Contains("自吸风", StringComparison.OrdinalIgnoreCase))
        {
            return "自吸风";
        }

        return relativePath.Contains("燃烧机", StringComparison.OrdinalIgnoreCase)
            ? "燃烧机"
            : null;
    }

    private static LibrarySyncManifest MergeEffectiveManifest(
        LibrarySyncManifest published,
        LibrarySyncManifest test)
    {
        if (string.IsNullOrWhiteSpace(published.Version))
        {
            return string.IsNullOrWhiteSpace(test.Version)
                ? published
                : test;
        }

        if (string.IsNullOrWhiteSpace(test.Version))
        {
            return published;
        }

        var coveredSources = published.Files
            .Select(file => file.SourceRelativePath)
            .Concat(published.RemovedSourcePaths)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remainingTestFiles = test.Files
            .Where(file => !coveredSources.Contains(
                file.SourceRelativePath))
            .ToArray();
        return new LibrarySyncManifest(
            Math.Max(published.SchemaVersion, test.SchemaVersion),
            remainingTestFiles.Length == 0
                ? published.Version
                : $"{published.Version}__{test.Version}",
            remainingTestFiles.Length > 0,
            published.GeneratedAtUtc > test.GeneratedAtUtc
                ? published.GeneratedAtUtc
                : test.GeneratedAtUtc,
            published.Notice,
            published.Files.Concat(remainingTestFiles).ToArray())
        {
            RemovedSourcePaths = published.RemovedSourcePaths
        };
    }

    private sealed class SourceChangeRow : INotifyPropertyChanged
    {
        private bool _isSelected;

        public SourceChangeRow(
            SourceFileChange change,
            bool isPublished)
        {
            Change = change;
            Kind = change.Kind == "added" && isPublished
                ? "modified"
                : change.Kind;
            FileName = Path.GetFileName(change.RelativePath);
            CanSelect = Kind == "removed" ||
                        SupportedExtensions.Contains(change.Extension);
            ActionLabel = Kind == "added" ? "移除" : "忽略";
            ActionForeground = new SolidColorBrush(Kind == "added"
                ? Color.FromRgb(174, 73, 54)
                : Color.FromRgb(101, 119, 129));
            DestinationLabel =
                $"发布到：{GetDestinationLabel(change.RelativePath)}";
            KindLabel = Kind switch
            {
                "added" => "新增",
                "modified" => "替换",
                "removed" => "删除",
                _ => "变化"
            };
            BadgeBackground = new SolidColorBrush(Kind switch
            {
                "added" => Color.FromRgb(225, 241, 234),
                "modified" => Color.FromRgb(232, 240, 247),
                "removed" => Color.FromRgb(251, 234, 229),
                _ => Color.FromRgb(238, 242, 241)
            });
            BadgeForeground = new SolidColorBrush(Kind switch
            {
                "added" => Color.FromRgb(25, 120, 90),
                "modified" => Color.FromRgb(45, 114, 164),
                "removed" => Color.FromRgb(174, 73, 54),
                _ => Color.FromRgb(101, 119, 129)
            });
        }

        public SourceFileChange Change { get; }

        public string Kind { get; }

        public string FileName { get; }

        public string RelativePath => Change.RelativePath;

        public string DestinationLabel { get; }

        public bool CanSelect { get; }

        public string ActionLabel { get; }

        public Brush ActionForeground { get; }

        public string KindLabel { get; }

        public Brush BadgeBackground { get; }

        public Brush BadgeForeground { get; }

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

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(
            [CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(propertyName));
    }

    private sealed class PublishedSourceGroup
    {
        public PublishedSourceGroup(
            string sourceRelativePath,
            IReadOnlyList<PublishedFileRow> files,
            bool isExpanded,
            bool isRemovalSelected)
        {
            SourceRelativePath = sourceRelativePath;
            Files = files;
            IsExpanded = isExpanded;
            IsRemovalSelected = isRemovalSelected;
            DisplayName = Path.GetFileNameWithoutExtension(
                sourceRelativePath);
            Detail = Path.GetExtension(sourceRelativePath)
                .ToLowerInvariant() switch
            {
                ".xls" or ".xlsx" => "Excel 工作簿",
                ".doc" or ".docx" => "Word 文档",
                ".pdf" => "PDF 文件",
                _ => "资料文件"
            };
            CountLabel = $"{files.Count} 张";
        }

        public string SourceRelativePath { get; }

        public string DisplayName { get; }

        public string Detail { get; }

        public string CountLabel { get; }

        public IReadOnlyList<PublishedFileRow> Files { get; }

        public bool IsExpanded { get; }

        public bool IsRemovalSelected { get; }
    }

    private sealed class PublishedTreeNode
    {
        private PublishedTreeNode(
            string displayName,
            string detail,
            string metaLabel,
            string iconLabel,
            Brush iconBackground,
            Brush iconForeground,
            double iconSize,
            double iconFontSize,
            FontWeight fontWeight,
            IReadOnlyList<PublishedTreeNode> children,
            bool isExpanded,
            PublishedSourceGroup? sourceGroup = null,
            PublishedFileRow? file = null,
            bool canSelectForRemoval = false)
        {
            DisplayName = displayName;
            Detail = detail;
            DetailVisibility = string.IsNullOrWhiteSpace(detail)
                ? Visibility.Collapsed
                : Visibility.Visible;
            MetaLabel = metaLabel;
            IconLabel = iconLabel;
            IconBackground = iconBackground;
            IconForeground = iconForeground;
            IconSize = iconSize;
            IconFontSize = iconFontSize;
            FontWeight = fontWeight;
            Children = children;
            IsExpanded = isExpanded;
            SourceGroup = sourceGroup;
            File = file;
            CanSelectForRemoval = canSelectForRemoval;
            IsRemovalSelected =
                canSelectForRemoval && sourceGroup?.IsRemovalSelected == true;
        }

        public string DisplayName { get; }

        public string Detail { get; }

        public Visibility DetailVisibility { get; }

        public string MetaLabel { get; }

        public string IconLabel { get; }

        public Brush IconBackground { get; }

        public Brush IconForeground { get; }

        public Brush MetaForeground => File is null
            ? new SolidColorBrush(Color.FromRgb(25, 120, 90))
            : new SolidColorBrush(Color.FromRgb(101, 119, 129));

        public double IconSize { get; }

        public double IconFontSize { get; }

        public FontWeight FontWeight { get; }

        public Thickness IndentMargin => File is null
            ? new Thickness(0)
            : new Thickness(3, 1, 0, 1);

        public IReadOnlyList<PublishedTreeNode> Children { get; }

        public bool IsExpanded { get; }

        public PublishedSourceGroup? SourceGroup { get; }

        public PublishedFileRow? File { get; }

        public bool CanSelectForRemoval { get; }

        public bool IsRemovalSelected { get; set; }

        public Visibility RemovalSelectionVisibility =>
            CanSelectForRemoval ? Visibility.Visible : Visibility.Collapsed;

        public static PublishedTreeNode ForRoot(
            string name,
            IReadOnlyList<PublishedTreeNode> children,
            bool isExpanded) =>
            ForBranch(name, children, isExpanded, "库", 27);

        public static PublishedTreeNode ForCategory(
            string name,
            IReadOnlyList<PublishedTreeNode> children,
            bool isExpanded) =>
            ForBranch(name, children, isExpanded, "类", 27);

        public static PublishedTreeNode ForSource(
            PublishedSourceGroup sourceGroup,
            IReadOnlyList<PublishedTreeNode> children,
            bool isExpanded) =>
            new(
                sourceGroup.DisplayName,
                sourceGroup.Detail,
                sourceGroup.CountLabel,
                "表",
                new SolidColorBrush(Color.FromRgb(227, 240, 235)),
                new SolidColorBrush(Color.FromRgb(25, 120, 90)),
                28,
                9,
                FontWeights.SemiBold,
                children,
                isExpanded,
                sourceGroup,
                canSelectForRemoval: true);

        public static PublishedTreeNode ForFile(
            PublishedFileRow file,
            PublishedSourceGroup sourceGroup,
            string? displayName = null,
            bool canSelectForRemoval = false) =>
            new(
                displayName ?? file.DisplayName,
                string.Empty,
                file.PageLabel,
                "PDF",
                new SolidColorBrush(Color.FromRgb(232, 240, 245)),
                new SolidColorBrush(Color.FromRgb(45, 114, 164)),
                26,
                7.5,
                FontWeights.Normal,
                Array.Empty<PublishedTreeNode>(),
                false,
                sourceGroup,
                file,
                canSelectForRemoval);

        private static PublishedTreeNode ForBranch(
            string name,
            IReadOnlyList<PublishedTreeNode> children,
            bool isExpanded,
            string iconLabel,
            double iconSize)
        {
            var sources = children
                .SelectMany(GetSourceGroups)
                .DistinctBy(
                    group => group.SourceRelativePath,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new PublishedTreeNode(
                name,
                string.Empty,
                $"{sources.Length} 组 · {sources.Sum(group => group.Files.Count)} 张",
                iconLabel,
                new SolidColorBrush(Color.FromRgb(220, 235, 231)),
                new SolidColorBrush(Color.FromRgb(25, 120, 90)),
                iconSize,
                9,
                FontWeights.SemiBold,
                children,
                isExpanded);
        }

        private static IEnumerable<PublishedSourceGroup> GetSourceGroups(
            PublishedTreeNode node)
        {
            if (node.SourceGroup is not null)
            {
                yield return node.SourceGroup;
                yield break;
            }

            foreach (var group in node.Children.SelectMany(GetSourceGroups))
            {
                yield return group;
            }
        }
    }

    private sealed class PublishedFileRow
    {
        public PublishedFileRow(LibrarySyncFile file)
        {
            File = file;
            DisplayName = string.IsNullOrWhiteSpace(file.WorksheetName)
                ? file.DisplayName
                : file.WorksheetName;
            Detail = GetDestinationLabel(file.SourceRelativePath);
            PageLabel = $"{file.PageCount} 页";
        }

        public LibrarySyncFile File { get; }

        public string DisplayName { get; }

        public string Detail { get; }

        public string PageLabel { get; }
    }

    private sealed record PublishedScopeOption(
        string Name,
        string? RootLabel);

    private sealed class PublishedArchiveRow
    {
        public PublishedArchiveRow(
            LibrarySyncManifest manifest,
            bool isCurrent)
        {
            Manifest = manifest;
            IsCurrent = isCurrent;
            var local = manifest.GeneratedAtUtc.ToLocalTime();
            DateLabel = local.ToString("yyyy-MM-dd");
            TimeLabel = local.ToString("HH:mm:ss");
            Notice = string.IsNullOrWhiteSpace(manifest.Notice)
                ? "未填写更新说明"
                : manifest.Notice;
            var groups = manifest.Files
                .Select(file => file.SourceRelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            Summary = $"{groups} 组 · {manifest.Files.Count} PDF";
            StateLabel = isCurrent ? "当前" : "存档";
            BadgeBackground = new SolidColorBrush(isCurrent
                ? Color.FromRgb(225, 241, 234)
                : Color.FromRgb(232, 240, 247));
            BadgeForeground = new SolidColorBrush(isCurrent
                ? Color.FromRgb(25, 120, 90)
                : Color.FromRgb(45, 114, 164));
        }

        public LibrarySyncManifest Manifest { get; }

        public bool IsCurrent { get; }

        public string DateLabel { get; }

        public string TimeLabel { get; }

        public string Notice { get; }

        public string Summary { get; }

        public string StateLabel { get; }

        public Brush BadgeBackground { get; }

        public Brush BadgeForeground { get; }
    }

    private sealed class BatchOption
    {
        public BatchOption(DraftBatch batch)
        {
            Batch = batch;
            var state = batch.Status switch
            {
                "pending-publish" => "待发布",
                "published" => "已发布",
                "test-only-do-not-publish" => "旧测试批次",
                _ => batch.Status
            };
            DisplayText =
                $"{batch.CreatedAtUtc.ToLocalTime():MM-dd HH:mm} · {state} · " +
                $"{batch.PdfCount} PDF";
        }

        public DraftBatch Batch { get; }

        public string DisplayText { get; }
    }

    private sealed class PublishItemRow
    {
        private PublishItemRow(
            string actionLabel,
            Brush actionForeground,
            string displayName,
            string detail,
            string meta,
            PdfDraftOutput? output,
            bool isIncluded,
            bool canChoose)
        {
            ActionLabel = actionLabel;
            ActionForeground = actionForeground;
            DisplayName = displayName;
            Detail = detail;
            Meta = meta;
            Output = output;
            IsIncluded = isIncluded;
            CanChoose = canChoose;
            SelectionVisibility = output is null
                ? Visibility.Hidden
                : Visibility.Visible;
        }

        public string ActionLabel { get; }

        public Brush ActionForeground { get; }

        public string DisplayName { get; }

        public string Detail { get; }

        public string Meta { get; }

        public PdfDraftOutput? Output { get; }

        public bool IsIncluded { get; set; }

        public bool CanChoose { get; }

        public Visibility SelectionVisibility { get; }

        public static PublishItemRow ForPdf(
            DraftSourceItem source,
            PdfDraftOutput output,
            bool isIncluded,
            bool canChoose) =>
            new(
                "新增/替换",
                new SolidColorBrush(Color.FromRgb(45, 114, 164)),
                output.DisplayName,
                $"{GetDestinationLabel(source.RelativePath)} · " +
                Path.GetFileName(source.RelativePath),
                $"{output.PageCount} 页",
                output,
                isIncluded,
                canChoose);

        public static PublishItemRow ForRemoval(string relativePath) =>
            new(
                "删除",
                new SolidColorBrush(Color.FromRgb(174, 73, 54)),
                Path.GetFileName(relativePath),
                GetDestinationLabel(relativePath),
                "整组移除",
                null,
                true,
                false);
    }

    private sealed record DestinationOption(
        string Name,
        string FolderName);
}
