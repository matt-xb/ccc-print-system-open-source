using System.IO;
using System.Text.Json;
using CccPrint.Models;

namespace CccPrint.Services;

public sealed class DraftReviewService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    private readonly string _draftsRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DraftReviewService(string dataRoot)
    {
        _draftsRoot = Path.Combine(
            Path.GetFullPath(dataRoot),
            "drafts");
    }

    public async Task<DraftReviewState> GetAsync(
        DraftBatch batch,
        CancellationToken cancellationToken = default)
    {
        var path = GetReviewPath(batch.BatchId);
        DraftReviewState? stored = null;
        if (File.Exists(path))
        {
            try
            {
                await using var stream = File.OpenRead(path);
                stored = await JsonSerializer
                    .DeserializeAsync<DraftReviewState>(
                        stream,
                        JsonOptions,
                        cancellationToken);
            }
            catch (JsonException)
            {
                // 损坏的审阅状态按未审阅重建，不影响 PDF 草稿。
            }
            catch (IOException)
            {
                // 文件正在写入时按当前批次重建，下一次操作会再次保存。
            }
        }

        var previous = stored?.Items.ToDictionary(
                           item => item.OutputId,
                           StringComparer.OrdinalIgnoreCase)
                       ?? new Dictionary<string, DraftOutputReview>(
                           StringComparer.OrdinalIgnoreCase);
        var items = batch.Sources
            .Where(source => source.Success)
            .SelectMany(source => source.Outputs)
            .Select(output => previous.TryGetValue(
                    output.OutputId,
                    out var review)
                ? review
                : new DraftOutputReview(
                    output.OutputId,
                    ReviewDecisions.Pending,
                    string.Empty,
                    DateTimeOffset.MinValue))
            .ToArray();

        return new DraftReviewState(
            1,
            batch.BatchId,
            stored?.ReleaseNote ?? string.Empty,
            stored?.UpdatedAtUtc ?? DateTimeOffset.UtcNow,
            items);
    }

    public async Task<DraftReviewState> SetDecisionAsync(
        DraftBatch batch,
        string outputId,
        string decision,
        string note,
        CancellationToken cancellationToken = default)
    {
        if (decision is not (
                ReviewDecisions.Approved or
                ReviewDecisions.Rejected or
                ReviewDecisions.Pending))
        {
            throw new ArgumentException("无效的审阅结论。", nameof(decision));
        }

        var outputs = batch.Sources
            .Where(source => source.Success)
            .SelectMany(source => source.Outputs)
            .ToArray();
        if (!outputs.Any(output => output.OutputId.Equals(
                outputId,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("草稿中不存在指定 PDF。", nameof(outputId));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await GetAsync(batch, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var items = state.Items
                .Select(item => item.OutputId.Equals(
                        outputId,
                        StringComparison.OrdinalIgnoreCase)
                    ? item with
                    {
                        Decision = decision,
                        Note = note.Trim(),
                        UpdatedAtUtc = now
                    }
                    : item)
                .ToArray();
            var updated = state with
            {
                UpdatedAtUtc = now,
                Items = items
            };
            await WriteAsync(updated, cancellationToken);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DraftReviewState> SetReleaseNoteAsync(
        DraftBatch batch,
        string releaseNote,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await GetAsync(batch, cancellationToken);
            var updated = state with
            {
                ReleaseNote = releaseNote.Trim(),
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            await WriteAsync(updated, cancellationToken);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DraftReviewState> SetAllDecisionsAsync(
        DraftBatch batch,
        string decision,
        CancellationToken cancellationToken = default)
    {
        if (decision is not (
                ReviewDecisions.Approved or
                ReviewDecisions.Rejected))
        {
            throw new ArgumentException("无效的选择状态。", nameof(decision));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await GetAsync(batch, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var updated = state with
            {
                UpdatedAtUtc = now,
                Items = state.Items
                    .Select(item => item with
                    {
                        Decision = decision,
                        Note = string.Empty,
                        UpdatedAtUtc = now
                    })
                    .ToArray()
            };
            await WriteAsync(updated, cancellationToken);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DraftReviewState> ApproveSafeOutputsAsync(
        DraftBatch batch,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await GetAsync(batch, cancellationToken);
            var safeIds = batch.Sources
                .Where(source => source.Success)
                .SelectMany(source => source.Outputs)
                .Where(output => !output.RequiresReview)
                .Select(output => output.OutputId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var now = DateTimeOffset.UtcNow;
            var items = state.Items
                .Select(item =>
                    safeIds.Contains(item.OutputId) &&
                    item.Decision == ReviewDecisions.Pending
                        ? item with
                        {
                            Decision = ReviewDecisions.Approved,
                            Note = "系统检查未发现分页或纸张尺寸异常。",
                            UpdatedAtUtc = now
                        }
                        : item)
                .ToArray();
            var updated = state with
            {
                UpdatedAtUtc = now,
                Items = items
            };
            await WriteAsync(updated, cancellationToken);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetReviewPath(string batchId)
    {
        if (Path.GetFileName(batchId) != batchId)
        {
            throw new ArgumentException("批次编号无效。", nameof(batchId));
        }

        return Path.Combine(_draftsRoot, batchId, "review.json");
    }

    private async Task WriteAsync(
        DraftReviewState state,
        CancellationToken cancellationToken)
    {
        var path = GetReviewPath(state.BatchId);
        if (!Directory.Exists(Path.GetDirectoryName(path)))
        {
            throw new DirectoryNotFoundException("PDF 草稿批次不存在。");
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         65536,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                state,
                JsonOptions,
                cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, path, true);
    }
}
