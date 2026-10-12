using System.Windows;
using System.Reflection;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CccPrint.Configuration;
using CccPrint.Models;
using CccPrint.Networking;
using CccPrint.Services;

namespace CccPrint;

public partial class App : Application
{
    private const string SingleInstanceMutexName =
        @"Local\CCCPrint.Public.Admin.SingleInstance";
    private const string ActivationEventName =
        @"Local\CCCPrint.Public.Admin.Activate";
    private UpdateServer? _server;
    private DiscoveryResponder? _discoveryResponder;
    private MainWindow? _mainWindow;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activationEvent;
    private volatile bool _shuttingDown;

    public App()
    {
        EnsureWindowsDirectoryEnvironment();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        EnsureWindowsDirectoryEnvironment();
        base.OnStartup(e);

        var options = AppOptions.Load(AppContext.BaseDirectory);
        var catalog = new LibraryCatalogService(options.SourcePath);
        var smokeOutputArgument = e.Args.FirstOrDefault(argument =>
            argument.StartsWith(
                "--management-smoke-output=",
                StringComparison.OrdinalIgnoreCase));
        var operationsSmokeArgument = e.Args.FirstOrDefault(argument =>
            argument.StartsWith(
                "--library-operations-smoke-output=",
                StringComparison.OrdinalIgnoreCase));
        if (operationsSmokeArgument is not null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var exitCode = await RunLibraryOperationsSmokeTestAsync(
                operationsSmokeArgument.Split('=', 2)[1]);
            Shutdown(exitCode);
            return;
        }

        if (smokeOutputArgument is not null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var exitCode = await RunManagementSmokeTestAsync(
                options,
                smokeOutputArgument.Split('=', 2)[1]);
            Shutdown(exitCode);
            return;
        }

        var serverOnly = e.Args.Any(arg =>
            string.Equals(arg, "--server-only", StringComparison.OrdinalIgnoreCase));
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

        if (serverOnly)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }
        else
        {
            _mainWindow = new MainWindow(options, catalog);
            MainWindow = _mainWindow;
            _mainWindow.Closed += (_, _) => Shutdown();
            _mainWindow.Show();
        }

        if (!options.IsAdminMode && !serverOnly)
        {
            _mainWindow?.SetServerStatus(
                isRunning: false,
                $"打印端 · 更新源 {options.ServerAddress}");
            return;
        }

        _server = new UpdateServer(options, catalog);

        try
        {
            await _server.StartAsync();

            var appVersion =
                Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
                ?? "0.6.29";
            _discoveryResponder = new DiscoveryResponder(
                options.DiscoveryPort,
                Environment.MachineName,
                options.ServerPort,
                appVersion);
            await _discoveryResponder.StartAsync();

            _mainWindow?.SetServerStatus(
                isRunning: true,
                $"管理端服务运行中 · {options.ServerAddress}");
        }
        catch (Exception ex)
        {
            _mainWindow?.SetServerStatus(
                isRunning: false,
                $"服务启动失败 · {ex.Message}");

            if (serverOnly)
            {
                Shutdown(-1);
            }
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
        if (_mainWindow is null)
        {
            return;
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Show();
        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    private static async Task<int> RunLibraryOperationsSmokeTestAsync(
        string outputPath)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"ccc-library-smoke-{Guid.NewGuid():N}");
        try
        {
            var sourceRoot = Path.Combine(root, "source");
            var dataRoot = Path.Combine(root, "data");
            var publishedRoot = Path.Combine(dataRoot, "published");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(publishedRoot);
            var jsonOptions = new JsonSerializerOptions(
                JsonSerializerDefaults.Web)
            {
                WriteIndented = true
            };
            var oldFile = new LibrarySyncFile(
                "old-pdf",
                "ccc-gas",
                "CCC 燃气资料库",
                "CCC燃气资料/旧资料.xlsx",
                "旧资料 - 子表1",
                "子表1",
                1,
                1,
                3,
                "abc",
                false,
                "/old");
            var oldManifest = new LibrarySyncManifest(
                1,
                "old-version",
                false,
                DateTimeOffset.UtcNow,
                "旧版本",
                new[] { oldFile });
            await File.WriteAllTextAsync(
                Path.Combine(publishedRoot, "current-manifest.json"),
                JsonSerializer.Serialize(oldManifest, jsonOptions));

            var removeBatch = new DraftBatch(
                1,
                "remove-batch",
                "pending-publish",
                DateTimeOffset.UtcNow,
                1,
                0,
                0,
                0,
                0,
                Array.Empty<DraftSourceItem>())
            {
                RemovedSourcePaths =
                    new[] { "CCC燃气资料/旧资料.xlsx" }
            };
            Directory.CreateDirectory(
                Path.Combine(dataRoot, "drafts", removeBatch.BatchId));
            var service = new PublishedLibraryService(
                dataRoot,
                sourceRoot);
            await service.PublishAsync(
                removeBatch,
                new DraftReviewState(
                    1,
                    removeBatch.BatchId,
                    "删除旧资料",
                    DateTimeOffset.UtcNow,
                    Array.Empty<DraftOutputReview>()));
            var removedManifest = await service.GetCurrentManifestAsync();

            var sourceRelativePath = "CCC燃气资料/旧资料.xlsx";
            var sourcePath = Path.Combine(
                sourceRoot,
                sourceRelativePath.Replace(
                    '/',
                    Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            var sourceBytes = Encoding.UTF8.GetBytes("source-v2");
            await File.WriteAllBytesAsync(sourcePath, sourceBytes);
            var sourceHash = Convert.ToHexString(
                    SHA256.HashData(sourceBytes))
                .ToLowerInvariant();
            var pdfBytes = Encoding.ASCII.GetBytes("%PDF-smoke");
            var pdfHash = Convert.ToHexString(
                    SHA256.HashData(pdfBytes))
                .ToLowerInvariant();
            var draftRelativePath = "drafts/add-batch/new.pdf";
            var draftPath = Path.Combine(
                dataRoot,
                draftRelativePath.Replace(
                    '/',
                    Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(draftPath)!);
            await File.WriteAllBytesAsync(draftPath, pdfBytes);
            var secondPdfBytes = Encoding.ASCII.GetBytes("%PDF-old-sheet2");
            var secondPdfHash = Convert.ToHexString(
                    SHA256.HashData(secondPdfBytes))
                .ToLowerInvariant();
            var secondDraftRelativePath = "drafts/add-batch/second.pdf";
            var secondDraftPath = Path.Combine(
                dataRoot,
                secondDraftRelativePath.Replace(
                    '/',
                    Path.DirectorySeparatorChar));
            await File.WriteAllBytesAsync(secondDraftPath, secondPdfBytes);
            var output = new PdfDraftOutput(
                "new-pdf",
                "旧资料 - 子表1",
                "子表1",
                1,
                draftRelativePath,
                pdfBytes.Length,
                pdfHash,
                1,
                false,
                false,
                Array.Empty<string>());
            var secondOutput = new PdfDraftOutput(
                "second-pdf",
                "旧资料 - 子表2",
                "子表2",
                2,
                secondDraftRelativePath,
                secondPdfBytes.Length,
                secondPdfHash,
                1,
                false,
                false,
                Array.Empty<string>());
            var addBatch = new DraftBatch(
                2,
                "add-batch",
                "pending-publish",
                DateTimeOffset.UtcNow,
                1,
                1,
                0,
                1,
                0,
                new[]
                {
                    new DraftSourceItem(
                        "source",
                        sourceRelativePath,
                        ".xlsx",
                        sourceBytes.Length,
                        sourceHash,
                        true,
                        1,
                        new[] { output, secondOutput },
                        null)
                });
            await service.PublishAsync(
                addBatch,
                new DraftReviewState(
                    1,
                    addBatch.BatchId,
                    "重新加入资料",
                    DateTimeOffset.UtcNow,
                    Array.Empty<DraftOutputReview>()));
            var restoredManifest = await service.GetCurrentManifestAsync();
            var updatedPdfBytes = Encoding.ASCII.GetBytes("%PDF-new-sheet1");
            var updatedPdfHash = Convert.ToHexString(
                    SHA256.HashData(updatedPdfBytes))
                .ToLowerInvariant();
            var partialDraftRelativePath =
                "drafts/partial-batch/updated.pdf";
            var partialDraftPath = Path.Combine(
                dataRoot,
                partialDraftRelativePath.Replace(
                    '/',
                    Path.DirectorySeparatorChar));
            Directory.CreateDirectory(
                Path.GetDirectoryName(partialDraftPath)!);
            await File.WriteAllBytesAsync(partialDraftPath, updatedPdfBytes);
            var partialOutput = output with
            {
                RelativePdfPath = partialDraftRelativePath,
                Bytes = updatedPdfBytes.Length,
                Sha256 = updatedPdfHash
            };
            var partialBatch = addBatch with
            {
                BatchId = "partial-batch",
                PdfCount = 2,
                Sources = new[]
                {
                    addBatch.Sources[0] with
                    {
                        Outputs = new[] { partialOutput, secondOutput }
                    }
                }
            };
            await service.PublishAsync(
                partialBatch,
                new DraftReviewState(
                    1,
                    partialBatch.BatchId,
                    "只替换子表1",
                    DateTimeOffset.UtcNow,
                    new[]
                    {
                        new DraftOutputReview(
                            secondOutput.OutputId,
                            ReviewDecisions.Rejected,
                            string.Empty,
                            DateTimeOffset.UtcNow)
                    }));
            var partialManifest = await service.GetCurrentManifestAsync();
            var governmentSourceRelativePath =
                "政府资料/应急管理/危化品/应急通知.docx";
            var governmentSourcePath = Path.Combine(
                sourceRoot,
                governmentSourceRelativePath.Replace(
                    '/',
                    Path.DirectorySeparatorChar));
            Directory.CreateDirectory(
                Path.GetDirectoryName(governmentSourcePath)!);
            var governmentSourceBytes =
                Encoding.UTF8.GetBytes("government-source");
            await File.WriteAllBytesAsync(
                governmentSourcePath,
                governmentSourceBytes);
            var governmentCatalog = new LibraryCatalogService(sourceRoot)
                .Scan()
                .Single(library => library.Id == "government");
            var governmentBaseline = await new SourceBaselineService(
                    sourceRoot,
                    Path.Combine(root, "baseline-data"))
                .EnsureInitializedAsync();
            var governmentSourceDiscovered =
                governmentCatalog.IsAvailable &&
                governmentCatalog.SourceFileCount == 1 &&
                governmentBaseline.Files.Any(file =>
                    file.RelativePath == governmentSourceRelativePath &&
                    file.LibraryId == "government" &&
                    file.LibraryName == "政府资料");
            var governmentSourceHash = Convert.ToHexString(
                    SHA256.HashData(governmentSourceBytes))
                .ToLowerInvariant();
            var governmentPdfBytes =
                Encoding.ASCII.GetBytes("%PDF-government");
            var governmentPdfHash = Convert.ToHexString(
                    SHA256.HashData(governmentPdfBytes))
                .ToLowerInvariant();
            var governmentDraftRelativePath =
                "drafts/government-batch/government.pdf";
            var governmentDraftPath = Path.Combine(
                dataRoot,
                governmentDraftRelativePath.Replace(
                    '/',
                    Path.DirectorySeparatorChar));
            Directory.CreateDirectory(
                Path.GetDirectoryName(governmentDraftPath)!);
            await File.WriteAllBytesAsync(
                governmentDraftPath,
                governmentPdfBytes);
            var governmentOutput = new PdfDraftOutput(
                "government-pdf",
                "应急通知",
                null,
                null,
                governmentDraftRelativePath,
                governmentPdfBytes.Length,
                governmentPdfHash,
                1,
                false,
                false,
                Array.Empty<string>());
            var governmentBatch = new DraftBatch(
                2,
                "government-batch",
                "pending-publish",
                DateTimeOffset.UtcNow,
                1,
                1,
                0,
                1,
                0,
                new[]
                {
                    new DraftSourceItem(
                        "government-source",
                        governmentSourceRelativePath,
                        ".docx",
                        governmentSourceBytes.Length,
                        governmentSourceHash,
                        true,
                        1,
                        new[] { governmentOutput },
                        null)
                });
            await service.PublishAsync(
                governmentBatch,
                new DraftReviewState(
                    1,
                    governmentBatch.BatchId,
                    "新增政府资料",
                    DateTimeOffset.UtcNow,
                    Array.Empty<DraftOutputReview>()));
            var governmentManifest = await service.GetCurrentManifestAsync();
            var governmentLibraryMapped =
                governmentManifest.Files.Any(file =>
                    file.SourceRelativePath == governmentSourceRelativePath &&
                    file.LibraryId == "government" &&
                    file.LibraryName == "政府资料库");
            var publishedGroupAvailableWithoutSourceDirectory =
                global::CccPrint.MainWindow.BuildCustomGroupOptions(
                        Array.Empty<string>(),
                        governmentManifest,
                        "政府资料",
                        "应急管理")
                    .Contains(
                        "危化品",
                        StringComparer.OrdinalIgnoreCase);
            var retentionDataRoot = Path.Combine(root, "retention-data");
            var retentionVersionsRoot = Path.Combine(
                retentionDataRoot,
                "published",
                "versions");
            Directory.CreateDirectory(retentionVersionsRoot);
            var retentionBaseTime = new DateTimeOffset(
                2030,
                12,
                15,
                12,
                0,
                0,
                TimeSpan.Zero);
            for (var index = 0; index < 50; index++)
            {
                await WriteRetentionVersionAsync(
                    retentionVersionsRoot,
                    $"recent-{index:D2}",
                    retentionBaseTime.AddMinutes(-index),
                    jsonOptions);
            }

            for (var month = 1; month <= 15; month++)
            {
                await WriteRetentionVersionAsync(
                    retentionVersionsRoot,
                    $"monthly-{month:D2}",
                    retentionBaseTime.AddMonths(-month),
                    jsonOptions);
            }

            var retentionService = new PublishedLibraryService(
                retentionDataRoot,
                sourceRoot);
            var prunedVersionCount =
                await retentionService.ApplyRetentionPolicyAsync("recent-00");
            var retainedHistory = await retentionService.GetHistoryAsync();
            var recentVersionsPreserved = Enumerable
                .Range(0, PublishedLibraryService.RecentVersionRetentionCount)
                .All(index => retainedHistory.Any(manifest =>
                    manifest.Version == $"recent-{index:D2}"));
            var retentionSucceeded =
                retainedHistory.Count == 41 &&
                prunedVersionCount == 24 &&
                recentVersionsPreserved &&
                retainedHistory.Any(manifest =>
                    manifest.Version == "monthly-11") &&
                retainedHistory.All(manifest =>
                    manifest.Version != "monthly-12");
            var success =
                removedManifest.Files.Count == 0 &&
                removedManifest.RemovedSourcePaths.Contains(
                    sourceRelativePath,
                    StringComparer.OrdinalIgnoreCase) &&
                restoredManifest.Files.Count == 2 &&
                restoredManifest.RemovedSourcePaths.Count == 0 &&
                partialManifest.Files.Count == 2 &&
                partialManifest.Files.Single(file =>
                    file.WorksheetName == "子表1").Sha256 == updatedPdfHash &&
                partialManifest.Files.Single(file =>
                    file.WorksheetName == "子表2").Sha256 == secondPdfHash &&
                governmentManifest.Files.Count == 3 &&
                governmentLibraryMapped &&
                governmentSourceDiscovered &&
                publishedGroupAvailableWithoutSourceDirectory &&
                retentionSucceeded;
            await WriteSmokeReportAsync(
                outputPath,
                new
                {
                    success,
                    RemovedFiles = removedManifest.Files.Count,
                    Tombstones = removedManifest.RemovedSourcePaths.Count,
                    RestoredFiles = restoredManifest.Files.Count,
                    RestoredTombstones =
                        restoredManifest.RemovedSourcePaths.Count,
                    PartialFiles = partialManifest.Files.Count,
                    SelectedSheetUpdated = partialManifest.Files.Single(file =>
                        file.WorksheetName == "子表1").Sha256 == updatedPdfHash,
                    UnselectedSheetPreserved = partialManifest.Files.Single(file =>
                        file.WorksheetName == "子表2").Sha256 == secondPdfHash,
                    GovernmentFiles = governmentManifest.Files.Count(file =>
                        file.LibraryId == "government"),
                    GovernmentLibraryMapped = governmentLibraryMapped,
                    GovernmentSourceDiscovered =
                        governmentSourceDiscovered,
                    PublishedGroupAvailableWithoutSourceDirectory =
                        publishedGroupAvailableWithoutSourceDirectory,
                    RetainedVersions = retainedHistory.Count,
                    PrunedVersions = prunedVersionCount,
                    RecentVersionsPreserved = recentVersionsPreserved
                });
            return success ? 0 : 1;
        }
        catch (Exception exception)
        {
            await WriteSmokeReportAsync(
                outputPath,
                new
                {
                    success = false,
                    error = exception.GetBaseException().Message
                });
            return 1;
        }
        finally
        {
            var tempPrefix =
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(Path.GetTempPath())) +
                Path.DirectorySeparatorChar;
            var fullRoot = Path.GetFullPath(root);
            if (fullRoot.StartsWith(
                    tempPrefix,
                    StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(fullRoot))
            {
                Directory.Delete(fullRoot, true);
            }
        }
    }

    private static async Task WriteRetentionVersionAsync(
        string versionsRoot,
        string version,
        DateTimeOffset generatedAtUtc,
        JsonSerializerOptions jsonOptions)
    {
        var versionRoot = Path.Combine(versionsRoot, version);
        Directory.CreateDirectory(versionRoot);
        var manifest = new LibrarySyncManifest(
            1,
            version,
            false,
            generatedAtUtc,
            version,
            Array.Empty<LibrarySyncFile>());
        await File.WriteAllTextAsync(
            Path.Combine(versionRoot, "manifest.json"),
            JsonSerializer.Serialize(manifest, jsonOptions));
    }

    private static async Task WriteSmokeReportAsync(
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

    private static async Task<int> RunManagementSmokeTestAsync(
        AppOptions options,
        string outputPath)
    {
        try
        {
            var drafts = new DraftPreparationService(
                options.SourcePath,
                options.DataRoot);
            var reviews = new DraftReviewService(options.DataRoot);
            var published = new PublishedLibraryService(
                options.DataRoot,
                options.SourcePath);
            var batches = await drafts.GetPendingAsync();
            var selected = batches.FirstOrDefault();
            var review = selected is null
                ? null
                : await reviews.GetAsync(selected);
            var current = await published.GetCurrentManifestAsync();
            var testPublishBlocked = false;
            var testBatch = batches.FirstOrDefault(batch =>
                batch.Status.Equals(
                    "test-only-do-not-publish",
                    StringComparison.OrdinalIgnoreCase));
            if (testBatch is not null)
            {
                try
                {
                    await published.PublishAsync(
                        testBatch,
                        await reviews.GetAsync(testBatch));
                }
                catch (InvalidOperationException)
                {
                    testPublishBlocked = true;
                }
            }

            var report = new
            {
                success = selected is null ||
                          review?.Items.Count == selected.PdfCount,
                BatchCount = batches.Count,
                SelectedBatchId = selected?.BatchId,
                ReviewItemCount = review?.Items.Count ?? 0,
                CurrentVersion = current.Version,
                CurrentFiles = current.Files.Count,
                TestPublishBlocked = testBatch is null || testPublishBlocked,
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
            return report.success && report.TestPublishBlocked ? 0 : 1;
        }
        catch (Exception exception)
        {
            var fullOutputPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(fullOutputPath)
                ?? AppContext.BaseDirectory);
            await File.WriteAllTextAsync(
                fullOutputPath,
                JsonSerializer.Serialize(
                    new
                    {
                        success = false,
                        error = exception.GetBaseException().Message
                    },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    }));
            return 1;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shuttingDown = true;
        _activationEvent?.Set();
        using var stopTimeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(4));
        try
        {
            Task.Run(() => StopServicesAsync(stopTimeout.Token))
                .WaitAsync(TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException)
        {
            // 进程退出会释放剩余监听资源。
        }
        catch (TimeoutException)
        {
            // 停服异常时也不能无限阻塞窗口退出。
        }

        _activationEvent?.Dispose();
        if (_singleInstanceMutex is not null)
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
        }

        base.OnExit(e);
    }

    private async Task StopServicesAsync(CancellationToken cancellationToken)
    {
        if (_discoveryResponder is not null)
        {
            await _discoveryResponder.StopAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (_server is not null)
        {
            await _server.StopAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
