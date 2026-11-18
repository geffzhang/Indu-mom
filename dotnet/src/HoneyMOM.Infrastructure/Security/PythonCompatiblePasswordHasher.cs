using System.Security.Cryptography;
using System.Text;
using HoneyMOM.Application.Abstractions;

namespace HoneyMOM.Infrastructure.Security;

public sealed class PythonCompatiblePasswordHasher : IPasswordHasher
{
    private const int Iterations = 120_000;
    private const int SaltBytes = 8;
    private const int HashBytes = 32;

    public string Hash(string password)
    {
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(SaltBytes)).ToLowerInvariant();
        var digest = Rfc2898DeriveBytes.Pbkdf2(
            password,
            Encoding.UTF8.GetBytes(salt),
            Iterations,
            HashAlgorithmName.SHA256,
            HashBytes);
        return $"pbkdf2${salt}${Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    public bool Verify(string password, string? storedHash)
    {
        var parts = (storedHash ?? "").Split('$');
        if (parts.Length != 3 || parts[0] != "pbkdf2" || parts[2].Length != HashBytes * 2)
        {
            return false;
        }

        try
        {
            var digest = Rfc2898DeriveBytes.Pbkdf2(
                password,
                Encoding.UTF8.GetBytes(parts[1]),
                Iterations,
                HashAlgorithmName.SHA256,
                HashBytes);
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(Convert.ToHexString(digest).ToLowerInvariant()),
                Encoding.UTF8.GetBytes(parts[2]));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
