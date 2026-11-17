namespace HoneyMOM.Infrastructure.Persistence.Entities;

public sealed class SystemUserRow
{
    public int Id { get; set; }
    public string? Username { get; set; }
    public string? Name { get; set; }
    public string? Roles { get; set; }
    public int? Employee { get; set; }
    public bool? Active { get; set; }
    public string? PasswordHash { get; set; }
    public string? Scope { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
