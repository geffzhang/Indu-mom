namespace HoneyMOM.Application.Users;

public sealed record AuthenticationResponse(
    string Token,
    AuthenticationResponseUser User);

public sealed record AuthenticationResponseUser(
    string Username,
    string Name,
    string Roles);

public sealed record AuthenticationOperationResult(
    AuthenticationResponse? Response,
    string? Error)
{
    public bool Succeeded => Response is not null;
}
