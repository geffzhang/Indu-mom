namespace HoneyMOM.Infrastructure.Persistence.Entities;

public sealed class SystemRoleRow
{
    public int Id { get; set; }
    public string? Code { get; set; }
    public string? Name { get; set; }
    public string? Perms { get; set; }
    public string? Scope { get; set; }
    public bool? Builtin { get; set; }
    public string? Remark { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
