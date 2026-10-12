using System.Diagnostics;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CccPrint.Networking;
using Microsoft.Win32;

namespace CccPrint.Client;

public partial class App : Application
{
    private const string SingleInstanceMutexName =
        @"Local\CCCPrint.Client.SingleInstance";
    private const string ActivationEventName =
        @"Local\CCCPrint.Client.Activate";
    private const string UninstallRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" +
        @"{0B43C92F-4FB6-4B40-9261-625EE17A6E26}_is1";
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activationEvent;
    private Timer? _legacyDuplicateTimer;
    private volatile bool _shuttingDown;

    public App()
    {
        EnsureWindowsDirectoryEnvironment();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException +=
            OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException +=
            OnUnobservedTaskException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        EnsureWindowsDirectoryEnvironment();
        base.OnStartup(e);

        var options = ClientOptions.Load(AppContext.BaseDirectory);
        var testOutputArgument = e.Args.FirstOrDefault(argument =>
            argument.StartsWith(
                "--connection-test-output=",
                StringComparison.OrdinalIgnoreCase));
        var syncTestOutputArgument = e.Args.FirstOrDefault(argument =>
            argument.StartsWith(
                "--sync-test-output=",
                StringComparison.OrdinalIgnoreCase));
        var catalogTestOutputArgument = e.Args.FirstOrDefault(argument =>
            argument.StartsWith(
                "--catalog-test-output=",
                StringComparison.OrdinalIgnoreCase));

        if (testOutputArgument is not null ||
            syncTestOutputArgument is not null ||
            catalogTestOutputArgument is not null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var exitCode = testOutputArgument is not null
                ? await RunConnectionTestAsync(
                    options,
                    testOutputArgument.Split('=', 2)[1])
                : syncTestOutputArgument is not null
                    ? await RunSyncTestAsync(
                        options,
                        syncTestOutputArgument.Split('=', 2)[1])
                    : await RunCatalogTestAsync(
                        catalogTestOutputArgument!.Split('=', 2)[1]);
            Shutdown(exitCode);
            return;
        }

        if (TryOpenNewerInstalledCopy())
        {
            Shutdown(0);
            return;
        }

        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            SingleInstanceMutexName,
            out var isFirstInstance);
        if (!isFirstInstance)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            SignalExistingInstance();
            Shutdown(0);
            return;
        }

        _activationEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            ActivationEventName);
        _ = Task.Run(WaitForActivation);

        var mainWindow = new MainWindow(options);
        MainWindow = mainWindow;
        mainWindow.Show();
        _legacyDuplicateTimer = new Timer(
            CloseLegacyDuplicateProcesses,
            null,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2));
    }

    private void CloseLegacyDuplicateProcesses(object? state)
    {
        var currentId = Environment.ProcessId;
        using var currentProcess = Process.GetCurrentProcess();
        var currentSessionId = currentProcess.SessionId;
        var currentVersion =
            Assembly.GetExecutingAssembly().GetName().Version;
        if (currentVersion is null)
        {
            return;
        }

        foreach (var process in Process.GetProcessesByName(
                     "CCC资料打印端"))
        {
            using (process)
            {
                if (process.Id == currentId)
                {
                    continue;
                }

                try
                {
                    if (process.SessionId != currentSessionId)
                    {
                        continue;
                    }

                    var processPath = process.MainModule?.FileName;
                    var versionText = string.IsNullOrWhiteSpace(processPath)
                        ? null
                        : FileVersionInfo
                            .GetVersionInfo(processPath)
                            .ProductVersion;
                    if (!Version.TryParse(
                            versionText,
                            out var duplicateVersion))
                    {
                        continue;
                    }

                    if (duplicateVersion > currentVersion)
                    {
                        Dispatcher.BeginInvoke(() => Shutdown(0));
                        return;
                    }

                    process.CloseMainWindow();
                }
                catch
                {
                    // 进程可能已退出或属于不可访问的其他会话。
                }
            }
        }
    }

    private static bool TryOpenNewerInstalledCopy()
    {
        try
        {
            using var uninstallKey = Registry.CurrentUser.OpenSubKey(
                UninstallRegistryPath);
            var installLocation = uninstallKey?.GetValue(
                "InstallLocation") as string;
            if (string.IsNullOrWhiteSpace(installLocation))
            {
                return false;
            }

            var installedExe = Path.GetFullPath(Path.Combine(
                installLocation,
                "CCC资料打印端.exe"));
            var currentExe = Path.GetFullPath(
                Environment.ProcessPath ?? string.Empty);
            if (!File.Exists(installedExe) ||
                installedExe.Equals(
                    currentExe,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var currentVersion =
                Assembly.GetExecutingAssembly().GetName().Version;
            var installedVersionText = FileVersionInfo
                .GetVersionInfo(installedExe)
                .ProductVersion;
            if (!Version.TryParse(
                    installedVersionText,
                    out var installedVersion) ||
                currentVersion is null ||
                installedVersion <= currentVersion)
            {
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = installedExe,
                WorkingDirectory = Path.GetDirectoryName(installedExe),
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception exception)
        {
            ClientLog.Write("检查已安装的新版本失败", exception);
            return false;
        }
    }

    private static void SignalExistingInstance()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (EventWaitHandle.TryOpenExisting(
                    ActivationEventName,
                    out var activationEvent))
            {
                using (activationEvent)
                {
                    activationEvent.Set();
                }

                return;
            }

            Thread.Sleep(100);
        }
    }

    private void WaitForActivation()
    {
        while (!_shuttingDown)
        {
            _activationEvent?.WaitOne();
            if (_shuttingDown)
            {
                return;
            }

            Dispatcher.BeginInvoke(ActivateMainWindow);
        }
    }

    private void ActivateMainWindow()
    {
        if (MainWindow is null)
        {
            return;
        }

        if (MainWindow.WindowState == WindowState.Minimized)
        {
            MainWindow.WindowState = WindowState.Normal;
        }

        MainWindow.Show();
        MainWindow.Activate();
        MainWindow.Topmost = true;
        MainWindow.Topmost = false;
        MainWindow.Focus();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shuttingDown = true;
        _legacyDuplicateTimer?.Dispose();
        _activationEvent?.Set();
        _activationEvent?.Dispose();
        if (_singleInstanceMutex is not null)
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
        }

        base.OnExit(e);
    }

    private static void EnsureWindowsDirectoryEnvironment()
    {
        if (!string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("windir")))
        {
            return;
        }

        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (!string.IsNullOrWhiteSpace(systemRoot))
        {
            Environment.SetEnvironmentVariable("windir", systemRoot);
        }
    }

    private static async Task<int> RunConnectionTestAsync(
        ClientOptions options,
        string outputPath)
    {
        var settingsStore = new ServerSettingsStore();
        var cachedAddress = await settingsStore.LoadAddressAsync(
            options.ServerDeviceName);

        using var locator = new ServerLocator();
        var result = await locator.LocateAsync(
            options.ServerDeviceName,
            options.ServerPort,
            options.DiscoveryPort,
            cachedAddress);

        if (result.Success && result.Server is not null)
        {
            await settingsStore.SaveAsync(result.Server);
        }

        var report = new
        {
            result.Success,
            result.Message,
            DeviceName = result.Server?.DeviceName,
            Address = result.Server?.Address.ToString(),
            Port = result.Server?.ServicePort,
            result.Health?.AppVersion,
            CheckedAt = DateTimeOffset.Now
        };

        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullOutputPath)
            ?? AppContext.BaseDirectory);
        await File.WriteAllTextAsync(
            fullOutputPath,
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }));
        return result.Success ? 0 : 1;
    }

    private static async Task<int> RunSyncTestAsync(
        ClientOptions options,
        string outputPath)
    {
        try
        {
            var settingsStore = new ServerSettingsStore();
            var cachedAddress = await settingsStore.LoadAddressAsync(
                options.ServerDeviceName);
            using var locator = new ServerLocator();
            var connection = await locator.LocateAsync(
                options.ServerDeviceName,
                options.ServerPort,
                options.DiscoveryPort,
                cachedAddress);
            if (!connection.Success || connection.Server is null)
            {
                await WriteTestReportAsync(
                    outputPath,
                    new
                    {
                        success = false,
                        connection.Message
                    });
                return 1;
            }

            await settingsStore.SaveAsync(connection.Server);
            using var client = new LibrarySyncClient();
            var manifest = await client.GetManifestAsync(
                connection.Server.BaseAddress);
            if (string.IsNullOrWhiteSpace(manifest.Version))
            {
                await WriteTestReportAsync(
                    outputPath,
                    new
                    {
                        success = true,
                        manifest.Version,
                        manifest.IsTestData,
                        RemoteFiles = manifest.Files.Count,
                        Notice = manifest.Notice,
                        CheckedAt = DateTimeOffset.Now
                    });
                return 0;
            }

            var store = new LocalLibraryStore();
            await store.SyncAsync(
                connection.Server.BaseAddress,
                manifest);
            var local = await store.LoadAsync();
            var availableFiles = local?.Files.Count(file =>
                store.GetPdfPath(local, file) is not null) ?? 0;

            await WriteTestReportAsync(
                outputPath,
                new
                {
                    success = local is not null &&
                              availableFiles == manifest.Files.Count,
                    manifest.Version,
                    manifest.IsTestData,
                    RemoteFiles = manifest.Files.Count,
                    LocalFiles = availableFiles,
                    GasFiles = manifest.Files.Count(file =>
                        file.LibraryId == "ccc-gas"),
                    ElectricFiles = manifest.Files.Count(file =>
                        file.LibraryId == "electric-heating"),
                    GovernmentFiles = manifest.Files.Count(file =>
                        file.LibraryId == "government"),
                    CheckedAt = DateTimeOffset.Now
                });
            return local is not null &&
                   availableFiles == manifest.Files.Count
                ? 0
                : 1;
        }
        catch (Exception exception)
        {
            ClientLog.Write("资料同步测试失败", exception);
            await WriteTestReportAsync(
                outputPath,
                new
                {
                    success = false,
                    error = exception.GetBaseException().Message
                });
            return 1;
        }
    }

    private static async Task WriteTestReportAsync(
        string outputPath,
        object report)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullOutputPath)
            ?? AppContext.BaseDirectory);
        await File.WriteAllTextAsync(
            fullOutputPath,
            JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }));
    }

    private static async Task<int> RunCatalogTestAsync(string outputPath)
    {
        try
        {
            var localLoadTimer = Stopwatch.StartNew();
            var store = new LocalLibraryStore();
            var manifest = await store.LoadAsync();
            var groups = (manifest?.Files ?? Array.Empty<LibrarySyncFile>())
                .GroupBy(
                    file => file.SourceRelativePath,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => new DocumentGroupViewModel(
                    group.Key,
                    group.ToArray()))
                .ToArray();
            localLoadTimer.Stop();
            var blankFilterNotifications = 0;
            NotifyCollectionChangedEventHandler blankFilterHandler =
                (_, _) => blankFilterNotifications++;
            foreach (var group in groups)
            {
                group.VisibleItems.CollectionChanged += blankFilterHandler;
            }

            var workshopFilterTimer = Stopwatch.StartNew();
            var workshopGroups = groups
                .Where(group =>
                    group.LibraryId == "ccc-gas" &&
                    group.CategoryId == "workshop" &&
                    group.ApplySearch(string.Empty))
                .ToArray();
            workshopFilterTimer.Stop();
            foreach (var group in groups)
            {
                group.VisibleItems.CollectionChanged -= blankFilterHandler;
            }

            var blankFilterStable =
                workshopGroups.Length > 0 &&
                blankFilterNotifications == 0;
            var ledger = groups.FirstOrDefault(group =>
                group.Title.Contains(
                    "原材料出入库台账",
                    StringComparison.OrdinalIgnoreCase));
            var singleItemGroup = groups.FirstOrDefault(group =>
                group.IsSingleItem);
            if (singleItemGroup is not null)
            {
                singleItemGroup.IsExpanded = true;
            }

            if (ledger is not null)
            {
                ledger.IsExpanded = true;
            }

            var groupPresentation =
                singleItemGroup is
                {
                    IsSingleItem: true,
                    HasMultipleItems: false,
                    IsExpanded: false
                } &&
                singleItemGroup.Title == Path.GetFileNameWithoutExtension(
                    singleItemGroup.SourceRelativePath) &&
                singleItemGroup.SingleItem.Name == singleItemGroup.Title &&
                ledger is
                {
                    IsSingleItem: false,
                    HasMultipleItems: true,
                    IsExpanded: true
                };
            var printService = new WindowsPdfPrintService();
            var samplePdf = manifest?.Files
                .Select(file => store.GetPdfPath(manifest, file))
                .FirstOrDefault(path => path is not null);
            var chineseSearch = groups.Any(group =>
                DocumentSearch.IsMatch(group.SearchText, "原材料"));
            var pinyinSearch = groups.Any(group =>
                DocumentSearch.IsMatch(group.SearchText, "yuancailiao"));
            var initialSearch = groups.Any(group =>
                DocumentSearch.IsMatch(group.SearchText, "ycl"));
            var similarSearch = groups.Any(group =>
                DocumentSearch.IsMatch(group.SearchText, "原材枓"));
            var worksheetSearch =
                ledger?.ApplySearch("铁锅") == true &&
                ledger.VisibleItems.Any(item =>
                    item.Name.Contains(
                        "铁锅",
                        StringComparison.OrdinalIgnoreCase)) &&
                ledger.IsExpanded;
            var workshopClassification =
                DocumentSearch.GetWorkshopCategoryId(
                    @"CCC燃气资料\生产部\全预混资料\样本.docx") == "premix" &&
                DocumentSearch.GetWorkshopCategoryId(
                    @"CCC燃气资料\生产部\自吸风资料\样本.docx") == "self-draft" &&
                DocumentSearch.GetWorkshopCategoryId(
                    @"CCC燃气资料\生产部\燃烧机资料\样本.docx") == "burner" &&
                DocumentSearch.GetWorkshopCategoryId(
                    @"CCC燃气资料\生产部\炉头工艺卡\样本.docx") ==
                "burner-process" &&
                DocumentSearch.GetWorkshopCategoryId(
                    @"CCC燃气资料\生产部\炉头作业指导书\样本.docx") ==
                "burner-guide" &&
                DocumentSearch.GetWorkshopCategoryId(
                    @"CCC燃气资料\生产部\01零部件进货检验.doc") == "other";
            DocumentGroupViewModel CreateTestGroup(
                string path,
                string libraryId = "ccc-gas") =>
                new(
                    path,
                    [new LibrarySyncFile(
                        "group-test",
                        libraryId,
                        libraryId,
                        path,
                        Path.GetFileNameWithoutExtension(path),
                        null,
                        null,
                        1,
                        1,
                        "group-test",
                        false,
                        string.Empty)]);
            var subgroupClassification =
                CreateTestGroup(
                    @"CCC燃气资料\普通资料\CCC规则\规则.pdf")
                    .CustomGroupName == "CCC规则" &&
                CreateTestGroup(
                    @"CCC燃气资料\普通资料\CCC证书\证书.pdf")
                    .CustomGroupName == "CCC证书" &&
                CreateTestGroup(
                    @"CCC燃气资料\普通资料\检测报告\报告.pdf")
                    .CustomGroupName == "检测报告" &&
                CreateTestGroup(
                    @"CCC燃气资料\仓库\年度台账\台账.xlsx")
                    .CustomGroupName == "年度台账" &&
                CreateTestGroup(
                    @"CCC燃气资料\财务\供应商资料\供应商.docx")
                    .CustomGroupName == "供应商资料" &&
                CreateTestGroup(
                    @"电加热资料\车间\检验记录\检验.docx",
                    "electric-heating")
                    .CustomGroupName == "检验记录" &&
                CreateTestGroup(
                    @"CCC燃气资料\普通资料\直接资料.pdf")
                    .CustomGroupName is null &&
                CreateTestGroup(
                    @"CCC燃气资料\生产部\全预混资料\样本.docx")
                    .CustomGroupName == "全预混" &&
                CreateTestGroup(
                    @"CCC燃气资料\车间\自吸风制造工艺卡.doc")
                    .CustomGroupName == "自吸风" &&
                CreateTestGroup(
                    @"CCC燃气资料\b生产部（车间）\工艺卡\全预混摇摆汤锅-焊接工艺卡.doc")
                    .CustomGroupName == "全预混" &&
                CreateTestGroup(
                    @"CCC燃气资料\b生产部（车间）\工艺卡\燃烧机摇摆汤锅制造工艺卡.docx")
                    .CustomGroupName == "燃烧机" &&
                CreateTestGroup(
                    @"CCC燃气资料\b生产部（车间）\工艺卡\通用工艺卡.docx")
                    .CustomGroupName == "工艺卡" &&
                CreateTestGroup(
                    @"CCC燃气资料\车间\定制工艺\样本.docx")
                    .CustomGroupName == "定制工艺";
            var governmentClassification =
                CreateTestGroup(
                    @"政府资料\应急管理\危化品\通知.pdf",
                    "government") is
                {
                    CategoryId: "emergency",
                    CustomGroupName: "危化品"
                } &&
                CreateTestGroup(
                    @"政府资料\安全监管\检查\检查表.docx",
                    "government").CategoryId == "safety" &&
                CreateTestGroup(
                    @"政府资料\政策法规\安全生产法.pdf",
                    "government").CategoryId == "policy" &&
                CreateTestGroup(
                    @"政府资料\其他资料\消防\通知.pdf",
                    "government").CategoryId == "other";
            var subcategoryOrdering =
                SubcategoryOrderStore.ApplyOrder(
                    ["CCC规则", "CCC证书", "检测报告", "未分组"],
                    ["检测报告", "CCC规则"])
                    .SequenceEqual(
                        ["检测报告", "CCC规则", "CCC证书", "未分组"]) &&
                SubcategoryOrderStore.Move(
                    ["CCC规则", "CCC证书", "检测报告"],
                    "检测报告",
                    "CCC规则",
                    false)
                    .SequenceEqual(
                        ["检测报告", "CCC规则", "CCC证书"]) &&
                SubcategoryOrderStore.Move(
                    ["CCC规则", "CCC证书", "检测报告"],
                    "CCC规则",
                    "检测报告",
                    true)
                    .SequenceEqual(
                        ["CCC证书", "检测报告", "CCC规则"]);
            var printSubmissions =
                new List<(DateTimeOffset SubmittedAt, string PdfPath, int Copies)>();
            var throttledPrintService = new WindowsPdfPrintService(
                (pdfPath, copies) =>
                {
                    printSubmissions.Add((
                        DateTimeOffset.UtcNow,
                        pdfPath,
                        copies));
                },
                TimeSpan.FromSeconds(3));
            var printQueueTimer = Stopwatch.StartNew();
            var dryRunSubmitted = samplePdf is null
                ? 0
                : await throttledPrintService.PrintAsync(
                    [(samplePdf, 8), (samplePdf, 1)]);
            printQueueTimer.Stop();
            var minimumPrintIntervalMs = printSubmissions
                .Zip(
                    printSubmissions.Skip(1),
                    (previous, current) =>
                        (current.SubmittedAt - previous.SubmittedAt)
                            .TotalMilliseconds)
                .DefaultIfEmpty(0)
                .Min();
            var directPrintQueueValid =
                dryRunSubmitted == 2 &&
                printSubmissions.Count == 2 &&
                minimumPrintIntervalMs >= 2900 &&
                printSubmissions[0].PdfPath == samplePdf &&
                printSubmissions[0].Copies == 8 &&
                printSubmissions[1].PdfPath == samplePdf &&
                printSubmissions[1].Copies == 1;
            var pdfiumRenderSucceeded =
                samplePdf is not null && printService.CanRender(samplePdf);
            var success =
                manifest is not null &&
                groups.Length > 0 &&
                ledger is not null &&
                ledger.SheetCount > 1 &&
                chineseSearch &&
                pinyinSearch &&
                initialSearch &&
                similarSearch &&
                worksheetSearch &&
                groupPresentation &&
                workshopClassification &&
                subgroupClassification &&
                governmentClassification &&
                subcategoryOrdering &&
                blankFilterStable &&
                directPrintQueueValid &&
                pdfiumRenderSucceeded;

            await WriteTestReportAsync(
                outputPath,
                new
                {
                    success,
                    manifest?.Version,
                    manifest?.IsTestData,
                    Groups = groups.Length,
                    Sheets = manifest?.Files.Count ?? 0,
                    LedgerGroup = ledger?.Title,
                    LedgerSheets = ledger?.SheetCount ?? 0,
                    GroupPresentation = new
                    {
                        SingleGroup = singleItemGroup?.Title,
                        SingleGroupExpanded = singleItemGroup?.IsExpanded,
                        MultipleGroup = ledger?.Title,
                        MultipleGroupExpanded = ledger?.IsExpanded,
                        groupPresentation
                    },
                    Search = new
                    {
                        chineseSearch,
                        pinyinSearch,
                        initialSearch,
                        similarSearch,
                        worksheetSearch
                    },
                    WorkshopClassification = workshopClassification,
                    SubgroupClassification = subgroupClassification,
                    GovernmentClassification = governmentClassification,
                    SubcategoryOrdering = subcategoryOrdering,
                    WorkshopFilter = new
                    {
                        Groups = workshopGroups.Length,
                        ElapsedMilliseconds =
                            workshopFilterTimer.ElapsedMilliseconds,
                        CollectionNotifications =
                            blankFilterNotifications,
                        blankFilterStable
                    },
                    LocalCatalogLoadMilliseconds =
                        localLoadTimer.ElapsedMilliseconds,
                    PrintQueue = new
                    {
                        DryRun = true,
                        PhysicalPrintSubmitted = false,
                        Submitted = dryRunSubmitted,
                        RequestedDocuments = 2,
                        RequestedCopies = 9,
                        FirstSubmissionCopies = printSubmissions
                            .FirstOrDefault().Copies,
                        SecondSubmissionCopies = printSubmissions
                            .Skip(1).FirstOrDefault().Copies,
                        WpsProcessLaunched = false,
                        TemporaryPdfCreated = false,
                        MinimumIntervalMilliseconds =
                            minimumPrintIntervalMs,
                        ElapsedMilliseconds =
                            printQueueTimer.ElapsedMilliseconds,
                        directPrintQueueValid
                    },
                    DefaultPrinter = printService.GetDefaultPrinterName(),
                    PdfiumRenderSucceeded = pdfiumRenderSucceeded,
                    PhysicalPrintSubmitted = false,
                    CheckedAt = DateTimeOffset.Now
                });
            return success ? 0 : 1;
        }
        catch (Exception exception)
        {
            ClientLog.Write("资料分组测试失败", exception);
            await WriteTestReportAsync(
                outputPath,
                new
                {
                    success = false,
                    error = exception.GetBaseException().Message
                });
            return 1;
        }
    }

    private static void OnCurrentDomainUnhandledException(
        object sender,
        UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            ClientLog.Write("AppDomain 未处理异常", exception);
        }
    }

    private static void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        ClientLog.Write("Task 未观察异常", e.Exception);
        e.SetObserved();
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        ClientLog.Write("WPF 未处理异常", e.Exception);
        e.Handled = true;
        MessageBox.Show(
            "程序遇到异常，但已阻止自动退出。\n\n" +
            $"诊断日志：{ClientLog.LogDirectory}",
            "CCC 资料打印端",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
