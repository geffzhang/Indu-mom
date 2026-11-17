namespace HoneyMOM.Application.Persistence;

public sealed record SchemaCheckResult(IReadOnlyList<string> Problems)
{
    public bool IsValid => Problems.Count == 0;
}
