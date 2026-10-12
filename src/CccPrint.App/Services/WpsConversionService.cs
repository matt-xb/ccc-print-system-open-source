using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using CccPrint.Models;

namespace CccPrint.Services;

public sealed class WpsConversionService
{
    private static readonly HashSet<string> WpsProcessNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "wps",
            "et",
            "wpsoffice"
        };

    private readonly SemaphoreSlim _conversionGate = new(1, 1);

    public async Task<IReadOnlyList<WpsGeneratedPdf>> ConvertAsync(
        string inputPath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var visibleProcesses = FindVisibleWpsProcesses();
        if (visibleProcesses.Count > 0)
        {
            throw new WpsBusyException(
                $"检测到正在使用的 WPS 窗口：{string.Join("、", visibleProcesses)}。" +
                "请保存并关闭可见的 WPS 文档后再转换。");
        }

        await _conversionGate.WaitAsync(cancellationToken);
        try
        {
            return await RunOnStaThreadAsync(inputPath, outputDirectory);
        }
        finally
        {
            _conversionGate.Release();
        }
    }

    public static IReadOnlyList<string> FindVisibleWpsProcesses()
    {
        var matches = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (WpsProcessNames.Contains(process.ProcessName) &&
                        process.MainWindowHandle != IntPtr.Zero)
                    {
                        matches.Add($"{process.ProcessName} ({process.Id})");
                    }
                }
                catch (InvalidOperationException)
                {
                    // 进程在枚举过程中退出。
                }
            }
        }

        return matches;
    }

    private static Task<IReadOnlyList<WpsGeneratedPdf>> RunOnStaThreadAsync(
        string inputPath,
        string outputDirectory)
    {
        var completion =
            new TaskCompletionSource<IReadOnlyList<WpsGeneratedPdf>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(ConvertCore(inputPath, outputDirectory));
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "CCC-WPS-PDF-Converter"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static IReadOnlyList<WpsGeneratedPdf> ConvertCore(
        string inputPath,
        string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var extension = Path.GetExtension(inputPath);

        if (extension.Equals(".doc", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            return ConvertWriterDocument(inputPath, outputDirectory);
        }

        if (extension.Equals(".xls", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return ConvertSpreadsheetSheets(inputPath, outputDirectory);
        }

        throw new NotSupportedException($"不支持转换此文件类型：{extension}");
    }

    private static IReadOnlyList<WpsGeneratedPdf> ConvertWriterDocument(
        string inputPath,
        string outputDirectory)
    {
        object? applicationObject = null;
        object? documentsObject = null;
        object? documentObject = null;
        var outputPath = Path.Combine(
            outputDirectory,
            $"{SanitizeFileName(Path.GetFileNameWithoutExtension(inputPath))}.pdf");

        try
        {
            var applicationType = Type.GetTypeFromProgID(
                "KWPS.Application",
                throwOnError: true)!;
            applicationObject = Activator.CreateInstance(applicationType)
                ?? throw new InvalidOperationException("无法启动 WPS Writer。");

            dynamic application = applicationObject;
            application.Visible = false;
            application.DisplayAlerts = 0;

            documentsObject = application.Documents;
            dynamic documents = documentsObject;
            documentObject = documents.Open(inputPath, false, true);

            dynamic document = documentObject;
            document.ExportAsFixedFormat(outputPath, 17);
            document.Close(false);
            documentObject = null;

            application.Quit();
            applicationObject = null;

            RequireNonEmptyFile(outputPath);
            return
            [
                new WpsGeneratedPdf(
                    outputPath,
                    Path.GetFileNameWithoutExtension(inputPath),
                    null,
                    null)
            ];
        }
        finally
        {
            TryClose(documentObject);
            TryQuit(applicationObject);
            ReleaseComObject(documentObject);
            ReleaseComObject(documentsObject);
            ReleaseComObject(applicationObject);
            ForceComCleanup();
        }
    }

    private static IReadOnlyList<WpsGeneratedPdf> ConvertSpreadsheetSheets(
        string inputPath,
        string outputDirectory)
    {
        object? applicationObject = null;
        object? workbooksObject = null;
        object? workbookObject = null;
        object? worksheetsObject = null;
        var outputs = new List<WpsGeneratedPdf>();

        try
        {
            var applicationType = Type.GetTypeFromProgID(
                "KET.Application",
                throwOnError: true)!;
            applicationObject = Activator.CreateInstance(applicationType)
                ?? throw new InvalidOperationException("无法启动 WPS 表格。");

            dynamic application = applicationObject;
            application.Visible = false;
            application.DisplayAlerts = false;
            application.ScreenUpdating = false;

            workbooksObject = application.Workbooks;
            dynamic workbooks = workbooksObject;
            workbookObject = workbooks.Open(inputPath, 0, true);
            dynamic workbook = workbookObject;

            worksheetsObject = workbook.Worksheets;
            dynamic worksheets = worksheetsObject;
            var worksheetCount = (int)worksheets.Count;

            for (var worksheetIndex = 1;
                 worksheetIndex <= worksheetCount;
                 worksheetIndex++)
            {
                object? worksheetObject = null;
                try
                {
                    worksheetObject = worksheets.Item(worksheetIndex);
                    dynamic worksheet = worksheetObject;
                    if (!IsWorksheetVisible(worksheet.Visible))
                    {
                        continue;
                    }

                    var worksheetName = (string)worksheet.Name;
                    var outputPath = Path.Combine(
                        outputDirectory,
                        $"{worksheetIndex:000}-" +
                        $"{SanitizeFileName(worksheetName)}.pdf");
                    worksheet.ExportAsFixedFormat(0, outputPath);
                    RequireNonEmptyFile(outputPath);
                    outputs.Add(new WpsGeneratedPdf(
                        outputPath,
                        worksheetName,
                        worksheetName,
                        worksheetIndex));
                }
                finally
                {
                    ReleaseComObject(worksheetObject);
                }
            }

            workbook.Close(false);
            workbookObject = null;

            application.Quit();
            applicationObject = null;

            if (outputs.Count == 0)
            {
                throw new InvalidOperationException(
                    "WPS 没有找到可转换的可见工作表。");
            }

            return outputs;
        }
        finally
        {
            TryClose(workbookObject);
            TryQuit(applicationObject);
            ReleaseComObject(worksheetsObject);
            ReleaseComObject(workbookObject);
            ReleaseComObject(workbooksObject);
            ReleaseComObject(applicationObject);
            ForceComCleanup();
        }
    }

    private static bool IsWorksheetVisible(object visibility) =>
        visibility is bool visible
            ? visible
            : Convert.ToInt32(visibility) == -1;

    private static void RequireNonEmptyFile(string outputPath)
    {
        var file = new FileInfo(outputPath);
        if (!file.Exists || file.Length == 0)
        {
            throw new InvalidOperationException(
                $"WPS 未生成有效 PDF：{outputPath}");
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(
                value.Select(character =>
                        invalidCharacters.Contains(character) ? '_' : character)
                    .ToArray())
            .Trim()
            .TrimEnd('.');

        return string.IsNullOrWhiteSpace(sanitized) ? "未命名" : sanitized;
    }

    private static void TryClose(object? value)
    {
        if (value is null)
        {
            return;
        }

        try
        {
            ((dynamic)value).Close(false);
        }
        catch
        {
            // 主异常优先返回，这里只做尽力清理。
        }
    }

    private static void TryQuit(object? value)
    {
        if (value is null)
        {
            return;
        }

        try
        {
            ((dynamic)value).Quit();
        }
        catch
        {
            // 主异常优先返回，这里只做尽力清理。
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private static void ForceComCleanup()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}

public sealed class WpsBusyException(string message) : Exception(message);
