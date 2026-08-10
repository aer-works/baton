namespace Aer.Flow.Projection;

public sealed record PendingPermission(
    string PermissionRequestId,
    string WorkerId,
    string VendorTag,
    string ToolName,
    string ToolInputJson,
    string Category,
    DateTimeOffset AskedAt);
