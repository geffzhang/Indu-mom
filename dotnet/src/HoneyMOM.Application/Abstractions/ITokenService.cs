namespace HoneyMOM.Application.Abstractions;

public interface ITokenService
{
    string Create(string username, DateTimeOffset now, TimeSpan? lifetime = null);

    string? ReadUsername(string token, DateTimeOffset now);
}
