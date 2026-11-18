using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using HoneyMOM.Application.Abstractions;
using HoneyMOM.Application.Configuration;

namespace HoneyMOM.Infrastructure.Security;

public sealed class PythonCompatibleTokenService(MomOptions options) : ITokenService
{
    private const long DefaultLifetimeSeconds = 7 * 24 * 60 * 60;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly string _secretPath = Path.Combine(options.DataDirectory, "secret.key");
    private byte[]? _secret;

    public string Create(string username, DateTimeOffset now, TimeSpan? lifetime = null)
    {
        var expiration = (long)(now.ToUnixTimeMilliseconds() / 1000d +
            (lifetime ?? TimeSpan.FromSeconds(DefaultLifetimeSeconds)).TotalSeconds);
        var payload = $"{{\"u\": {SerializePythonString(username)}, \"exp\": {expiration}}}";
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .Replace('+', '-')
            .Replace('/', '_');
        var signature = Sign(body);
        return $"{body}.{signature}";
    }

    public string? ReadUsername(string token, DateTimeOffset now)
    {
        var parts = token.Split('.');
        if (parts.Length != 2 || parts[1].Length != 32)
        {
            return null;
        }

        var expectedSignature = Sign(parts[0]);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expectedSignature),
                Encoding.ASCII.GetBytes(parts[1])))
        {
            return null;
        }

        try
        {
            var base64 = parts[0].Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(base64));
            var payload = document.RootElement;
            var username = payload.GetProperty("u");
            var expiration = payload.GetProperty("exp");
            if (username.ValueKind != JsonValueKind.String ||
                expiration.ValueKind != JsonValueKind.Number ||
                !expiration.TryGetInt64(out var expiresAt) ||
            expiresAt <= now.ToUnixTimeMilliseconds() / 1000d)
            {
                return null;
            }

            return username.GetString();
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private string Sign(string body)
    {
        var digest = HMACSHA256.HashData(GetSecret(), Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(digest).ToLowerInvariant()[..32];
    }

    private byte[] GetSecret()
    {
        if (_secret is not null)
        {
            return _secret;
        }

        if (!File.Exists(_secretPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_secretPath)!);
            var generated = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            try
            {
                using var secretFile = new FileStream(_secretPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(secretFile, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(generated);
            }
            catch (IOException) when (File.Exists(_secretPath))
            {
            }
        }

        _secret = Encoding.UTF8.GetBytes(File.ReadAllText(_secretPath).Trim());
        return _secret;
    }

    private static string SerializePythonString(string value)
    {
        var serialized = JsonSerializer.Serialize(value, JsonOptions);
        var escaped = new StringBuilder(serialized.Length);
        foreach (var character in serialized)
        {
            if (character > 0x7f)
            {
                escaped.Append("\\u").Append(((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                escaped.Append(character);
            }
        }

        return escaped.ToString();
    }
}
