using System.Diagnostics;
using System.Drawing.Printing;
using System.IO;
using System.Printing;
using System.Runtime.InteropServices;
using PdfiumViewer;

namespace CccPrint.Client;

public sealed class WindowsPdfPrintService
{
    private static readonly TimeSpan MinimumSubmissionInterval =
        TimeSpan.FromSeconds(3);
    private readonly Action<string, int> _submitPrint;
    private readonly TimeSpan _minimumSubmissionInterval;
    private static readonly object PdfiumLoadLock = new();
    private static nint _pdfiumHandle;

    public WindowsPdfPrintService()
        : this(SubmitToDefaultPrinter, MinimumSubmissionInterval)
    {
    }

    internal WindowsPdfPrintService(
        Action<string, int> submitPrint,
        TimeSpan minimumSubmissionInterval)
    {
        _submitPrint = submitPrint;
        _minimumSubmissionInterval = minimumSubmissionInterval;
    }

    public string GetDefaultPrinterName()
    {
        try
        {
            using var server = new LocalPrintServer();
            return server.DefaultPrintQueue?.FullName ?? "未设置默认打印机";
        }
        catch
        {
            return "未设置默认打印机";
        }
    }

    public bool CanRender(string pdfPath)
    {
        if (!File.Exists(pdfPath))
        {
            return false;
        }

        try
        {
            EnsurePdfiumLoaded();
            using var pdf = PdfDocument.Load(pdfPath);
            if (pdf.PageCount == 0)
            {
                return false;
            }

            using var preview = pdf.Render(0, 128, 128, 96, 96, false);
            return preview.Width == 128 && preview.Height == 128;
        }
        catch
        {
            return false;
        }
    }

    public async Task<int> PrintAsync(
        IReadOnlyList<(string PdfPath, int Copies)> jobs,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var submitted = 0;

        foreach (var job in jobs)
        {
            ValidateJob(job);
            cancellationToken.ThrowIfCancellationRequested();

            var startedAt = Stopwatch.StartNew();
            await Task.Run(
                () => _submitPrint(job.PdfPath, job.Copies),
                cancellationToken);
            submitted++;
            progress?.Report(submitted);

            if (submitted < jobs.Count)
            {
                var remaining = _minimumSubmissionInterval - startedAt.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, cancellationToken);
                }
            }
        }

        return submitted;
    }

    private static void SubmitToDefaultPrinter(string pdfPath, int copies)
    {
        EnsurePdfiumLoaded();
        using var pdf = PdfDocument.Load(pdfPath);
        using var printDocument = pdf.CreatePrintDocument(
            PdfPrintMode.ShrinkToMargin);
        printDocument.DocumentName = Path.GetFileNameWithoutExtension(pdfPath);
        printDocument.PrintController = new StandardPrintController();
        printDocument.PrinterSettings.Copies = checked((short)copies);
        printDocument.PrinterSettings.Collate = true;

        if (!printDocument.PrinterSettings.IsValid)
        {
            throw new InvalidOperationException(
                "Windows 默认打印机不可用，请先检查打印机连接和默认打印机设置。");
        }

        printDocument.Print();
    }

    private static void EnsurePdfiumLoaded()
    {
        if (_pdfiumHandle != 0)
        {
            return;
        }

        lock (PdfiumLoadLock)
        {
            if (_pdfiumHandle != 0)
            {
                return;
            }

            var path = Path.Combine(
                AppContext.BaseDirectory,
                "x64",
                "pdfium.dll");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "打印引擎文件缺失，请重新安装资料打印端。",
                    path);
            }

            _pdfiumHandle = NativeLibrary.Load(path);
        }
    }

    private static void ValidateJob((string PdfPath, int Copies) job)
    {
        if (!File.Exists(job.PdfPath))
        {
            throw new FileNotFoundException(
                "待打印 PDF 不存在。",
                job.PdfPath);
        }

        if (job.Copies is < 1 or > 99)
        {
            throw new ArgumentOutOfRangeException(
                nameof(job),
                "打印份数必须在 1 到 99 之间。");
        }
    }
}
