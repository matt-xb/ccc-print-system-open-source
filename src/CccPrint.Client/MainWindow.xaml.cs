using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CccPrint.Networking;

namespace CccPrint.Client;

public partial class MainWindow : Window
{
    private static readonly CategoryOption[] StandardCategories =
    {
        new("warehouse", "仓库"),
        new("workshop", "车间"),
        new("finance", "财务"),
        new("other", "其余资料")
    };
    private static readonly CategoryOption[] GovernmentCategories =
    {
        new("emergency", "应急管理"),
        new("safety", "安全监管"),
        new("policy", "政策法规"),
        new("other", "其他资料")
    };
    private const string AllSubcategories = "全部";
    private const string UngroupedSubcategory = "未分组";
    private const string SubcategoryDragFormat =
        "CccPrint.Client.Subcategory";
    private static readonly TimeSpan MonitorInterval =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan InventoryReportInterval =
        TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SoftwareUpdateCheckInterval =
        TimeSpan.FromMinutes(30);
    private readonly ClientOptions _options;
    private readonly ServerSettingsStore _settingsStore = new();
    private readonly SubcategoryOrderStore _subcategoryOrderStore = new();
    private readonly LocalLibraryStore _libraryStore = new();
    private readonly WindowsPdfPrintService _printService = new();
    private readonly ObservableCollection<SheetPrintItem> _printCart = new();
    private readonly Dictionary<string, List<string>> _subcategoryOrders;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private CancellationTokenSource? _monitorSource;
    private Task? _monitorTask;
    private CancellationTokenSource? _updateSource;
    private DiscoveredServer? _connectedServer;
    private LibrarySyncManifest? _localManifest;
    private LibrarySyncManifest? _remoteManifest;
    private IReadOnlyList<DocumentGroupViewModel> _allGroups =
        Array.Empty<DocumentGroupViewModel>();
    private IReadOnlyList<DocumentGroupViewModel> _visibleGroups =
        Array.Empty<DocumentGroupViewModel>();
    private string _selectedLibraryId = "ccc-gas";
    private string _selectedCategoryId = "warehouse";
    private string _selectedSubcategoryName = AllSubcategories;
    private Point _subcategoryDragStart;
    private Button? _subcategoryDragSource;
    private DateTimeOffset _lastInventoryReportAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSoftwareUpdateCheckAtUtc = DateTimeOffset.MinValue;
    private string? _lastReportedLibraryVersion;

    public MainWindow(ClientOptions options)
    {
        InitializeComponent();
        _options = options;
        _subcategoryOrders = _subcategoryOrderStore.Load();
        GroupsList.ItemsSource = _visibleGroups;
        PrintCartList.ItemsSource = _printCart;
        DefaultPrinterText.Text =
            $"默认打印机：{GetFriendlyPrinterName(
                _printService.GetDefaultPrinterName())}";
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        await LoadLocalLibraryAsync();
        SetLibraryReady(HasUsableLocalLibrary());
        if (HasUsableLocalLibrary())
        {
            SetConnectionState(
                "#D59A2D",
                "本地资料可用",
                "正在后台检查管理端；管理端离线不影响预览和打印");
        }
        else
        {
            SetConnectionState(
                "#C94335",
                "等待首次同步",
                "本机暂无完整资料，正在等待管理端上线");
        }

        _monitorSource = new CancellationTokenSource();
        _monitorTask = MonitorServerAsync(_monitorSource.Token);
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        await VerifyAndSyncLibraryAsync(
            userInitiated: true,
            _monitorSource?.Token ?? CancellationToken.None);
    }

    private async void SoftwareUpdateButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await CheckSoftwareUpdateAsync();
    }

    private void GasLibraryButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        OpenLibrary("ccc-gas");
    }

    private void ElectricLibraryButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        OpenLibrary("electric-heating");
    }

    private void GovernmentLibraryButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        OpenLibrary("government");
    }

    private void BackHomeButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        LibraryView.Visibility = Visibility.Collapsed;
        HomeView.Visibility = Visibility.Visible;
    }

    private void WarehouseCategoryButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        SelectCategory(GetCategoryOptions()[0].Id);
    }

    private void WorkshopCategoryButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        SelectCategory(GetCategoryOptions()[1].Id);
    }

    private void FinanceCategoryButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        SelectCategory(GetCategoryOptions()[2].Id);
    }

    private void OtherCategoryButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        SelectCategory(GetCategoryOptions()[3].Id);
    }

    private void SubcategoryButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is Button { Tag: string subcategoryName })
        {
            _selectedSubcategoryName = subcategoryName;
            RefreshGroups();
        }
    }

    private void SubcategoryButton_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        _subcategoryDragStart = e.GetPosition(this);
        _subcategoryDragSource = sender as Button;
    }

    private void SubcategoryButton_PreviewMouseMove(
        object sender,
        MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            sender is not Button { Tag: string name } button ||
            button != _subcategoryDragSource ||
            name == AllSubcategories)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _subcategoryDragStart.X) <
                SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _subcategoryDragStart.Y) <
                SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var payload = new SubcategoryDragPayload(
            GetSubcategoryOrderKey(),
            name);
        button.Opacity = 0.55;
        try
        {
            DragDrop.DoDragDrop(
                button,
                new DataObject(SubcategoryDragFormat, payload),
                DragDropEffects.Move);
        }
        finally
        {
            button.Opacity = 1;
            _subcategoryDragSource = null;
        }

        e.Handled = true;
    }

    private void SubcategoryButton_Drop(
        object sender,
        DragEventArgs e)
    {
        if (sender is not Button { Tag: string targetName } target ||
            targetName == AllSubcategories ||
            !TryGetSubcategoryDragPayload(e, out var payload))
        {
            return;
        }

        var insertAfter = e.GetPosition(target).X >
                          target.ActualWidth / 2;
        MoveSubcategory(
            payload.Name,
            targetName,
            insertAfter);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void SubcategoryTabsPanel_Drop(
        object sender,
        DragEventArgs e)
    {
        if (e.Handled ||
            !TryGetSubcategoryDragPayload(e, out var payload))
        {
            return;
        }

        var names = GetCurrentSubcategoryNames().ToList();
        if (!names.Remove(payload.Name))
        {
            return;
        }

        names.Add(payload.Name);
        SaveSubcategoryOrder(names);
        RefreshGroups();
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void SearchBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        SearchHintText.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshGroups();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        RefreshGroups();
        e.Handled = true;
    }

    private void GroupHeaderButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is Button
            {
                DataContext: DocumentGroupViewModel group
            } &&
            group.HasMultipleItems)
        {
            group.IsExpanded = !group.IsExpanded;
        }
    }

    private void SheetCheckBox_Click(
        object sender,
        RoutedEventArgs e)
    {
        RefreshPrintCart();
    }

    private void DecreaseCopiesButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SheetPrintItem item })
        {
            item.Copies--;
            item.IsSelected = true;
            RefreshPrintCart();
        }
    }

    private void IncreaseCopiesButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SheetPrintItem item })
        {
            item.Copies++;
            item.IsSelected = true;
            RefreshPrintCart();
        }
    }

    private void CopiesTextBox_PreviewTextInput(
        object sender,
        TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(character => !char.IsDigit(character));
    }

    private void CopiesTextBox_Pasting(
        object sender,
        DataObjectPastingEventArgs e)
    {
        var textBox = sender as TextBox;
        if (textBox is null ||
            !e.DataObject.GetDataPresent(DataFormats.UnicodeText) ||
            e.DataObject.GetData(DataFormats.UnicodeText) is not string pasted ||
            pasted.Length == 0 ||
            pasted.Any(character => !char.IsDigit(character)))
        {
            e.CancelCommand();
            RestoreCopiesText(textBox);
        }
    }

    private void CopiesTextBox_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            CommitCopiesText(textBox);
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            RestoreCopiesText(textBox);
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void CopiesTextBox_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            CommitCopiesText(textBox);
        }
    }

    private void CopiesTextBox_GotKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.SelectAll();
        }
    }

    private void CommitCopiesText(TextBox textBox)
    {
        if (textBox.DataContext is not SheetPrintItem item ||
            !int.TryParse(textBox.Text, out var copies) ||
            copies is < 1 or > 99)
        {
            RestoreCopiesText(textBox);
            return;
        }

        item.Copies = copies;
        item.IsSelected = true;
        textBox.Text = item.Copies.ToString();
        RefreshPrintCart();
    }

    private static void RestoreCopiesText(TextBox? textBox)
    {
        if (textBox?.DataContext is SheetPrintItem item)
        {
            textBox.Text = item.Copies.ToString();
            textBox.SelectAll();
        }
    }

    private void SheetPreviewButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SheetPrintItem item })
        {
            OpenPdf(item);
        }
    }

    private void ClearSelectionButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        foreach (var item in _allGroups.SelectMany(group => group.AllItems))
        {
            item.IsSelected = false;
            item.Copies = 1;
        }

        RefreshPrintCart();
    }

    private async void OneClickPrintButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await PrintSelectedAsync();
    }

    private async Task LoadLocalLibraryAsync()
    {
        _localManifest = await _libraryStore.LoadAsync();
        RebuildDocumentGroups();
    }

    private async Task MonitorServerAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await VerifyAndSyncLibraryAsync(
                    userInitiated: false,
                    cancellationToken);
                await Task.Delay(MonitorInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭时停止后台检测。
        }
    }

    private async Task VerifyAndSyncLibraryAsync(
        bool userInitiated,
        CancellationToken cancellationToken)
    {
        if (!await _syncGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var localReady = HasUsableLocalLibrary();
            RetryButton.IsEnabled = false;
            RetryButton.Visibility = Visibility.Collapsed;
            if (userInitiated || _connectedServer is null)
            {
                SetConnectionState(
                    "#D59A2D",
                    localReady ? "检查更新中" : "连接中",
                    localReady
                        ? "本地资料可用，正在检查管理端更新"
                        : "正在连接管理端并下载首次资料");
            }

            var remoteState = await GetRemoteStateAsync(cancellationToken);
            if (remoteState is null)
            {
                _connectedServer = null;
                SoftwareUpdateButton.IsEnabled = false;
                RetryButton.Visibility = Visibility.Visible;
                SetLibraryReady(localReady);
                SetConnectionState(
                    localReady ? "#D59A2D" : "#C94335",
                    localReady ? "离线使用" : "等待管理端",
                    localReady
                        ? "管理端未上线，正在使用本地资料；上线后会自动同步"
                        : "本机暂无完整资料；管理端上线后会自动首次同步");
                return;
            }

            _connectedServer = remoteState.Server;
            SoftwareUpdateButton.IsEnabled = true;
            var status = remoteState.Status;
            if (string.IsNullOrWhiteSpace(status.Version))
            {
                SetLibraryReady(localReady);
                SetConnectionState(
                    localReady ? "#D59A2D" : "#C94335",
                    localReady ? "本地资料可用" : "等待首次发布",
                    localReady
                        ? "管理端在线但暂无发布资料，继续使用本地版本"
                        : string.IsNullOrWhiteSpace(status.Notice)
                            ? "管理端还没有可同步的资料版本"
                            : status.Notice);
                return;
            }

            var requiresManifest =
                !localReady ||
                !string.Equals(
                    _localManifest?.Version,
                    status.Version,
                    StringComparison.OrdinalIgnoreCase) ||
                _localManifest?.Files.Count != status.FileCount;
            var synchronized = false;
            if (requiresManifest)
            {
                SetConnectionState(
                    "#D59A2D",
                    "发现资料更新",
                    "正在获取最新资料清单");
                using var client = new LibrarySyncClient();
                var remote = await client.GetManifestAsync(
                    remoteState.Server.BaseAddress,
                    cancellationToken);
                _remoteManifest = remote;
                if (string.IsNullOrWhiteSpace(remote.Version))
                {
                    throw new InvalidDataException(
                        string.IsNullOrWhiteSpace(remote.Notice)
                            ? "管理端还没有可同步的资料版本。"
                            : remote.Notice);
                }

                if (IsDataOutdated(remote))
                {
                    SetConnectionState(
                        "#D59A2D",
                        "正在同步",
                        $"发现资料不一致，正在同步 0/{remote.Files.Count}");
                    var progress = new Progress<LibrarySyncProgress>(value =>
                    {
                        var completed = Math.Min(
                            value.CompletedFiles,
                            value.TotalFiles);
                        FooterStatusText.Text =
                            $"正在同步资料 {completed}/{value.TotalFiles}";
                    });
                    await _libraryStore.SyncAsync(
                        remoteState.Server.BaseAddress,
                        remote,
                        progress,
                        cancellationToken);
                    _localManifest = remote;
                    RebuildDocumentGroups();
                    synchronized = true;
                }
            }

            localReady = HasUsableLocalLibrary();
            if (!localReady)
            {
                throw new InvalidDataException("本地资料未完整同步。");
            }

            SetLibraryReady(true);
            SetConnectionState(
                "#28A66A",
                synchronized ? "同步完成" : "资料已是最新",
                $"本地资料已就绪 · {_localManifest!.Files.Count} 份 PDF");

            var now = DateTimeOffset.UtcNow;
            if (!string.Equals(
                    _lastReportedLibraryVersion,
                    _localManifest.Version,
                    StringComparison.OrdinalIgnoreCase) ||
                now - _lastInventoryReportAtUtc >= InventoryReportInterval)
            {
                await ReportInventoryAsync(cancellationToken);
                _lastReportedLibraryVersion = _localManifest.Version;
                _lastInventoryReportAtUtc = now;
            }

            if (now - _lastSoftwareUpdateCheckAtUtc >=
                SoftwareUpdateCheckInterval)
            {
                await DetectSoftwareUpdateAsync(cancellationToken);
                _lastSoftwareUpdateCheckAtUtc = now;
            }
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            _connectedServer = null;
            SoftwareUpdateButton.IsEnabled = false;
            var localReady = HasUsableLocalLibrary();
            SetLibraryReady(localReady);
            RetryButton.Visibility = Visibility.Visible;
            SetConnectionState(
                localReady ? "#D59A2D" : "#C94335",
                localReady ? "离线使用" : "等待管理端",
                localReady
                    ? "管理端连接已中断，正在使用本地资料；系统会自动重连"
                    : "连接管理端超时；系统会自动重试首次同步");
        }
        catch (OperationCanceledException)
        {
            // 关闭窗口时停止检测或同步。
        }
        catch (Exception exception)
        {
            ClientLog.Write("后台核对或同步资料失败", exception);
            var localReady = HasUsableLocalLibrary();
            SetLibraryReady(localReady);
            RetryButton.Visibility = Visibility.Visible;
            SetConnectionState(
                localReady ? "#D59A2D" : "#C94335",
                "同步失败",
                localReady
                    ? "同步失败，继续使用本地资料；系统会自动重试"
                    : "首次同步失败；系统会在管理端上线后自动重试");
        }
        finally
        {
            RetryButton.IsEnabled = true;
            _syncGate.Release();
        }
    }

    private async Task<RemoteLibraryState?> GetRemoteStateAsync(
        CancellationToken cancellationToken)
    {
        using var client = new LibrarySyncClient();
        if (_connectedServer is not null)
        {
            try
            {
                var currentStatus = await client.GetStatusAsync(
                    _connectedServer.BaseAddress,
                    cancellationToken);
                return new RemoteLibraryState(
                    _connectedServer,
                    currentStatus);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                _connectedServer = null;
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                _connectedServer = null;
                return null;
            }
        }

        var cachedAddress = await _settingsStore.LoadAddressAsync(
            _options.ServerDeviceName);
        using var locator = new ServerLocator();
        var result = await locator.LocateAsync(
            _options.ServerDeviceName,
            _options.ServerPort,
            _options.DiscoveryPort,
            cachedAddress,
            cancellationToken);
        if (!result.Success || result.Server is null)
        {
            return null;
        }

        await _settingsStore.SaveAsync(result.Server);
        var status = await client.GetStatusAsync(
            result.Server.BaseAddress,
            cancellationToken);
        return new RemoteLibraryState(result.Server, status);
    }

    private bool HasUsableLocalLibrary() =>
        _localManifest is not null && _libraryStore.IsComplete(_localManifest);

    private async Task DetectSoftwareUpdateAsync(
        CancellationToken cancellationToken)
    {
        if (_connectedServer is null)
        {
            SoftwareUpdateDot.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            using var appClient = new AppUpdateClient();
            var appManifest = await appClient.GetManifestAsync(
                _connectedServer.BaseAddress,
                cancellationToken);
            var appOutdated =
                appManifest.Available &&
                Version.TryParse(
                    appManifest.Version,
                    out var availableVersion) &&
                availableVersion > Version.Parse(GetCurrentVersion());
            SoftwareUpdateDot.Visibility = appOutdated
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            // 关闭窗口或重新同步时取消检查。
        }
        catch (Exception exception)
        {
            ClientLog.Write("自动检查软件更新失败", exception);
        }
    }

    private async Task ReportInventoryAsync(
        CancellationToken cancellationToken)
    {
        if (_connectedServer is null)
        {
            return;
        }

        try
        {
            using var reporter = new ClientInventoryReporter();
            await reporter.ReportAsync(
                _connectedServer.BaseAddress,
                new ClientLibraryInventory(
                    Environment.MachineName,
                    GetCurrentVersion(),
                    DateTimeOffset.UtcNow,
                    _localManifest),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭或重新连接时停止后台上报。
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            ClientLog.Write("回报本地资料清单失败", exception);
        }
    }

    private async Task CheckSoftwareUpdateAsync()
    {
        if (_connectedServer is null)
        {
            ShowInformation("请先连接管理端，再检查软件更新。", "软件更新");
            return;
        }

        _updateSource?.Cancel();
        _updateSource?.Dispose();
        _updateSource = new CancellationTokenSource();
        SoftwareUpdateButton.IsEnabled = false;
        RetryButton.IsEnabled = false;
        SoftwareUpdateButton.Content = "正在检查…";

        try
        {
            using var updateClient = new AppUpdateClient();
            var manifest = await updateClient.GetManifestAsync(
                _connectedServer.BaseAddress,
                _updateSource.Token);
            if (!manifest.Available)
            {
                ShowInformation(
                    manifest.Message ?? "管理端当前没有可用安装包。",
                    "软件更新");
                return;
            }

            if (!Version.TryParse(manifest.Version, out var availableVersion))
            {
                throw new InvalidDataException("管理端返回的软件版本号无效。");
            }

            var currentVersion = Version.Parse(GetCurrentVersion());
            if (availableVersion <= currentVersion)
            {
                SoftwareUpdateDot.Visibility = Visibility.Collapsed;
                ShowInformation(
                    $"当前已是最新版本 v{currentVersion.ToString(3)}。",
                    "软件更新");
                return;
            }

            var notes = string.IsNullOrWhiteSpace(manifest.ReleaseNotes)
                ? "管理端未填写更新说明。"
                : manifest.ReleaseNotes;
            var answer = MessageBox.Show(
                $"发现新版本 v{availableVersion.ToString(3)}。\n\n" +
                $"{notes}\n\n是否立即下载并安装？",
                "发现软件更新",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            var packageName = Path.GetFileName(manifest.PackageFileName);
            if (string.IsNullOrWhiteSpace(packageName))
            {
                throw new InvalidDataException("更新安装包名称无效。");
            }

            var updateDirectory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "CCC资料打印系统公开版",
                "打印端",
                "updates",
                availableVersion.ToString(3));
            Directory.CreateDirectory(updateDirectory);
            var temporaryPath = Path.Combine(
                updateDirectory,
                $"{packageName}.download");
            var installerPath = Path.Combine(updateDirectory, packageName);
            File.Delete(temporaryPath);

            var progress = new Progress<double>(value =>
            {
                SoftwareUpdateButton.Content = $"下载 {value:P0}";
            });
            await updateClient.DownloadAndVerifyAsync(
                _connectedServer.BaseAddress,
                manifest,
                temporaryPath,
                progress,
                _updateSource.Token);
            File.Move(temporaryPath, installerPath, true);

            SoftwareUpdateButton.Content = "准备安装…";
            MessageBox.Show(
                "安装包已下载并校验完成。\n\n" +
                "点击“确定”后，打印端将自动关闭、完成安装并重新启动。",
                "准备安装更新",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            var installerLogPath = Path.Combine(
                updateDirectory,
                $"install-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var installDirectory = Path.TrimEndingDirectorySeparator(
                AppContext.BaseDirectory);
            var installerProcess = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments =
                    "/SILENT /SUPPRESSMSGBOXES /NORESTART " +
                    $"/CLOSEAPPLICATIONS /DIR=\"{installDirectory}\" " +
                    $"/LOG=\"{installerLogPath}\"",
                UseShellExecute = true
            });
            if (installerProcess is null)
            {
                throw new InvalidOperationException("无法启动软件安装程序。");
            }

            Application.Current.Shutdown();
            return;
        }
        catch (OperationCanceledException)
        {
            // 窗口关闭时取消下载。
        }
        catch (Exception exception)
        {
            ClientLog.Write("软件更新失败", exception);
            MessageBox.Show(
                $"软件更新失败：{exception.GetBaseException().Message}",
                "软件更新",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SoftwareUpdateButton.Content = "检查软件更新";
            SoftwareUpdateButton.IsEnabled = _connectedServer is not null;
            RetryButton.IsEnabled = true;
        }
    }

    private void OpenLibrary(string libraryId)
    {
        _selectedLibraryId = libraryId;
        _selectedCategoryId = GetCategoryOptions()[0].Id;
        _selectedSubcategoryName = AllSubcategories;
        SearchBox.Text = string.Empty;
        RefreshGroups();
        HomeView.Visibility = Visibility.Collapsed;
        LibraryView.Visibility = Visibility.Visible;
    }

    private void SelectCategory(string categoryId)
    {
        _selectedCategoryId = categoryId;
        _selectedSubcategoryName = AllSubcategories;
        RefreshGroups();
    }

    private void RebuildDocumentGroups()
    {
        var selectedItems = _printCart.ToDictionary(
            item => item.File.FileId,
            item => item.Copies,
            StringComparer.OrdinalIgnoreCase);
        foreach (var item in _allGroups.SelectMany(group => group.AllItems))
        {
            item.PropertyChanged -= SheetItem_PropertyChanged;
        }

        var files = _localManifest?.Files ??
                    Array.Empty<LibrarySyncFile>();
        _allGroups = files
            .GroupBy(
                file => file.SourceRelativePath,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new DocumentGroupViewModel(
                group.Key,
                group.ToArray()))
            .OrderBy(group => group.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var item in _allGroups.SelectMany(group => group.AllItems))
        {
            item.PropertyChanged += SheetItem_PropertyChanged;
        }

        _printCart.Clear();
        foreach (var item in _allGroups
                     .SelectMany(group => group.AllItems)
                     .Where(item => selectedItems.ContainsKey(item.File.FileId)))
        {
            item.Copies = selectedItems[item.File.FileId];
            item.IsSelected = true;
        }

        RefreshLibraryCounts();
        RefreshGroups();
        RefreshPrintCart();
    }

    private void RefreshLibraryCounts()
    {
        SetLibraryCount("ccc-gas", GasCountText);
        SetLibraryCount("electric-heating", ElectricCountText);
        SetLibraryCount("government", GovernmentCountText);
        TestDataBanner.Visibility = _localManifest?.IsTestData == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SetLibraryCount(string libraryId, TextBlock target)
    {
        var groups = _allGroups
            .Where(group => group.LibraryId == libraryId)
            .ToArray();
        target.Text =
            $"{groups.Length} 组表格 · {groups.Sum(group => group.SheetCount)} 张可选表单";
    }

    private void RefreshGroups()
    {
        if (GroupsList is null)
        {
            return;
        }

        var libraryGroups = _allGroups
            .Where(group => group.LibraryId == _selectedLibraryId)
            .ToArray();
        UpdateCategoryPresentation(libraryGroups);
        UpdateCategoryButtonStates();
        RefreshSubcategoryTabs(libraryGroups);

        var query = SearchBox?.Text ?? string.Empty;
        var searching = !string.IsNullOrWhiteSpace(query);
        SearchHintText.Visibility = searching
            ? Visibility.Collapsed
            : Visibility.Visible;
        _visibleGroups = libraryGroups
            .Where(group =>
                (searching ||
                 group.CategoryId == _selectedCategoryId &&
                 (_selectedSubcategoryName == AllSubcategories ||
                  GetSubcategoryName(group) ==
                  _selectedSubcategoryName)) &&
                group.ApplySearch(query))
            .ToArray();
        GroupsList.ItemsSource = _visibleGroups;

        var libraryName = _selectedLibraryId switch
        {
            "ccc-gas" => "CCC 燃气资料库",
            "electric-heating" => "电加热资料库",
            "government" => "政府资料库",
            _ => "资料库"
        };
        SelectedLibraryText.Text = libraryName;
        SelectedDepartmentText.Text =
            searching
                ? "搜索结果"
                : GetSelectedDepartmentName();
        VisibleCountText.Text =
            $"{_visibleGroups.Count} 组 · " +
            $"{_visibleGroups.Sum(group => group.VisibleItems.Count)} 张子表";
        NoResultsText.Visibility = _visibleGroups.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        SubcategoryBar.Visibility =
            !searching && SubcategoryTabsPanel.Children.Count > 1
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private static void SetDepartmentCount(
        TextBlock target,
        IEnumerable<DocumentGroupViewModel> groups,
        string categoryId)
    {
        var selected = groups
            .Where(group => group.CategoryId == categoryId)
            .ToArray();
        target.Text =
            $"{selected.Length} 组 · {selected.Sum(group => group.SheetCount)} 张";
    }

    private void UpdateCategoryPresentation(
        IReadOnlyList<DocumentGroupViewModel> libraryGroups)
    {
        var categories = GetCategoryOptions();
        var presentations = new[]
        {
            (FirstCategoryNameText, WarehouseCountText),
            (SecondCategoryNameText, WorkshopCountText),
            (ThirdCategoryNameText, FinanceCountText),
            (FourthCategoryNameText, OtherCountText)
        };
        for (var index = 0; index < categories.Length; index++)
        {
            presentations[index].Item1.Text = categories[index].Name;
            SetDepartmentCount(
                presentations[index].Item2,
                libraryGroups,
                categories[index].Id);
        }
    }

    private void SheetItem_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SheetPrintItem.IsSelected) or
            nameof(SheetPrintItem.Copies))
        {
            RefreshPrintCart();
        }
    }

    private void RefreshPrintCart()
    {
        var selected = _allGroups
            .SelectMany(group => group.AllItems)
            .Where(item => item.IsSelected)
            .ToArray();
        _printCart.Clear();
        foreach (var item in selected)
        {
            _printCart.Add(item);
        }

        var totalCopies = selected.Sum(item => item.Copies);
        PrintSummaryText.Text = selected.Length == 0
            ? "尚未选择子表"
            : $"{selected.Length} 张子表 · 共 {totalCopies} 份";
        OneClickPrintButton.Content = totalCopies == 0
            ? "一键打印"
            : $"一键打印 {totalCopies} 份";
        OneClickPrintButton.IsEnabled = totalCopies > 0;
        ClearSelectionButton.IsEnabled = selected.Length > 0;
    }

    private async Task PrintSelectedAsync()
    {
        if (_localManifest is null)
        {
            return;
        }

        var selected = _allGroups
            .SelectMany(group => group.AllItems)
            .Where(item => item.IsSelected)
            .ToArray();
        var jobs = new List<(string PdfPath, int Copies)>();
        foreach (var item in selected)
        {
            var path = _libraryStore.GetPdfPath(
                _localManifest,
                item.File);
            if (path is null)
            {
                ShowInformation(
                    $"找不到“{item.Name}”的本地 PDF，请返回主页重试同步。",
                    "一键打印");
                return;
            }

            jobs.Add((path, item.Copies));
        }

        var totalCopies = jobs.Sum(job => job.Copies);
        var printerName = _printService.GetDefaultPrinterName();
        var answer = MessageBox.Show(
            $"将按顺序向 Windows 默认打印机提交 {selected.Length} 份资料。\n\n" +
            $"打印机：{printerName}\n" +
            $"子表：{selected.Length} 张\n" +
            $"总份数：{totalCopies}\n\n" +
            "同一张子表的多份打印只提交一次；不同子表会依次处理，避免电脑卡顿。\n" +
            "是否开始打印？",
            "确认一键打印",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        OneClickPrintButton.IsEnabled = false;
        try
        {
            var progress = new Progress<int>(count =>
            {
                OneClickPrintButton.Content =
                    $"排队打印 {count}/{selected.Length}";
            });
            var submitted = await _printService.PrintAsync(jobs, progress);
            MessageBox.Show(
                $"已按顺序提交 {submitted} 份资料，共 {totalCopies} 份。\n\n" +
                "如果需要更换打印机，请先在 Windows 中修改默认打印机。",
                "打印任务已提交",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            ClientLog.Write("批量打印失败", exception);
            MessageBox.Show(
                $"打印失败：{exception.GetBaseException().Message}",
                "一键打印",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            RefreshPrintCart();
        }
    }

    private void OpenPdf(SheetPrintItem item)
    {
        if (_localManifest is null)
        {
            return;
        }

        var path = _libraryStore.GetPdfPath(_localManifest, item.File);
        if (path is null)
        {
            ShowInformation(
                "本地 PDF 不存在，请返回主页重试同步。",
                "打开预览");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            ClientLog.Write("打开 PDF 失败", exception);
            MessageBox.Show(
                $"无法打开 PDF：{exception.GetBaseException().Message}",
                "打开预览",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private bool IsDataOutdated(LibrarySyncManifest remote) =>
        !string.Equals(
            _localManifest?.Version,
            remote.Version,
            StringComparison.OrdinalIgnoreCase) ||
        BuildDataChanges(remote).Total > 0 ||
        !_libraryStore.IsComplete(remote);

    private DataChanges BuildDataChanges(LibrarySyncManifest remote)
    {
        var localFiles = (_localManifest?.Files ??
                          Array.Empty<LibrarySyncFile>())
            .ToDictionary(file => file.FileId, StringComparer.OrdinalIgnoreCase);
        var remoteFiles = remote.Files
            .ToDictionary(file => file.FileId, StringComparer.OrdinalIgnoreCase);
        var added = remoteFiles.Keys.Count(key =>
            !localFiles.ContainsKey(key));
        var updated = remoteFiles.Count(pair =>
            localFiles.TryGetValue(pair.Key, out var local) &&
            !local.Sha256.Equals(
                pair.Value.Sha256,
                StringComparison.OrdinalIgnoreCase));
        var removed = localFiles.Keys.Count(key =>
            !remoteFiles.ContainsKey(key));
        var affectedIds = remoteFiles
            .Where(pair =>
                !localFiles.TryGetValue(pair.Key, out var local) ||
                !local.Sha256.Equals(
                    pair.Value.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key)
            .Concat(localFiles.Keys.Where(key =>
                !remoteFiles.ContainsKey(key)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var affectedGroups = remote.Files
            .Where(file => affectedIds.Contains(file.FileId))
            .Select(file => CleanDocumentTitle(file.SourceRelativePath))
            .Concat((_localManifest?.Files ??
                     Array.Empty<LibrarySyncFile>())
                .Where(file =>
                    affectedIds.Contains(file.FileId) &&
                    !remoteFiles.ContainsKey(file.FileId))
                .Select(file =>
                    CleanDocumentTitle(file.SourceRelativePath)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new DataChanges(added, updated, removed, affectedGroups);
    }

    private static string CleanDocumentTitle(string relativePath) =>
        Path.GetFileNameWithoutExtension(relativePath)
            .Replace("（打印版）", string.Empty, StringComparison.Ordinal)
            .Replace("(打印版)", string.Empty, StringComparison.Ordinal)
            .Trim();

    private void UpdateCategoryButtonStates()
    {
        var categories = GetCategoryOptions();
        SetCategoryButtonState(
            WarehouseCategoryButton,
            _selectedCategoryId == categories[0].Id);
        SetCategoryButtonState(
            WorkshopCategoryButton,
            _selectedCategoryId == categories[1].Id);
        SetCategoryButtonState(
            FinanceCategoryButton,
            _selectedCategoryId == categories[2].Id);
        SetCategoryButtonState(
            OtherCategoryButton,
            _selectedCategoryId == categories[3].Id);
    }

    private void RefreshSubcategoryTabs(
        IReadOnlyList<DocumentGroupViewModel> libraryGroups)
    {
        var categoryGroups = libraryGroups
            .Where(group => group.CategoryId == _selectedCategoryId)
            .ToArray();
        var discoveredSubcategories = categoryGroups
            .Select(GetSubcategoryName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name == UngroupedSubcategory ? 1 : 0)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var subcategories = ApplySavedSubcategoryOrder(
            discoveredSubcategories);
        if (_selectedSubcategoryName != AllSubcategories &&
            !subcategories.Contains(
                _selectedSubcategoryName,
                StringComparer.OrdinalIgnoreCase))
        {
            _selectedSubcategoryName = AllSubcategories;
        }

        SubcategoryTabsPanel.Children.Clear();
        foreach (var name in new[] { AllSubcategories }.Concat(subcategories))
        {
            var button = new Button
            {
                Content = name,
                Tag = name,
                Style = (Style)FindResource("WorkshopTab")
            };
            button.Click += SubcategoryButton_Click;
            if (name != AllSubcategories)
            {
                button.AllowDrop = true;
                button.ToolTip = "按住并左右拖动排序";
                button.PreviewMouseLeftButtonDown +=
                    SubcategoryButton_PreviewMouseLeftButtonDown;
                button.PreviewMouseMove +=
                    SubcategoryButton_PreviewMouseMove;
                button.Drop += SubcategoryButton_Drop;
            }
            SetWorkshopButtonState(
                button,
                name.Equals(
                    _selectedSubcategoryName,
                    StringComparison.OrdinalIgnoreCase));
            SubcategoryTabsPanel.Children.Add(button);
        }
    }

    private IReadOnlyList<string> ApplySavedSubcategoryOrder(
        IReadOnlyList<string> discovered)
    {
        if (!_subcategoryOrders.TryGetValue(
                GetSubcategoryOrderKey(),
                out var saved))
        {
            return discovered;
        }

        return SubcategoryOrderStore.ApplyOrder(discovered, saved);
    }

    private IReadOnlyList<string> GetCurrentSubcategoryNames() =>
        SubcategoryTabsPanel.Children
            .OfType<Button>()
            .Select(button => button.Tag as string)
            .Where(name => !string.IsNullOrWhiteSpace(name) &&
                           name != AllSubcategories)
            .Cast<string>()
            .ToArray();

    private void MoveSubcategory(
        string sourceName,
        string targetName,
        bool insertAfter)
    {
        if (sourceName.Equals(
                targetName,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var names = SubcategoryOrderStore.Move(
            GetCurrentSubcategoryNames(),
            sourceName,
            targetName,
            insertAfter);
        SaveSubcategoryOrder(names.ToList());
        RefreshGroups();
    }

    private bool TryGetSubcategoryDragPayload(
        DragEventArgs e,
        out SubcategoryDragPayload payload)
    {
        payload = null!;
        if (!e.Data.GetDataPresent(SubcategoryDragFormat) ||
            e.Data.GetData(SubcategoryDragFormat) is not
                SubcategoryDragPayload candidate ||
            candidate.OrderKey != GetSubcategoryOrderKey())
        {
            return false;
        }

        payload = candidate;
        return true;
    }

    private void SaveSubcategoryOrder(List<string> names)
    {
        _subcategoryOrders[GetSubcategoryOrderKey()] = names;
        _subcategoryOrderStore.Save(_subcategoryOrders);
    }

    private string GetSubcategoryOrderKey() =>
        $"{_selectedLibraryId}|{_selectedCategoryId}";

    private static string GetSubcategoryName(DocumentGroupViewModel group) =>
        string.IsNullOrWhiteSpace(group.CustomGroupName)
            ? UngroupedSubcategory
            : group.CustomGroupName;

    private sealed record SubcategoryDragPayload(
        string OrderKey,
        string Name);

    private static void SetWorkshopButtonState(Button button, bool selected)
    {
        button.Foreground = new SolidColorBrush(
            selected
                ? Color.FromRgb(20, 125, 117)
                : Color.FromRgb(108, 123, 130));
        button.BorderBrush = new SolidColorBrush(
            selected
                ? Color.FromRgb(20, 125, 117)
                : Colors.Transparent);
        button.FontWeight = selected
            ? FontWeights.SemiBold
            : FontWeights.Normal;
    }

    private string GetSelectedDepartmentName()
    {
        var department = GetCategoryOptions()
            .FirstOrDefault(option => option.Id == _selectedCategoryId)
            ?.Name ?? "资料";
        if (_selectedSubcategoryName == AllSubcategories)
        {
            return department;
        }

        return $"{department} · {_selectedSubcategoryName}";
    }

    private static void SetCategoryButtonState(
        Button button,
        bool selected)
    {
        button.Background = new SolidColorBrush(
            selected
                ? Color.FromRgb(232, 241, 239)
                : Colors.Transparent);
        button.BorderBrush = new SolidColorBrush(
            selected
                ? Color.FromRgb(20, 125, 117)
                : Colors.Transparent);
    }

    private CategoryOption[] GetCategoryOptions() =>
        _selectedLibraryId == "government"
            ? GovernmentCategories
            : StandardCategories;

    private void SetConnectionState(
        string color,
        string text,
        string footer)
    {
        ConnectionDot.Fill =
            (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        ConnectionText.Text = $"{text} · v{GetCurrentVersion()}";
        FooterStatusText.Text = footer;
    }

    private void SetLibraryReady(bool ready)
    {
        GasLibraryButton.IsEnabled = ready;
        ElectricLibraryButton.IsEnabled = ready;
        GovernmentLibraryButton.IsEnabled = ready;
    }

    protected override void OnClosed(EventArgs e)
    {
        _monitorSource?.Cancel();
        _monitorSource?.Dispose();
        _updateSource?.Cancel();
        _updateSource?.Dispose();
        base.OnClosed(e);
    }

    private sealed record RemoteLibraryState(
        DiscoveredServer Server,
        LibrarySyncStatus Status);

    private static void ShowInformation(string message, string title)
    {
        MessageBox.Show(
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static string GetFriendlyPrinterName(string printerName)
    {
        var parts = printerName.Split(
            new[] { '\\', '/' },
            StringSplitOptions.RemoveEmptyEntries);
        return parts.LastOrDefault() ?? printerName;
    }

    private static string GetCurrentVersion() =>
        Assembly.GetExecutingAssembly()
            .GetName()
            .Version?
            .ToString(3)
        ?? "0.0.0";

    private sealed record DataChanges(
        int Added,
        int Updated,
        int Removed,
        IReadOnlyList<string> AffectedGroups)
    {
        public int Total => Added + Updated + Removed;
    }

    private sealed record CategoryOption(string Id, string Name);
}
