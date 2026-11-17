namespace HoneyMOM.Infrastructure.Persistence.Entities;

public sealed class SettingRow
{
    public int Id { get; set; }
    public string? Key { get; set; }
    public string? Value { get; set; }
    public string? Remark { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
