namespace CccPrint.Models;

public static class ReviewDecisions
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}

public sealed record DraftOutputReview(
    string OutputId,
    string Decision,
    string Note,
    DateTimeOffset UpdatedAtUtc);

public sealed record DraftReviewState(
    int SchemaVersion,
    string BatchId,
    string ReleaseNote,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<DraftOutputReview> Items);

public sealed record PublishResult(
    string Version,
    int FileCount,
    DateTimeOffset PublishedAtUtc);
