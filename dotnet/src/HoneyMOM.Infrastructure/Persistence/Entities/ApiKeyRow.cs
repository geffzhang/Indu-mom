namespace HoneyMOM.Infrastructure.Persistence.Entities;

public sealed class ApiKeyRow
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Prefix { get; set; }
    public string? KeyHash { get; set; }
    public string? Roles { get; set; }
    public string? Perms { get; set; }
    public string? Scope { get; set; }
    public string? IpAllow { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public string? LastUsedIp { get; set; }
    public int? UseCount { get; set; }
    public string? Remark { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? CreatedAt { get; set; }
}
