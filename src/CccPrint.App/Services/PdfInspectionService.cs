using System.IO;
using System.Security.Cryptography;
using CccPrint.Models;
using UglyToad.PdfPig;

namespace CccPrint.Services;

public sealed class PdfInspectionService
{
    public async Task<PdfInspection> InspectAsync(
        string pdfPath,
        CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(pdfPath);
        if (!file.Exists || file.Length == 0)
        {
            throw new InvalidDataException($"PDF 文件不存在或为空：{pdfPath}");
        }

        string hash;
        await using (var stream = new FileStream(
                         pdfPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         65536,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            hash = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken))
                .ToLowerInvariant();
        }

        try
        {
            using var document = PdfDocument.Open(pdfPath);
            if (document.NumberOfPages <= 0)
            {
                throw new InvalidDataException($"PDF 没有有效页面：{pdfPath}");
            }

            var firstPage = document.GetPage(1);
            var hasMixedPageSizes = false;
            for (var pageNumber = 2;
                 pageNumber <= document.NumberOfPages;
                 pageNumber++)
            {
                var page = document.GetPage(pageNumber);
                if (Math.Abs(page.Width - firstPage.Width) > 1 ||
                    Math.Abs(page.Height - firstPage.Height) > 1)
                {
                    hasMixedPageSizes = true;
                    break;
                }
            }

            return new PdfInspection(
                file.Length,
                hash,
                document.NumberOfPages,
                hasMixedPageSizes);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidDataException(
                $"PDF 解析失败：{pdfPath}",
                exception);
        }
    }
}
