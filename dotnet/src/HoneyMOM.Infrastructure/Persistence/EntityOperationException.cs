namespace HoneyMOM.Infrastructure.Persistence;

public sealed class EntityOperationException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
