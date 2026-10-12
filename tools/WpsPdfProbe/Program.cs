using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

if (args.Length != 2)
{
    Console.Error.WriteLine("用法: WpsPdfProbe <输入目录> <PDF输出目录>");
    return 2;
}

var visibleWpsProcesses = Process
    .GetProcesses()
    .Where(process =>
        process.ProcessName.Equals("wps", StringComparison.OrdinalIgnoreCase) ||
        process.ProcessName.Equals("et", StringComparison.OrdinalIgnoreCase) ||
        process.ProcessName.Equals("wpsoffice", StringComparison.OrdinalIgnoreCase))
    .Where(process => process.MainWindowHandle != IntPtr.Zero)
    .Select(process => $"{process.ProcessName} ({process.Id})")
    .ToArray();

if (visibleWpsProcesses.Length > 0)
{
    Console.Error.WriteLine(
        "检测到正在使用的 WPS 窗口。为避免关闭或干扰当前文档，本次转换已取消：");
    foreach (var process in visibleWpsProcesses)
    {
        Console.Error.WriteLine($"- {process}");
    }

    return 3;
}

var inputDirectory = Path.GetFullPath(args[0]);
var outputDirectory = Path.GetFullPath(args[1]);

if (!Directory.Exists(inputDirectory))
{
    Console.Error.WriteLine($"输入目录不存在: {inputDirectory}");
    return 2;
}

Directory.CreateDirectory(outputDirectory);

var supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    ".doc",
    ".docx",
    ".xls",
    ".xlsx"
};

var inputs = Directory
    .EnumerateFiles(inputDirectory)
    .Where(path => supportedExtensions.Contains(Path.GetExtension(path)))
    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
    .ToArray();

var results = new List<ConversionResult>();

foreach (var inputPath in inputs)
{
    var stopwatch = Stopwatch.StartNew();
    var generatedPdfs = new List<GeneratedPdf>();

    try
    {
        var extension = Path.GetExtension(inputPath);
        if (extension.Equals(".doc", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            var outputPath = Path.Combine(
                outputDirectory,
                $"{Path.GetFileNameWithoutExtension(inputPath)}.pdf");
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            ConvertWriterDocument(inputPath, outputPath);
            var output = RequireValidPdf(outputPath);
            generatedPdfs.Add(new GeneratedPdf(
                output.Name,
                null,
                null,
                output.Length));
        }
        else
        {
            var workbookOutputDirectory = Path.Combine(
                outputDirectory,
                Path.GetFileNameWithoutExtension(inputPath));
            generatedPdfs.AddRange(
                ConvertSpreadsheetSheets(inputPath, workbookOutputDirectory));
        }

        stopwatch.Stop();
        if (generatedPdfs.Count == 0)
        {
            throw new InvalidOperationException("WPS 没有生成任何可见工作表 PDF。");
        }

        results.Add(new ConversionResult(
            Path.GetFileName(inputPath),
            true,
            stopwatch.ElapsedMilliseconds,
            new FileInfo(inputPath).Length,
            generatedPdfs.Sum(output => output.OutputBytes),
            generatedPdfs,
            null));

        Console.WriteLine(
            $"成功  {Path.GetFileName(inputPath)}  ->  {generatedPdfs.Count} 份 PDF");
    }
    catch (Exception exception)
    {
        stopwatch.Stop();
        results.Add(new ConversionResult(
            Path.GetFileName(inputPath),
            false,
            stopwatch.ElapsedMilliseconds,
            new FileInfo(inputPath).Length,
            generatedPdfs.Sum(output => output.OutputBytes),
            generatedPdfs,
            exception.GetBaseException().Message));

        Console.Error.WriteLine(
            $"失败  {Path.GetFileName(inputPath)}  ->  {exception.GetBaseException().Message}");
    }
}

var report = new ConversionReport(
    DateTimeOffset.Now,
    Environment.MachineName,
    inputs.Length,
    results.Count(result => result.Success),
    results.Count(result => !result.Success),
    results);

var reportPath = Path.Combine(outputDirectory, "conversion-report.json");
File.WriteAllText(
    reportPath,
    JsonSerializer.Serialize(
        report,
        new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));

Console.WriteLine($"报告  {reportPath}");
return report.FailedCount == 0 ? 0 : 1;

static void ConvertWriterDocument(string inputPath, string outputPath)
{
    object? applicationObject = null;
    object? documentObject = null;

    try
    {
        var applicationType = Type.GetTypeFromProgID("KWPS.Application", throwOnError: true)!;
        applicationObject = Activator.CreateInstance(applicationType)
            ?? throw new InvalidOperationException("无法启动 WPS Writer。");

        dynamic application = applicationObject;
        application.Visible = false;
        application.DisplayAlerts = 0;
        documentObject = application.Documents.Open(inputPath, false, true);

        dynamic document = documentObject;
        document.ExportAsFixedFormat(outputPath, 17);
        document.Close(false);
        documentObject = null;

        application.Quit();
        applicationObject = null;
    }
    finally
    {
        TryClose(documentObject);
        TryQuit(applicationObject);
        ReleaseComObject(documentObject);
        ReleaseComObject(applicationObject);
        ForceComCleanup();
    }
}

static IReadOnlyList<GeneratedPdf> ConvertSpreadsheetSheets(
    string inputPath,
    string outputDirectory)
{
    object? applicationObject = null;
    object? workbookObject = null;
    var generatedPdfs = new List<GeneratedPdf>();

    try
    {
        var applicationType = Type.GetTypeFromProgID("KET.Application", throwOnError: true)!;
        applicationObject = Activator.CreateInstance(applicationType)
            ?? throw new InvalidOperationException("无法启动 WPS 表格。");

        dynamic application = applicationObject;
        application.Visible = false;
        application.DisplayAlerts = false;
        application.ScreenUpdating = false;
        workbookObject = application.Workbooks.Open(inputPath, 0, true);

        dynamic workbook = workbookObject;
        Directory.CreateDirectory(outputDirectory);
        foreach (var stalePdf in Directory.EnumerateFiles(outputDirectory, "*.pdf"))
        {
            File.Delete(stalePdf);
        }

        var worksheetCount = (int)workbook.Worksheets.Count;
        for (var worksheetIndex = 1; worksheetIndex <= worksheetCount; worksheetIndex++)
        {
            object? worksheetObject = null;
            try
            {
                worksheetObject = workbook.Worksheets.Item(worksheetIndex);
                dynamic worksheet = worksheetObject;

                var visibility = worksheet.Visible;
                var isVisible = visibility is bool visible
                    ? visible
                    : Convert.ToInt32(visibility) == -1;
                if (!isVisible)
                {
                    continue;
                }

                var worksheetName = (string)worksheet.Name;
                var outputFileName =
                    $"{worksheetIndex:000}-{SanitizeFileName(worksheetName)}.pdf";
                var outputPath = Path.Combine(outputDirectory, outputFileName);
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }

                worksheet.ExportAsFixedFormat(0, outputPath);
                var output = RequireValidPdf(outputPath);
                generatedPdfs.Add(new GeneratedPdf(
                    Path.Combine(
                        Path.GetFileName(outputDirectory),
                        output.Name),
                    worksheetName,
                    worksheetIndex,
                    output.Length));
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
        return generatedPdfs;
    }
    finally
    {
        TryClose(workbookObject);
        TryQuit(applicationObject);
        ReleaseComObject(workbookObject);
        ReleaseComObject(applicationObject);
        ForceComCleanup();
    }
}

static FileInfo RequireValidPdf(string outputPath)
{
    var output = new FileInfo(outputPath);
    if (!output.Exists || output.Length == 0)
    {
        throw new InvalidOperationException(
            $"WPS 未生成有效 PDF 文件: {outputPath}");
    }

    return output;
}

static string SanitizeFileName(string value)
{
    var invalidCharacters = Path.GetInvalidFileNameChars();
    var sanitized = new string(
            value.Select(character =>
                    invalidCharacters.Contains(character) ? '_' : character)
                .ToArray())
        .Trim()
        .TrimEnd('.');

    return string.IsNullOrWhiteSpace(sanitized) ? "未命名工作表" : sanitized;
}

static void TryClose(object? value)
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
        // 主转换异常优先返回；这里只做尽力清理。
    }
}

static void TryQuit(object? value)
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
        // 主转换异常优先返回；这里只做尽力清理。
    }
}

static void ReleaseComObject(object? value)
{
    if (value is not null && Marshal.IsComObject(value))
    {
        Marshal.FinalReleaseComObject(value);
    }
}

static void ForceComCleanup()
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    GC.WaitForPendingFinalizers();
}

internal sealed record ConversionResult(
    string InputFile,
    bool Success,
    long ElapsedMilliseconds,
    long InputBytes,
    long OutputBytes,
    IReadOnlyList<GeneratedPdf> Outputs,
    string? Error);

internal sealed record GeneratedPdf(
    string OutputFile,
    string? WorksheetName,
    int? WorksheetIndex,
    long OutputBytes);

internal sealed record ConversionReport(
    DateTimeOffset CreatedAt,
    string HostName,
    int TotalCount,
    int SucceededCount,
    int FailedCount,
    IReadOnlyList<ConversionResult> Results);
