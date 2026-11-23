namespace HoneyMOM.Application.Auditing;

public sealed record AuditRequestValues(string Source, string? Ip);

public static class AuditRequestContext
{
    private static readonly AsyncLocal<AuditRequestValues?> CurrentValue = new();

    public static AuditRequestValues? Current => CurrentValue.Value;

    public static IDisposable Push(string source, string? ip)
    {
        var previous = CurrentValue.Value;
        CurrentValue.Value = new AuditRequestValues(source, ip);
        return new Scope(previous);
    }

    private sealed class Scope(AuditRequestValues? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                CurrentValue.Value = previous;
            }
        }
    }
}
