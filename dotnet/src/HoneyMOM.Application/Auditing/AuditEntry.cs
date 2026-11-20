namespace HoneyMOM.Application.Auditing;

public sealed record AuditEntry(
    string Action,
    string? Entity = null,
    int? EntityId = null,
    object? Diff = null,
    object? Detail = null,
    string? Target = null,
    string? User = null,
    string? Source = null,
    string? Ip = null,
    string? Kind = null,
    DateTime? Time = null);

public sealed record AuditProblem(int? Sequence, int? Id, string Reason);

public sealed record AuditVerificationResult(
    bool Ok,
    int Checked,
    int Unsealed,
    AuditProblem? Problem);
