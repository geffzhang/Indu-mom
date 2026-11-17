namespace HoneyMOM.Infrastructure.Persistence.Entities;

public sealed class AuditLogRow
{
    public int Id { get; set; }
    public DateTime? Time { get; set; }
    public string? User { get; set; }
    public string? Kind { get; set; }
    public string? Target { get; set; }
    public string? Detail { get; set; }
    public string? Entity { get; set; }
    public int? EntityId { get; set; }
    public string? Action { get; set; }
    public string? Diff { get; set; }
    public string? Source { get; set; }
    public string? Ip { get; set; }
    public int? Seq { get; set; }
    public string? PrevHash { get; set; }
    public string? Hash { get; set; }
}
