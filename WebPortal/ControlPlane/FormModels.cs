namespace WebPortal.ControlPlane
{
    public sealed record FormSummary(string Name, int LatestRevision, string LatestStatus, int? ActiveRevision, DateTime UpdatedAt, string? UpdatedBy);

    public sealed record RevisionInfo(int Id, int Revision, int? BaseRevision, string Status, string Sha256, DateTime CreatedAt, string? CreatedBy, DateTime? PublishedAt, string? PublishedBy);

    public sealed record RevisionContent(RevisionInfo Info, string Content);

    /// <summary>Revision đang có hiệu lực của một form = revision publish có số lớn nhất</summary>
    public sealed record ActiveForm(string Name, int RevisionId, int Revision, string Content);

    public sealed record PackageInfo(int Id, int ProjectId, DateTime CreatedAt, string? CreatedBy, string ManifestSha256, int FormCount);

    public sealed record PackageItem(string Form, int Revision, string Content);

    public sealed record PackageRecord(PackageInfo Info, string ManifestJson, IReadOnlyList<PackageItem> Items);

    /// <summary>Kết quả hàm dựng gói chạy bên trong transaction của kho (có id gói để ghi vào manifest)</summary>
    public sealed record PackageDraft(string ManifestJson, string ManifestSha256, IReadOnlyList<int> RevisionIds);

    public sealed record SyncLogRecord(int ProjectId, string Env, string Event, int? PackageId, string Source, string? Actor, string Outcome, string? Detail);

    public sealed record SyncLogEntry(long Id, DateTime At, int ProjectId, string ProjectCode, string Env, string Event, int? PackageId, string Source, string? Actor, string Outcome, string? Detail);

    public sealed record SyncLogPage(int Total, IReadOnlyList<SyncLogEntry> Items);
}
